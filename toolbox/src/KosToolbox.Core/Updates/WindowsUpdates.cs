using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using Microsoft.Win32;

namespace KosToolbox.Core.Updates;

/// <summary>An update Windows Update offers this PC and that isn't installed.</summary>
public sealed record WuUpdate(string Id, string Title, string? Kb, long Size, string Category, bool Hidden, bool Driver, bool Mandatory);

/// <summary>What an install run did: per update, its title and an error (null = installed).</summary>
public sealed record WuInstallResult(IReadOnlyList<(string Title, string? Error)> Updates, bool RebootRequired)
{
    public int Installed => Updates.Count(u => u.Error is null);
}

/// <summary>
/// Windows Update through its own API (the Windows Update Agent, as wuauclt and Settings use it): lists the updates offered, installs
/// the chosen ones and hides or shows them again. KOS keeps Windows Update off, so each operation first lets it run (the
/// wuauserv, BITS and Delivery Optimization services on manual, and the two policies that block access removed), then puts every
/// value back exactly as it was and stops the services it allowed. Administrator only.
/// </summary>
public static class WindowsUpdates
{
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    static readonly string[] Services = { "wuauserv", "bits", "dosvc" };
    static readonly string[] BlockingPolicies = { "DisableWindowsUpdateAccess", "DoNotConnectToWindowsUpdateInternetLocations" };
    // Hidden updates only come back when asked for: IsHidden=0 alone (the default) leaves them out.
    const string Criteria = "IsInstalled=0 and IsHidden=0 or IsInstalled=0 and IsHidden=1";
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<IReadOnlyList<WuUpdate>> SearchAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<WuUpdate>>(session =>
        {
            var found = new List<WuUpdate>();
            foreach (var u in Search(session)) found.Add(Read(u));
            Log.Info($"Windows Update: {found.Count} update(s) offered ({found.Count(f => f.Hidden)} hidden).");
            return found.OrderBy(f => f.Hidden).ThenBy(f => f.Category).ThenBy(f => f.Title).ToList();
        }, ct);

    /// <summary>Downloads and installs the updates one by one, so one failing doesn't stop the others; <paramref name="progress"/> gets each step.</summary>
    public static Task<WuInstallResult> InstallAsync(IReadOnlyCollection<string> ids, IProgress<string> progress, CancellationToken ct = default) =>
        RunAsync(session =>
        {
            var chosen = Search(session).Where(u => ids.Contains(IdOf(u))).ToList();
            dynamic s = session;
            var results = new List<(string, string?)>();
            var reboot = false;
            for (var i = 0; i < chosen.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                dynamic u = chosen[i];
                string title = u.Title;
                try
                {
                    if (!(bool)u.EulaAccepted) u.AcceptEula(); // the user picked it to install, as Settings does on Install
                    dynamic list = Create("Microsoft.Update.UpdateColl");
                    list.Add(u);
                    progress.Report($"Downloading {i + 1} of {chosen.Count}: {title}");
                    dynamic downloader = s.CreateUpdateDownloader();
                    downloader.Updates = list;
                    dynamic downloaded = downloader.Download();
                    int code = downloaded.GetUpdateResult(0).HResult;
                    if ((int)downloaded.ResultCode is not (2 or 3)) throw new InvalidOperationException(Failure("download", code));
                    progress.Report($"Installing {i + 1} of {chosen.Count}: {title}");
                    dynamic installer = s.CreateUpdateInstaller();
                    installer.Updates = list;
                    dynamic installed = installer.Install();
                    code = installed.GetUpdateResult(0).HResult;
                    if ((int)installed.ResultCode is not (2 or 3)) throw new InvalidOperationException(Failure("install", code));
                    reboot |= (bool)installed.RebootRequired;
                    results.Add((title, null));
                    Log.Info($"Windows Update: installed {title}");
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    var error = ex is COMException c ? Describe(c.HResult) : ex.Message;
                    results.Add((title, error));
                    Log.Warn($"Windows Update: {title} not installed: {error}");
                }
            }
            return new WuInstallResult(results, reboot);
        }, ct);

    /// <summary>Hides the updates (Windows Update no longer offers them) or shows them again. A mandatory update can't be hidden.</summary>
    public static Task<int> SetHiddenAsync(IReadOnlyCollection<string> ids, bool hidden, CancellationToken ct = default) =>
        RunAsync(session =>
        {
            var changed = 0;
            foreach (dynamic u in Search(session).Where(u => ids.Contains(IdOf(u))))
            {
                if ((bool)u.IsHidden == hidden || (hidden && (bool)u.IsMandatory)) continue;
                u.IsHidden = hidden;
                changed++;
                Log.Info($"Windows Update: {(hidden ? "hid" : "showed again")} {u.Title}");
            }
            return changed;
        }, ct);

    /// <summary>An operation is running: closing the toolbox now would skip putting the services and policies back.</summary>
    public static bool Busy => Gate.CurrentCount == 0;

    static async Task<T> RunAsync<T>(Func<object, T> work, CancellationToken ct)
    {
        if (!Elevation.IsAdmin) throw new UnauthorizedAccessException("Windows Update needs the toolbox running as administrator.");
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var changed = new List<Changed>();
                T result;
                try
                {
                    Allow(changed);
                    result = work((object)Create("Microsoft.Update.Session"));
                }
                catch (COMException ex)
                {
                    Restore(changed, quiet: true);
                    throw new InvalidOperationException(Describe(ex.HResult), ex);
                }
                catch
                {
                    Restore(changed, quiet: true);
                    throw;
                }
                Restore(changed, quiet: false);
                return result;
            }, CancellationToken.None).ConfigureAwait(false); // started work always restores the values, even when cancelled
        }
        finally { Gate.Release(); }
    }

    /// <summary>A value changed for the operation: what it was and what was written in its place.</summary>
    readonly record struct Changed(RegistrySetting Setting, string? Before, string? Written);

    /// <summary>
    /// Lets Windows Update run: removes the policies that block it and puts its disabled services on manual (they start on demand).
    /// Each change is recorded first, so it can be undone even if a later one fails.
    /// shortcut: if the toolbox is killed here (closing is refused while <see cref="Busy"/>), the policies stay removed until the user sets Windows Update again; the services go back to disabled at the next startup in the Off mode (bin\3\windows-update-policy.cmd).
    /// </summary>
    static void Allow(List<Changed> changed)
    {
        foreach (var name in BlockingPolicies)
            Change(new RegistrySetting(Hive.LocalMachine, PolicyKey, name, RegistryValueKind.DWord), null, changed);
        foreach (var service in Services)
        {
            var s = RegistrySetting.ServiceStart(service);
            if (s.Read() == "4") Change(s, "3", changed);
        }
    }

    static void Change(RegistrySetting s, string? value, List<Changed> changed)
    {
        var before = s.Read();
        if (s.ValueEquals(before, value)) return;
        changed.Add(new Changed(s, before, value));
        s.Write(value);
        Log.Info($"Windows Update: {s.Display} {before ?? "(absent)"} → {value ?? "(absent)"} for this operation.");
    }

    /// <summary>
    /// Writes every value back and checks it by reading it again, then stops the services that were disabled. A value someone changed
    /// in the meantime (the Windows page set another Windows Update mode) is left as they set it.
    /// </summary>
    static void Restore(List<Changed> changed, bool quiet)
    {
        var problems = new List<string>();
        var stop = new List<string>();
        foreach (var (s, before, written) in Enumerable.Reverse(changed))
        {
            try
            {
                if (!s.ValueEquals(s.Read(), written)) { Log.Info($"Windows Update: {s.Display} was changed during the operation; left as it is."); continue; }
                s.Write(before);
                if (!s.ValueEquals(s.Read(), before)) problems.Add(s.Display);
                else if (s.Name == "Start" && before == "4") stop.Add(Path.GetFileName(s.Path));
            }
            catch (Exception ex) { problems.Add($"{s.Display}: {ex.Message}"); }
        }
        // Disabled first, then stopped: a service started on demand in between can't start again.
        foreach (var name in stop)
        {
            try
            {
                ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), $"stop {ProcessRunner.Quote(name)}", TimeSpan.FromSeconds(30));
                if (!Stopped(name)) Log.Warn($"Windows Update: {name} is disabled again but still running; it stops at the next restart.");
            }
            catch (Exception ex) { Log.Warn($"Windows Update: {name} wasn't stopped: {ex.Message}"); }
        }
        if (problems.Count == 0) { if (changed.Count > 0) Log.Info("Windows Update: services and policies back as they were."); return; }
        var text = $"These values couldn't be put back: {string.Join("; ", problems)}. Set Windows › System › Windows Update again.";
        Log.Error($"Windows Update: {text}");
        if (!quiet) throw new InvalidOperationException(text);
    }

    /// <summary>Waits up to 15 s for the service to report stopped.</summary>
    static bool Stopped(string name)
    {
        for (var i = 0; i < 30; i++)
        {
            if (Interop.ServiceControl.Enumerate(Interop.ServiceControl.TypeWin32).FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { State: not 1 })
                return true; // SERVICE_STOPPED (1), or not listed
            Thread.Sleep(500);
        }
        return false;
    }

    static List<object> Search(object updateSession)
    {
        dynamic session = updateSession;
        session.ClientApplicationID = "KOS Toolbox";
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search(Criteria);
        if ((int)result.ResultCode is not (2 or 3)) throw new InvalidOperationException($"The search didn't finish (result {(int)result.ResultCode}).");
        var list = new List<object>();
        for (var i = 0; i < (int)result.Updates.Count; i++) list.Add((object)result.Updates.Item(i));
        return list;
    }

    static string IdOf(dynamic update) => update.Identity.UpdateID;

    static WuUpdate Read(dynamic u)
    {
        string id = u.Identity.UpdateID, title = u.Title;
        string? kb = (int)u.KBArticleIDs.Count > 0 ? $"KB{u.KBArticleIDs.Item(0)}" : null;
        string category = (int)u.Categories.Count > 0 ? u.Categories.Item(0).Name : "";
        return new WuUpdate(id, title, kb, (long)(decimal)u.MaxDownloadSize, category, (bool)u.IsHidden, (int)u.Type == 2, (bool)u.IsMandatory);
    }

    static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException($"{progId} isn't registered: the Windows Update Agent is missing."))!;

    static string Failure(string step, int hresult) => $"The {step} failed: {Describe(hresult)}";

    /// <summary>The Windows Update errors a KOS PC runs into, in words; others as their code.</summary>
    internal static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x8024402C or 0x80244022 or 0x8024401C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 => "Windows Update's servers can't be reached (no internet, a firewall or the hosts file blocks them).",
        0x8024002E => "A policy blocks access to Windows Update.",
        0x80070422 => "A service Windows Update needs is disabled.",
        0x8024001E => "The operation was cancelled.",
        0x80240016 => "Another install is running; try again when it finishes.",
        0x80240022 or 0x80240020 => "Windows Update refused it (it must run as administrator in the signed-in session).",
        0x8024200B => "The update failed to install; the details are in Windows Update's own log.",
        var code => $"Windows Update error 0x{code:X8}.",
    };
}
