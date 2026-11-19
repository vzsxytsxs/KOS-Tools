using KosToolbox.Core.Gpu;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Firewall;

/// <summary>What an apply installed and the rules it left out.</summary>
public sealed record FirewallApplyResult(int Filters, IReadOnlyList<string> Skipped);

/// <summary>
/// KOS Firewall: the config (admin-only, %ProgramData%\TheKOS\Firewall) and the filters built from it. The toolbox and KOS Engine
/// both change it, always through here and under one machine-wide mutex, so an Allow from a popup and a change on the page
/// never interleave. Everything needs administrator. Turning it on puts KOS's filters in place first and only then turns the
/// Windows Firewall profiles off; turning it off does the reverse, so there's never a moment with neither.
/// </summary>
public static class KosFirewall
{
    public static string Directory => Path.Combine(AppPaths.TheKosRoot, "Firewall");
    public static string ConfigPath => Path.Combine(Directory, "config.json");
    const string MutexName = @"Global\KosFirewall.Apply";

    /// <summary>The saved config; a default (off, no rules) with no file or with the folder/file refused.</summary>
    public static FirewallConfig Load() =>
        File.Exists(ConfigPath) && Problem() is null ? JsonFile.Load(ConfigPath, KosJson.Default.FirewallConfig) : new FirewallConfig();

    /// <summary>Null if the folder and file are trustworthy (not links, owned by SYSTEM/Administrators).</summary>
    public static string? Problem() =>
        GpuTuner.FolderProblem(AppPaths.TheKosRoot, Directory) ?? (File.Exists(ConfigPath) ? ToolDiscovery.OwnerProblem(ConfigPath) : null);

    static void Save(FirewallConfig config)
    {
        GpuTuner.PrepareDirectory(Directory);
        File.Delete(ConfigPath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(ConfigPath, config, KosJson.Default.FirewallConfig);
    }

    /// <summary>The installed KOS programs (always allowed out), plus the one running this code.</summary>
    public static IEnumerable<string> KosPrograms()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KOS");
        yield return Path.Combine(root, "toolbox", "KosToolbox.exe");
        yield return Path.Combine(root, "governor", "KosGovernor.exe");
        yield return Path.Combine(root, "engine", "KosEngine.exe");
        if (Environment.ProcessPath is { } self) yield return self;
    }

    /// <summary>Why KOS Firewall can't run here (the Base Filtering Engine is disabled or stopped), or null.</summary>
    public static string? BfeProblem()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BFE");
        if (key?.GetValue("Start") is 4) return "The Base Filtering Engine service is disabled (Windows › Security › Windows Firewall turned off). Turn it on and restart first.";
        return ServiceControl.State("BFE") == ServiceControl.StateRunning ? null : "The Base Filtering Engine service isn't running.";
    }

    /// <summary>KOS's filters currently in the filter engine (persistent and boot-time).</summary>
    public static List<WfpFilterInfo> Filters()
    {
        using var s = WfpSession.Open(dynamic: false);
        return s.Filters();
    }

    /// <summary>Turns KOS Firewall on: filters from the config, then the Windows Firewall profiles off (remembered, to turn them on again).</summary>
    public static FirewallApplyResult TurnOn()
    {
        using var hold = Hold();
        if (BfeProblem() is { } problem) throw new InvalidOperationException(problem);
        var config = Load();
        if (!config.Enabled)
        {
            var state = WindowsFirewall.State();
            config.RestoreProfiles = WindowsFirewall.Profiles.Where((_, i) => state[i]).ToList();
        }
        config.Enabled = true;
        FirewallApplyResult result;
        try
        {
            result = Apply(config);
            Save(config);
        }
        catch
        {
            // After the commit, a failed read-back or save would leave the catch-all block in place with the config saying off
            // (no popups, the page showing Off): take everything out again so the PC is as it was.
            try { using var s = WfpSession.Open(dynamic: false); s.RemoveEverything(); }
            catch (Exception cleanup) { Log.Error("KOS Firewall: removing the filters after a failed turn-on", cleanup); }
            throw;
        }
        Log.Info($"KOS Firewall on: {result.Filters} filters, {result.Skipped.Count} rule(s) left out.");
        if (WindowsFirewall.AnyOn) WindowsFirewall.Set(on: false);
        return result;
    }

    /// <summary>
    /// Turns KOS Firewall off: the Windows Firewall profiles it turned off go back on first (when <paramref name="restoreWindows"/>),
    /// then every KOS filter, the sublayer and the provider are deleted. Returns how many filters there were.
    /// </summary>
    public static int TurnOff(bool restoreWindows)
    {
        using var hold = Hold();
        var config = Load();
        if (restoreWindows && config.RestoreProfiles.Count > 0)
            try { WindowsFirewall.Set(on: true, config.RestoreProfiles); }
            catch (Exception ex) { Log.Error("Turning Windows Firewall back on", ex); }
        int removed;
        using (var s = WfpSession.Open(dynamic: false)) removed = s.RemoveEverything();
        if (config.Enabled || config.RestoreProfiles.Count > 0)
        {
            config.Enabled = false;
            config.RestoreProfiles = new();
            Save(config);
        }
        Log.Info($"KOS Firewall off: {removed} filter(s) removed.");
        return removed;
    }

    /// <summary>
    /// Changes the rules (and options) through <paramref name="change"/> on the saved config, saves it and, when KOS Firewall is
    /// on, rebuilds its filters. Returns the apply result (null when it's off).
    /// </summary>
    public static FirewallApplyResult? Change(Action<FirewallConfig> change)
    {
        using var hold = Hold();
        var config = Load();
        var enabled = config.Enabled;
        change(config);
        config.Enabled = enabled; // only TurnOn/TurnOff switch it
        var result = enabled ? Apply(config) : null;
        Save(config);
        return result;
    }

    /// <summary>Rebuilds KOS's filters from the config in one transaction: all or nothing.</summary>
    static FirewallApplyResult Apply(FirewallConfig config)
    {
        var plan = FirewallPolicy.Build(config, KosPrograms(), File.Exists, FirewallIdentity.ServiceSid("Dnscache"));
        foreach (var skipped in plan.Skipped) Log.Warn("KOS Firewall rule left out: " + skipped);
        using var s = WfpSession.Open(dynamic: false);
        var old = s.Filters();
        s.Transaction(() =>
        {
            foreach (var f in old) s.Remove(f.Id);
            s.EnsureProvider();
            foreach (var f in plan.Filters) s.Add(f);
        });
        var count = s.Filters().Count;
        if (count != plan.Filters.Count) throw new InvalidOperationException($"The filter engine holds {count} KOS filters; {plan.Filters.Count} were added.");
        return new FirewallApplyResult(count, plan.Skipped);
    }

    /// <summary>The machine-wide lock every change takes (an abandoned one, from a process that died, is taken over).</summary>
    static IDisposable Hold()
    {
        var mutex = new Mutex(false, MutexName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Another KOS Firewall change is still running."); }
        catch (AbandonedMutexException) { }
        catch { mutex.Dispose(); throw; }
        return new Release(mutex);
    }

    sealed class Release(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
