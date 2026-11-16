using System.Runtime.InteropServices;
using System.Text.Json;

namespace KosGovernor.Core;

/// <summary>CPU use of each process between two snapshots, as a share of the whole machine (every logical processor = 100 %).</summary>
public sealed class CpuUsage
{
    readonly Dictionary<(int Pid, long Create), long> _times = new();
    readonly Dictionary<(int Pid, long Create), double> _percent = new();
    long _lastMs;

    public double Percent(ProcessEntry p) => _percent.GetValueOrDefault((p.Pid, p.CreateTime));

    /// <param name="nowMs">Environment.TickCount64 (passed in so tests can choose the time).</param>
    public void Update(IReadOnlyList<ProcessEntry> snapshot, long nowMs, int cpus)
    {
        var elapsed = _lastMs == 0 ? 0 : nowMs - _lastMs;
        _lastMs = nowMs;
        _percent.Clear();
        var seen = new HashSet<(int, long)>();
        foreach (var p in snapshot)
        {
            if (p.Pid == 0) continue; // the idle "process"
            var key = (p.Pid, p.CreateTime);
            seen.Add(key);
            // CpuTime is in 100 ns units: elapsed ms × 10 000 per processor.
            if (elapsed > 0 && _times.TryGetValue(key, out var before))
                _percent[key] = Math.Clamp((p.CpuTime - before) / (elapsed * 10_000.0 * Math.Max(1, cpus)) * 100, 0, 100);
            _times[key] = p.CpuTime;
        }
        foreach (var k in _times.Keys.Where(k => !seen.Contains(k)).ToList()) _times.Remove(k);
    }
}

/// <summary>How long a condition has held for each process, from the first snapshot where it was true.</summary>
internal sealed class Since
{
    readonly Dictionary<(int, long), long> _since = new();

    /// <returns>Milliseconds the condition has held; -1 when it doesn't hold now.</returns>
    public long Track((int, long) key, bool holds, long nowMs)
    {
        if (!holds) { _since.Remove(key); return -1; }
        if (!_since.TryGetValue(key, out var start)) _since[key] = start = nowMs;
        return nowMs - start;
    }

    public void Prune(IReadOnlySet<(int, long)> alive)
    {
        foreach (var k in _since.Keys.Where(k => !alive.Contains(k)).ToList()) _since.Remove(k);
    }
}

/// <summary>
/// CPU limiter rules (<see cref="ProcessRule.CpuLimit"/>): a program above the limit for long enough loses its highest-numbered logical
/// processors for a while, then gets its affinity back (only if nobody changed it in the meantime).
/// shortcut: not saved to disk, so after an engine crash a limited program keeps the reduced affinity until it restarts.
/// </summary>
public sealed class CpuLimiter
{
    readonly Since _over = new();
    readonly Dictionary<(int Pid, long Create), (ulong Before, ulong Set, long UntilMs, string Exe)> _limited = new();

    public void Tick(IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, ProcessRule?> rule, CpuUsage usage, long nowMs)
    {
        var alive = new HashSet<(int, long)>();
        foreach (var p in snapshot)
        {
            var key = (p.Pid, p.CreateTime);
            alive.Add(key);
            if (_limited.TryGetValue(key, out var held))
            {
                if (nowMs < held.UntilMs) continue;
                _limited.Remove(key);
                if (Affinity.Swap(p.Pid, p.CreateTime, held.Set, held.Before)) Log.Info($"CPU limiter: {held.Exe} (pid {p.Pid}) has all its processors back.");
                continue;
            }
            if (rule(p)?.CpuLimit is not { } limit) { _over.Track(key, false, nowMs); continue; }
            if (_over.Track(key, usage.Percent(p) >= limit.Percent, nowMs) < limit.OverSeconds * 1000L) continue;
            _over.Track(key, false, nowMs);
            if (Affinity.Read(p.Pid, p.CreateTime) is not { } before || Without(before, limit.Cores) is not { } reduced) continue;
            if (!Affinity.Swap(p.Pid, p.CreateTime, before, reduced)) continue;
            _limited[key] = (before, reduced, nowMs + limit.ForSeconds * 1000L, p.Name);
            Log.Info($"CPU limiter: {p.Name} (pid {p.Pid}) used {usage.Percent(p):0} % for {limit.OverSeconds} s; {limit.Cores} processor(s) taken away for {limit.ForSeconds} s.");
        }
        foreach (var k in _limited.Keys.Where(k => !alive.Contains(k)).ToList()) _limited.Remove(k);
        _over.Prune(alive);
    }

    /// <summary>The mask without its <paramref name="cores"/> highest processors, always leaving one; null when nothing can be taken.</summary>
    internal static ulong? Without(ulong mask, int cores)
    {
        var result = mask;
        for (var i = 0; i < cores && System.Numerics.BitOperations.PopCount(result) > 1; i++)
            result &= ~(1UL << (63 - System.Numerics.BitOperations.LeadingZeroCount(result)));
        return result == mask ? null : result;
    }

    /// <summary>Engine exiting: gives the processors back.</summary>
    public void Release()
    {
        foreach (var ((pid, create), held) in _limited) Affinity.Swap(pid, create, held.Set, held.Before);
        _limited.Clear();
    }
}

static class Affinity
{
    public static ulong? Read(int pid, long created)
    {
        using var h = ProcessControl.Open(pid, Native.ProcessQueryLimited);
        return h is not null && Same(h, created) && Native.GetProcessAffinityMask(h, out var mask, out _) ? (ulong)mask : null;
    }

    /// <summary>Sets <paramref name="to"/> only while the affinity is still <paramref name="from"/> and the pid still belongs to that process.</summary>
    public static bool Swap(int pid, long created, ulong from, ulong to)
    {
        using var h = ProcessControl.Open(pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
        return h is not null && Same(h, created) && Native.GetProcessAffinityMask(h, out var now, out _) && (ulong)now == from
               && Native.SetProcessAffinityMask(h, (UIntPtr)to);
    }

    internal static bool Same(Microsoft.Win32.SafeHandles.SafeProcessHandle h, long created) =>
        Native.GetProcessTimes(h, out var creation, out _, out _, out _) && creation == created;
}

/// <summary>
/// While gamemode is on: a background program that keeps using a lot of CPU drops from Normal to Below normal priority, and goes back once it
/// has been quiet for a while, comes to the front, or gamemode turns off. Saved in the gamemode state, so a crash is undone at the next start.
/// </summary>
public sealed class Restraint
{
    const double HighPercent = 15, QuietPercent = 5;
    const long HighMs = 6_000, QuietMs = 15_000;
    const uint Normal = 0x20, BelowNormal = 0x4000;
    readonly Since _high = new(), _quiet = new();

    public void Tick(GamemodeSystemState state, IReadOnlyList<ProcessEntry> snapshot, CpuUsage usage, Func<ProcessEntry, bool> eligible, long nowMs)
    {
        var front = Native.ForegroundPid();
        var frontName = front == 0 ? null : snapshot.FirstOrDefault(p => p.Pid == front)?.Name;
        var alive = new HashSet<(int, long)>();
        var changed = false;
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        foreach (var p in snapshot)
        {
            var key = (p.Pid, p.CreateTime);
            alive.Add(key);
            var inFront = frontName is not null && p.Name.Equals(frontName, StringComparison.OrdinalIgnoreCase);
            var i = state.Restrained.FindIndex(t => t.Pid == p.Pid && t.Create == p.CreateTime);
            if (i >= 0)
            {
                if (!inFront && _quiet.Track(key, usage.Percent(p) < QuietPercent, nowMs) < QuietMs) continue;
                Release(p.Pid, p.CreateTime);
                state.Restrained.RemoveAt(i);
                _quiet.Track(key, false, nowMs);
                changed = true;
                Log.Info($"Gamemode: {p.Name} (pid {p.Pid}) back to Normal priority.");
                continue;
            }
            if (inFront || p.Pid is 0 or 4 || p.Pid == Environment.ProcessId || !eligible(p)) { _high.Track(key, false, nowMs); continue; }
            if (_high.Track(key, usage.Percent(p) >= HighPercent, nowMs) < HighMs) continue;
            _high.Track(key, false, nowMs);
            // Windows' own (Windows folder) and ones without a readable path stay as they are.
            if (ProcessControl.ImagePath(p.Pid) is not { } path || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
            if (!SwapClass(p.Pid, p.CreateTime, Normal, BelowNormal)) continue;
            state.Restrained.Add(new ThrottledProcess { Pid = p.Pid, Create = p.CreateTime });
            changed = true;
            Log.Info($"Gamemode: {p.Name} (pid {p.Pid}) used {usage.Percent(p):0} % of the CPU in the background; Below normal priority until it calms down.");
        }
        if (state.Restrained.RemoveAll(t => !alive.Contains((t.Pid, t.Create))) > 0) changed = true;
        _high.Prune(alive);
        _quiet.Prune(alive);
        if (changed) GamemodeSystem.Save(state);
    }

    /// <summary>Back to Normal, only if it is still Below normal (the program or the user may have changed it).</summary>
    public static bool Release(int pid, long created) => SwapClass(pid, created, BelowNormal, Normal);

    static bool SwapClass(int pid, long created, uint from, uint to)
    {
        using var h = ProcessControl.Open(pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
        return h is not null && Affinity.Same(h, created) && Native.GetPriorityClass(h) == from && Native.SetPriorityClass(h, to);
    }
}

/// <summary>What the rule extras changed, so an engine that crashed undoes it at the next start.</summary>
public sealed class RuleState
{
    public Guid? PlanBefore { get; set; }
    public Guid? PlanSet { get; set; }
    /// <summary>Screen (GDI name) set to a temporary mode by <see cref="ProcessRule.DisplayMode"/>.</summary>
    public string? DisplayGdi { get; set; }
}

/// <summary>
/// Power plan and screen mode per program (<see cref="ProcessRule.PowerPlan"/>, <see cref="ProcessRule.DisplayMode"/>): the first running program
/// with one decides; when none runs, what was there before comes back, but only if it is still as the engine left it.
/// </summary>
public sealed class RuleExtras
{
    readonly RuleState _state = new();
    string? _failedDisplay, _heldDisplay;

    static string StatePath => Path.Combine(ConfigStore.Directory, "rule-state.json");

    /// <param name="gamemodePlan">Gamemode switches the plan itself: the rules leave the plan alone meanwhile.</param>
    public void Tick(IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, ProcessRule?> rule, bool gamemodePlan)
    {
        ProcessRule? plan = null, display = null;
        foreach (var p in snapshot)
        {
            if (rule(p) is not { } r) continue;
            if (plan is null && r.PowerPlan is not null) plan = r;
            if (display is null && r.DisplayMode is not null) display = r;
        }
        if (!gamemodePlan) GamemodeSystem.Step("power plan rule", () => SyncPlan(plan));
        GamemodeSystem.Step("screen mode rule", () => SyncDisplay(display));
    }

    void SyncPlan(ProcessRule? rule)
    {
        if (rule?.PowerPlan is { } want)
        {
            if (_state.PlanSet == want) return;
            var active = GamemodeSystem.Power.Active();
            if (_state.PlanSet is null)
            {
                if (active == want) return;
                _state.PlanBefore = active;
            }
            else if (active != _state.PlanSet) _state.PlanBefore = active; // the user changed it meanwhile: that is what comes back
            _state.PlanSet = want;
            Save();
            GamemodeSystem.Power.Activate(want);
            Log.Info($"Power plan {want} while {rule.Exe} runs (was {_state.PlanBefore}).");
        }
        else if (_state.PlanSet is { } set)
        {
            if (GamemodeSystem.Power.Active() == set && _state.PlanBefore is { } before)
            {
                GamemodeSystem.Power.Activate(before);
                Log.Info($"Power plan {before} restored.");
            }
            (_state.PlanSet, _state.PlanBefore) = (null, null);
            Save();
        }
    }

    void SyncDisplay(ProcessRule? rule)
    {
        if (rule?.DisplayMode is { } want)
        {
            var text = want.ToString();
            if (_failedDisplay == text || _heldDisplay == text && _state.DisplayGdi is not null) return;
            var primary = KosToolbox.Core.Gpu.WindowsDisplays.List().FirstOrDefault(d => d.Primary);
            if (primary is null) return;
            if (primary.Width == want.Width && primary.Height == want.Height && (want.RefreshHz is null || primary.Hz == want.RefreshHz)) return;
            _state.DisplayGdi = primary.GdiName;
            Save();
            try
            {
                KosToolbox.Core.Gpu.WindowsDisplays.SetTemporaryMode(primary.GdiName, want.Width, want.Height, want.RefreshHz);
                _heldDisplay = text;
                Log.Info($"Screen {primary.GdiName}: {text} while {rule.Exe} runs.");
            }
            catch (Exception ex)
            {
                _failedDisplay = text; // tried once per mode: retrying every interval would make the screen flicker
                Log.Warn($"Screen mode {text} for {rule.Exe} wasn't applied: {ex.Message}");
            }
        }
        else
        {
            (_failedDisplay, _heldDisplay) = (null, null);
            if (_state.DisplayGdi is not { } gdi) return;
            KosToolbox.Core.Gpu.WindowsDisplays.ResetTemporaryMode(gdi);
            Log.Info($"Screen {gdi}: back to its saved mode.");
            _state.DisplayGdi = null;
            Save();
        }
    }

    /// <summary>Engine exiting: the plan and the screen mode go back.</summary>
    public void Release()
    {
        GamemodeSystem.Step("power plan rule", () => SyncPlan(null));
        GamemodeSystem.Step("screen mode rule", () => SyncDisplay(null));
    }

    void Save()
    {
        if (_state.PlanSet is null && _state.DisplayGdi is null)
        {
            try { if (ConfigStore.FolderProblem() is null) File.Delete(StatePath); } catch { /* next save retries */ }
            return;
        }
        ConfigStore.PrepareDirectory();
        File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, GovernorJson.Default.RuleState));
    }

    /// <summary>Engine starting: a saved state belongs to an engine that crashed while a rule's plan or screen mode was on.</summary>
    public static void RecoverAtStart()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            if ((ConfigStore.FolderProblem() ?? ConfigStore.OwnerProblem(StatePath)) is { } problem) { Log.Warn($"Rule state: {problem}"); return; }
            var state = JsonSerializer.Deserialize(File.ReadAllText(StatePath), GovernorJson.Default.RuleState);
            if (state?.PlanSet is { } set && state.PlanBefore is { } before && GamemodeSystem.Power.Active() == set) GamemodeSystem.Power.Activate(before);
            if (state?.DisplayGdi is { } gdi) KosToolbox.Core.Gpu.WindowsDisplays.ResetTemporaryMode(gdi);
            Log.Warn("The engine stopped while a rule's power plan or screen mode was on; restored.");
            File.Delete(StatePath);
        }
        catch (Exception ex) { Log.Error("Restoring a rule's power plan or screen mode failed", ex); }
    }
}

/// <summary>Keeps the PC and the screen from sleeping. Per thread: call it from the engine loop's thread.</summary>
public static class KeepAwake
{
    const uint Continuous = 0x80000000, SystemRequired = 0x1, DisplayRequired = 0x2;

    public static void Set(bool on) => SetThreadExecutionState(on ? Continuous | SystemRequired | DisplayRequired : Continuous);

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}
