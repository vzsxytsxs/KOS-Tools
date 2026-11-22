using System.Text.Json;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
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

    /// <summary>
    /// The saved config; a default (off, no rules) when there's no file. Throws when the file is there but refused, unreadable (a
    /// user can hold it open: users can read it) or not a config: a decision taken on a default could leave neither firewall on.
    /// </summary>
    public static FirewallConfig Load() => Read(ConfigPath, Problem);

    internal static FirewallConfig Read(string path, Func<string?> problem)
    {
        if (!File.Exists(path)) return new FirewallConfig();
        if (problem() is { } refused) throw new InvalidOperationException(refused);
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), KosJson.Default.FirewallConfig)
                ?? throw new InvalidDataException("The file holds no config.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException($"KOS Firewall's config ({path}) can't be read: {ex.Message}", ex);
        }
    }

    /// <summary>Whether KOS Firewall is on, as saved; an unreadable config counts as on (refuse rather than guess). Replaceable in tests.</summary>
    public static Func<bool> IsOn { get; set; } = () => { try { return Load().Enabled; } catch { return true; } };

    /// <summary>
    /// Why a service start change is refused: the Base Filtering Engine disabled while KOS Firewall is on (its boot-time filters
    /// would stay with nothing to remove them). Null otherwise. Every page that changes a service's start type asks this.
    /// </summary>
    public static string? ServiceStartRefused(string service, int start) =>
        start == 4 && string.Equals(service, "BFE", StringComparison.OrdinalIgnoreCase) && IsOn() ? Txt.FirewallToggleRefused : null;

    /// <summary>Null if the folder and file are trustworthy (not links, owned by SYSTEM/Administrators).</summary>
    public static string? Problem() =>
        GpuTuner.FolderProblem(AppPaths.TheKosRoot, Directory) ?? (File.Exists(ConfigPath) ? ToolDiscovery.OwnerProblem(ConfigPath) : null);

    static void Save(FirewallConfig config)
    {
        GpuTuner.PrepareDirectory(Directory);
        File.Delete(ConfigPath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(ConfigPath, config, KosJson.Default.FirewallConfig);
    }

    /// <summary>
    /// The installed KOS programs (always allowed out). Only the Program Files copies: a copy running from a folder its user can
    /// write would make that path an allow above every block rule, for whatever is put there later.
    /// </summary>
    public static IEnumerable<string> KosPrograms()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KOS");
        yield return Path.Combine(root, "toolbox", "KosToolbox.exe");
        yield return Path.Combine(root, "governor", "KosGovernor.exe");
        yield return Path.Combine(root, "engine", "KosEngine.exe");
    }

    /// <summary>Why KOS Firewall can't run here (the Base Filtering Engine is disabled or stopped), or null.</summary>
    public static string? BfeProblem()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BFE");
        if (key?.GetValue("Start") is 4) return "The Base Filtering Engine service is disabled (Windows › Security › Firewall set to Disabled). Set it to Enabled and restart first.";
        return ServiceControl.State("BFE") == ServiceControl.StateRunning ? null : "The Base Filtering Engine service isn't running.";
    }

    /// <summary>KOS's filters currently in the filter engine (persistent, boot-time and until restart).</summary>
    public static List<WfpFilterInfo> Filters()
    {
        using var s = WfpSession.Open(dynamic: false);
        return s.Filters();
    }

    /// <summary>The names of KOS's filters in the filter engine (which rules until restart still hold: <see cref="FirewallPolicy.Holds"/>).</summary>
    public static HashSet<string> FilterNames() => Filters().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

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
        FirewallPolicy.DropExpired(config, FilterNames(), new HashSet<string>()); // KOS Firewall was off, or is being redone: only what still holds
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
    /// then every KOS filter, the sublayer and the provider are deleted. Returns how many filters there were. It's the way back, so
    /// it needs neither the lock nor a readable config: with the config unreadable every profile goes back on and the config is
    /// left as it is (saving would drop its rules). Callers read Windows Firewall back to say what is on.
    /// <paramref name="strict"/> (the Windows page's Firewall choice, which disables BFE right after): takes the lock like any
    /// change, a profile that doesn't come back on throws before anything is removed, and a filter left in the engine throws.
    /// </summary>
    public static int TurnOff(bool restoreWindows, bool strict = false)
    {
        using var hold = Hold(required: strict);
        FirewallConfig? config;
        try { config = Load(); }
        catch (Exception ex) { Log.Error("KOS Firewall: turning off with the config unreadable; every Windows Firewall profile goes back on", ex); config = null; }
        var restore = config?.RestoreProfiles ?? [.. WindowsFirewall.Profiles];
        if (restoreWindows && restore.Count > 0)
            try { WindowsFirewall.Set(on: true, restore); }
            catch (Exception ex) when (!strict) { Log.Error("Turning Windows Firewall back on", ex); }
        int removed;
        using (var s = WfpSession.Open(dynamic: false))
        {
            removed = s.RemoveEverything();
            if (strict && s.Filters().Count is > 0 and var left) throw new InvalidOperationException($"KOS Firewall: {left} filter(s) are still in the filter engine.");
        }
        if (config is not null && (config.Enabled || config.RestoreProfiles.Count > 0))
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
        var before = config.Rules.Select(r => r.Id).ToHashSet();
        change(config);
        config.Enabled = enabled; // only TurnOn/TurnOff switch it
        FirewallPolicy.DropExpired(config, enabled ? FilterNames() : [], config.Rules.Select(r => r.Id).Where(id => !before.Contains(id)).ToHashSet());
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

    /// <summary>
    /// The machine-wide lock every change takes (an abandoned one, from a process that died, is taken over). Any user can create or
    /// hold a Global\ mutex, so turning off (<paramref name="required"/> false) goes ahead without it after a short wait: removing
    /// the filters and turning Windows Firewall on is safe to do twice. Shortcut: a user holding it still blocks every other change
    /// (never the way out); an admin-only lock would close that.
    /// </summary>
    static IDisposable? Hold(bool required = true)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            bool taken;
            try { taken = mutex.WaitOne(TimeSpan.FromSeconds(required ? 30 : 5)); }
            catch (AbandonedMutexException) { taken = true; }
            if (taken) return new Release(mutex);
            throw new TimeoutException("Another KOS Firewall change is still running.");
        }
        catch (Exception ex) when (!required)
        {
            mutex?.Dispose();
            Log.Warn($"KOS Firewall: turning off without the change lock ({ex.Message}).");
            return null;
        }
        catch { mutex?.Dispose(); throw; }
    }

    sealed class Release(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
