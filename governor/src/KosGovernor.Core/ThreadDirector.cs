using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KosGovernor.Core;

/// <summary>
/// Journal of a thread the director changed. Isolated: <see cref="Mask"/> is the core it was given and <see cref="Ideal"/> its ideal
/// processor before (-1 = unknown). Lowered: it was time-critical and is now Highest.
/// </summary>
public sealed class DirectedThread
{
    public int Pid { get; set; }
    public int Tid { get; set; }
    public long Create { get; set; }
    public ulong Mask { get; set; }
    public int Ideal { get; set; } = -1;
}

/// <summary>Journal of a process given default CPU sets (<see cref="Mask"/>); <see cref="Previous"/> comes back on restore (0 = none).</summary>
public sealed class PlacedProcess
{
    public int Pid { get; set; }
    public long Create { get; set; }
    public ulong Mask { get; set; }
    public ulong Previous { get; set; }
}

/// <summary>
/// Thread director, while gamemode is on and a listed game runs (the busiest of this session without a CPU rule of its own). The game runs
/// on the game cores (CPU sets it already has, else the V-Cache CCD, the P-cores, the CCD with Windows' best-rated core or all). Every
/// second its threads' CPU use is measured (smoothed) and the busiest (<see cref="GamemodeSystemSettings.HeavyThreads"/>) get a core of
/// their own that the rest of the game stays off, picked away from the processors where the last interrupt trace saw the input stack or the
/// most DPC/ISR work. Left alone: worker and pool threads (by description), spin-waiting threads, threads the game pinned itself and threads
/// with a rule. A game that keeps its cores busy widens to the others and narrows back once it calms down. Optionally other programs move off the game cores, and other
/// programs' Realtime priority and time-critical threads are lowered. Every change is journaled in the gamemode state and undone when the
/// game closes, gamemode turns off or, after a crash, at the next start; only what is still as the director left it.
/// </summary>
public sealed class ThreadDirector(CpuTopology topo)
{
    const long WindowMs = 1_000;
    /// <summary>Share of one core a thread must use to get a core of its own, and to keep it (released after <see cref="ReleaseSamples"/> low samples in a row).</summary>
    internal const double HeavyShare = 0.4, KeepShare = 0.2;
    internal const int ReleaseSamples = 5;
    /// <summary>A thread busier than this with fewer context switches per second spins on a wait: a core of its own would be wasted on it.</summary>
    internal const double SpinShare = 0.5, SpinSwitches = 200;
    /// <summary>Game load (CPU used / its cores) that widens it to the other cores after <see cref="WidenSeconds"/>, and narrows it back after <see cref="NarrowSeconds"/>.</summary>
    internal const double WidenAt = 0.85, NarrowAt = 0.6, WidenSeconds = 10, NarrowSeconds = 30;
    const uint Realtime = 0x100, High = 0x80;
    const int TimeCritical = 15, Highest = 2;
    static readonly string[] PoolWords = { "worker", "pool", "taskgraph", "job" };
    /// <summary>Kernel, session and security processes, the compositor and the audio engine: never moved.</summary>
    static readonly HashSet<string> NeverMoved = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "LsaIso.exe",
        "winlogon.exe", "dwm.exe", "fontdrvhost.exe", "audiodg.exe",
    };

    (int Pid, long Create)? _game;
    string _gameName = "";
    ulong _gameCores, _gameMask;
    List<ulong> _cores = new();
    long _windowStart;
    bool _refusedLogged, _dirty, _widened;
    double _hot, _cool;
    Dictionary<(int, long), (ulong Cycles, long Cpu)> _last = new();
    Dictionary<(int, long), double> _smooth = new();
    Dictionary<(int, long), uint> _switches = new();
    readonly Dictionary<(int, long), int> _low = new();
    readonly HashSet<(int, long)> _moved = new(), _classChecked = new(), _threadsChecked = new(), _refused = new();
    readonly Dictionary<(int, long), bool> _leftAlone = new();

    /// <summary>The game being directed (pid, start); the engine keeps it while it runs, so two listed processes don't trade places.</summary>
    public (int Pid, long Create)? Game => _game;

    public void Tick(GamemodeSystemState state, GamemodeSystemSettings s, IReadOnlyList<ProcessEntry> snapshot, ProcessEntry? game,
        Func<ProcessEntry, bool> leaveAlone, Func<int, long, bool> threadHasRule, InterruptLoad? interrupts, long nowMs)
    {
        // A step that throws after earlier changes, or a save that failed, still gets the journal written (now or next tick).
        try { _dirty |= TickCore(state, s, snapshot, game, leaveAlone, threadHasRule, interrupts, nowMs); }
        catch { _dirty = true; throw; }
        finally
        {
            if (_dirty)
            {
                GamemodeSystem.Save(state);
                _dirty = false;
            }
        }
    }

    bool TickCore(GamemodeSystemState state, GamemodeSystemSettings s, IReadOnlyList<ProcessEntry> snapshot, ProcessEntry? game,
        Func<ProcessEntry, bool> leaveAlone, Func<int, long, bool> threadHasRule, InterruptLoad? interrupts, long nowMs)
    {
        var changed = Prune(state, snapshot);
        (int, long)? now = game is null ? null : (game.Pid, game.CreateTime);
        if (_game != now)
        {
            if (_game is not null)
            {
                Log.Info($"Thread director: {_gameName} closed or is no longer the game; undoing its changes.");
                changed |= Undo(state);
            }
            if (game is not null) changed |= Start(state, game, Math.Clamp(s.HeavyThreads, 1, 4), s.AvoidInterrupts ? interrupts : null, nowMs);
        }
        if (game is not null && _game is not null)
        {
            if (nowMs - _windowStart >= WindowMs) changed |= Rebalance(state, game, threadHasRule, nowMs);
            if (s.MoveBackground) changed |= Move(state, snapshot, leaveAlone);
            if (s.NormalizePriorities) changed |= Lower(state, snapshot, leaveAlone);
        }
        return changed;
    }

    /// <summary>Turned off while gamemode is on: undoes what it did.</summary>
    public void Stop(GamemodeSystemState state)
    {
        if (_game is null && !Journaled(state)) return;
        if (Undo(state)) GamemodeSystem.Save(state);
    }

    /// <summary>Gamemode off: the journal is undone by <see cref="Restore"/>; this forgets the rest.</summary>
    public void Reset()
    {
        _game = null;
        _cores = new();
        _last = new();
        _smooth = new();
        _switches = new();
        _low.Clear();
        _widened = false;
        _hot = _cool = 0;
        _moved.Clear(); _classChecked.Clear(); _threadsChecked.Clear(); _refused.Clear(); _leftAlone.Clear();
        _refusedLogged = false;
    }

    bool Undo(GamemodeSystemState state)
    {
        var any = Journaled(state);
        Restore(state);
        Reset();
        return any;
    }

    static bool Journaled(GamemodeSystemState s) => s.Isolated.Count + s.Placed.Count + s.LoweredRealtime.Count + s.LoweredThreads.Count > 0;

    bool Start(GamemodeSystemState state, ProcessEntry game, int heavy, InterruptLoad? interrupts, long nowMs)
    {
        Reset();
        _game = (game.Pid, game.CreateTime);
        _gameName = game.Name;
        _windowStart = nowMs;
        using var h = ProcessControl.Open(game.Pid, Native.ProcessQueryLimited | Native.ProcessSetLimited);
        if (h is null || !Affinity.Same(h, game.CreateTime) || ProcessControl.ReadCpuSets(h, topo) is not { } current)
        {
            Log.Warn($"Thread director: {game.Name}'s CPU sets can't be read or changed; left as it is.");
            _cores = new();
            _gameCores = topo.AllMask;
            return false;
        }
        _gameCores = GameCores(topo, current == topo.AllMask ? 0 : current);
        _cores = Reserve(topo, _gameCores, heavy, interrupts?.CpuMs, interrupts?.InputCpus);
        var reserved = _cores.Aggregate(0UL, (m, c) => m | c);
        _gameMask = _gameCores & ~reserved;
        if (_gameMask == topo.AllMask) _gameMask = 0;
        var changed = _gameMask != current && Place(state, h, game.Pid, game.CreateTime, _gameMask, current);
        Log.Info($"Thread director: {game.Name} on CPUs {CpuSpec.FormatList(_gameCores)}"
                 + (_cores.Count == 0 ? " (too few cores to give its busiest threads their own)." : $"; CPUs {CpuSpec.FormatList(reserved)} kept for its {_cores.Count} busiest thread(s).")
                 + (interrupts is null ? "" : $" Most interrupt work: {interrupts.Busiest()}."));
        Measure(game, 0); // the starting point of the first window
        return changed;
    }

    /// <summary>The game's cores: CPU sets it already has (its own, or Cores for games), else the V-Cache CCD, the P-cores, the CCD with
    /// Windows' best-rated core (several equal CCDs) or all.</summary>
    internal static ulong GameCores(CpuTopology topo, ulong current) =>
        current != 0 ? current : topo.VCacheMask ?? (topo.IsHybrid ? topo.PCoreMask : topo.BestRankedCcdMask ?? topo.AllMask);

    /// <summary>
    /// Cores for the busiest threads, one mask per physical core with all its processors among <paramref name="game"/>: at most
    /// <paramref name="heavy"/>, leaving at least two to the rest of the game. Cores where the input stack ran come last, whatever their
    /// load; then cores with no real interrupt work (under 1 ms in the trace, about 0.5 ms/s) first, then Windows' best-rated cores
    /// (scheduling class), then the least interrupt work, then any but the core of CPU 0, which takes most interrupts by default.
    /// </summary>
    const double QuietInterruptMs = 1;

    internal static List<ulong> Reserve(CpuTopology topo, ulong game, int heavy, IReadOnlyDictionary<int, double>? interrupts,
        IReadOnlySet<int>? inputCpus = null)
    {
        var cores = topo.Cores.Where(c => c.All(l => (game >> l.Index & 1) == 1)).ToList();
        var count = Math.Min(heavy, cores.Count - 2);
        if (count <= 0) return new();
        double Work(IReadOnlyList<LogicalCpu> c) => interrupts is null ? 0 : c.Sum(l => interrupts.GetValueOrDefault(l.Index));
        return cores.OrderBy(c => inputCpus is not null && c.Any(l => inputCpus.Contains(l.Index)) ? 1 : 0)
            .ThenBy(c => Work(c) >= QuietInterruptMs ? 1 : 0).ThenByDescending(c => c.Max(l => l.SchedulingClass)).ThenBy(Work)
            .ThenBy(c => c.Any(l => l.Index == 0) ? 1 : 0).ThenBy(c => c[0].Index)
            .Take(count).Select(c => c.Aggregate(0UL, (m, l) => m | 1UL << l.Index)).ToList();
    }

    /// <summary>Where other programs go: off the game cores when there are others (E-cores, the other CCD), else off the busiest threads' cores.</summary>
    internal static ulong OthersMask(ulong all, ulong gameCores, ulong reserved)
    {
        var rest = all & ~gameCores;
        return rest != 0 ? rest : all & ~reserved;
    }

    /// <summary>
    /// Which threads lose and get a core: an isolated one keeps it while it uses at least <see cref="KeepShare"/> of a core, and the free
    /// cores go to the others using at least <see cref="HeavyShare"/>, most cycles first. Stable on purpose: a busier newcomer waits for a free core.
    /// </summary>
    internal static (List<T> Release, List<T> Add) Choose<T>(IReadOnlyList<(T Key, double Busy, ulong Cycles)> measured, IReadOnlySet<T> isolated, int cores)
        where T : notnull
    {
        var busy = measured.ToDictionary(m => m.Key, m => m.Busy);
        var release = isolated.Where(k => busy.GetValueOrDefault(k) < KeepShare).ToList();
        var free = cores - (isolated.Count - release.Count);
        var add = measured.Where(m => m.Busy >= HeavyShare && !isolated.Contains(m.Key)).OrderByDescending(m => m.Cycles)
            .Take(Math.Max(0, free)).Select(m => m.Key).ToList();
        return (release, add);
    }

    /// <summary>A spin-waiting thread: busy, yet it barely waits (few context switches). Unknown switches = not a spinner.</summary>
    internal static bool IsSpinner(double busy, double? switchesPerSecond) => busy >= SpinShare && switchesPerSecond is { } s && s < SpinSwitches;

    /// <summary>
    /// Whether the game should be wide (on every core but the busiest threads') after a sample of <paramref name="seconds"/> at
    /// <paramref name="load"/>: it widens after <see cref="WidenSeconds"/> at <see cref="WidenAt"/> or more, and narrows after
    /// <see cref="NarrowSeconds"/> under <see cref="NarrowAt"/>.
    /// </summary>
    internal static bool Widen(bool wide, double load, double seconds, ref double hot, ref double cool)
    {
        if (!wide)
        {
            cool = 0;
            hot = load >= WidenAt ? hot + seconds : 0;
            return hot >= WidenSeconds;
        }
        hot = 0;
        cool = load < NarrowAt ? cool + seconds : 0;
        return cool < NarrowSeconds;
    }

    /// <summary>Cycles (ranking) and CPU time (share of a core) of the game's threads since the last call.</summary>
    List<((int Tid, long Create) Key, double Busy, ulong Cycles)> Measure(ProcessEntry game, double seconds)
    {
        var next = new Dictionary<(int, long), (ulong Cycles, long Cpu)>();
        var result = new List<((int, long), double, ulong)>();
        foreach (var t in game.Threads)
        {
            using var h = ThreadControl.Open(t.Tid, Native.ThreadQueryLimited);
            if (h is null || !Native.QueryThreadCycleTime(h, out var cycles)
                || !(Native.GetThreadTimes(h, out var created, out _, out var kernel, out var user) && created == t.CreateTime)) continue;
            var key = (t.Tid, t.CreateTime);
            next[key] = (cycles, kernel + user);
            if (seconds > 0 && _last.TryGetValue(key, out var before))
                result.Add((key, (kernel + user - before.Cpu) / (seconds * 10_000_000), cycles - before.Cycles));
        }
        _last = next;
        return result;
    }

    bool Rebalance(GamemodeSystemState state, ProcessEntry game, Func<int, long, bool> threadHasRule, long nowMs)
    {
        var changed = KeepPlacement(state, game);
        var seconds = (nowMs - _windowStart) / 1000.0;
        var raw = Measure(game, seconds);
        _windowStart = nowMs;
        var switches = new Dictionary<(int, long), uint>();
        var rates = new Dictionary<(int, long), double>();
        foreach (var t in game.Threads)
        {
            var key = (t.Tid, t.CreateTime);
            switches[key] = t.ContextSwitches;
            if (seconds > 0 && _switches.TryGetValue(key, out var before)) rates[key] = (t.ContextSwitches - before) / seconds;
        }
        _switches = switches;
        // Smoothed share of a core: half the new sample, half the old (a new thread starts at its first sample).
        var smooth = new Dictionary<(int, long), double>();
        var measured = raw.Select(m => (m.Key, Busy: smooth[m.Key] = _smooth.TryGetValue(m.Key, out var old) ? (old + m.Busy) / 2 : m.Busy, m.Cycles)).ToList();
        _smooth = smooth;
        changed |= WidenOrNarrow(state, game, raw.Sum(m => m.Busy), seconds);
        if (_cores.Count == 0) return changed;
        var isolated = state.Isolated.Where(t => t.Pid == game.Pid).ToList();
        var ids = isolated.Select(t => (t.Tid, t.Create)).ToHashSet();
        // An isolated thread keeps its core until it has been under the keep share ReleaseSamples times in a row.
        foreach (var key in ids) _low[key] = measured.Any(m => m.Key == key && m.Busy >= KeepShare) ? 0 : _low.GetValueOrDefault(key) + 1;
        foreach (var key in _low.Keys.Where(k => !ids.Contains(k)).ToList()) _low.Remove(key);
        measured = measured.Select(m => ids.Contains(m.Key) && _low.GetValueOrDefault(m.Key) < ReleaseSamples ? (m.Key, Math.Max(m.Busy, KeepShare), m.Cycles) : m).ToList();
        var processAffinity = Affinity.Read(game.Pid, game.CreateTime);
        var candidates = measured.Where(m => ids.Contains(m.Key)
            || m.Busy >= HeavyShare && !_refused.Contains(m.Key) && !IsSpinner(m.Busy, rates.TryGetValue(m.Key, out var r) ? r : null)
               && processAffinity is { } a && Eligible(m.Key.Tid, m.Key.Create, a, threadHasRule)).ToList();
        var (release, add) = Choose(candidates, ids, _cores.Count);
        foreach (var key in release)
        {
            var t = isolated.First(i => (i.Tid, i.Create) == key);
            Unpin(t, topo);
            state.Isolated.Remove(t);
            changed = true;
            Log.Info($"Thread director: {game.Name} thread {t.Tid} calmed down; back to the game's cores.");
        }
        var free = new Queue<ulong>(_cores.Where(c => !state.Isolated.Exists(t => t.Pid == game.Pid && t.Mask == c)));
        foreach (var key in add)
        {
            if (!free.TryPeek(out var core)) break;
            var (tid, created) = key;
            if (!Pin(state, game, tid, created, core)) { _refused.Add(key); continue; }
            free.Dequeue();
            changed = true;
        }
        return changed;
    }

    /// <summary>The game keeps its cores busy (<see cref="Widen"/>): its CPU sets go to every core but the busiest threads'; back once it calms down.</summary>
    bool WidenOrNarrow(GamemodeSystemState state, ProcessEntry game, double used, double seconds)
    {
        var reserved = _cores.Aggregate(0UL, (m, c) => m | c);
        var wideMask = topo.AllMask & ~reserved;
        if (wideMask == topo.AllMask) wideMask = 0;
        if (wideMask == _gameMask || seconds <= 0) return false; // already on every core it could get
        var load = used / Math.Max(1, BitOperations.PopCount(_gameCores & ~reserved));
        var wide = Widen(_widened, load, seconds, ref _hot, ref _cool);
        if (wide == _widened) return false;
        using var h = ProcessControl.Open(game.Pid, Native.ProcessQueryLimited | Native.ProcessSetLimited);
        if (h is null || !Affinity.Same(h, game.CreateTime) || ProcessControl.ReadCpuSets(h, topo) is not { } current) return false;
        var previous = state.Placed.Find(p => p.Pid == game.Pid && p.Create == game.CreateTime)?.Previous ?? current;
        if (!Place(state, h, game.Pid, game.CreateTime, wide ? wideMask : _gameMask, previous)) return false;
        _widened = wide;
        Log.Info(wide ? $"Thread director: {game.Name} kept its cores {load:P0} busy for {WidenSeconds:0} s; widened to CPUs {CpuSpec.FormatList(wideMask == 0 ? topo.AllMask : wideMask)}."
                      : $"Thread director: {game.Name} calmed down; back to CPUs {CpuSpec.FormatList(_gameMask == 0 ? topo.AllMask : _gameMask)}.");
        return true;
    }

    /// <summary>A candidate for a core of its own: no thread rule, not a worker or pool thread, not pinned by the game (CPU sets or a narrower affinity).</summary>
    bool Eligible(int tid, long created, ulong processAffinity, Func<int, long, bool> threadHasRule)
    {
        if (threadHasRule(tid, created)) return false;
        if (ThreadControl.Description(tid) is { } d && PoolWords.Any(w => d.Contains(w, StringComparison.OrdinalIgnoreCase))) return false;
        using var h = ThreadControl.Open(tid, Native.ThreadQueryLimited);
        if (h is null || ThreadControl.ReadCpuSets(h, topo) is not 0) return false;
        var basic = new Native.ThreadBasicInformation();
        return Native.NtQueryInformationThread(h, Native.ThreadBasicInformationClass, ref basic, Marshal.SizeOf<Native.ThreadBasicInformation>(), out _) >= 0
               && (ulong)basic.AffinityMask == processAffinity;
    }

    bool Pin(GamemodeSystemState state, ProcessEntry game, int tid, long created, ulong core)
    {
        // Full access also sets the ideal processor; some games allow only the limited rights, enough for the CPU sets.
        using var h = ThreadControl.Open(tid, Native.ThreadQueryLimited | Native.ThreadSetLimited | Native.ThreadSetInformation)
                      ?? ThreadControl.Open(tid, Native.ThreadQueryLimited | Native.ThreadSetLimited);
        if (h is null)
        {
            if (!_refusedLogged) Log.Warn($"Thread director: {game.Name}'s threads can't be changed ({Native.Win32Message(Marshal.GetLastWin32Error())}); an anti-cheat may protect them.");
            _refusedLogged = true;
            return false;
        }
        if (!(Native.GetThreadTimes(h, out var c, out _, out _, out _) && c == created)) return false;
        var ids = topo.CpuSetIds(core);
        if (!Native.SetThreadSelectedCpuSets(h, ids, (uint)ids.Length)) return false;
        var ideal = Native.GetThreadIdealProcessorEx(h, out var before) && before.Group == 0 ? before.Number : -1;
        var n = new Native.ProcessorNumber { Number = (byte)BitOperations.TrailingZeroCount(core) };
        Native.SetThreadIdealProcessorEx(h, ref n, out _);
        state.Isolated.Add(new DirectedThread { Pid = game.Pid, Tid = tid, Create = created, Mask = core, Ideal = ideal });
        var name = ThreadControl.Description(tid) is { } d ? $" ({d})" : "";
        Log.Info($"Thread director: {game.Name} thread {tid}{name} gets CPUs {CpuSpec.FormatList(core)} to itself.");
        return true;
    }

    /// <summary>The game's CPU sets were changed by something else (a rule applied again): set them back.</summary>
    bool KeepPlacement(GamemodeSystemState state, ProcessEntry game)
    {
        if (state.Placed.Find(p => p.Pid == game.Pid && p.Create == game.CreateTime) is not { } entry) return false;
        using var h = ProcessControl.Open(game.Pid, Native.ProcessQueryLimited | Native.ProcessSetLimited);
        if (h is null || !Affinity.Same(h, game.CreateTime) || ProcessControl.ReadCpuSets(h, topo) is not { } now || now == entry.Mask) return false;
        Log.Info($"Thread director: {game.Name}'s CPU sets were changed to CPUs {CpuSpec.FormatList(now)}; set back.");
        return Place(state, h, game.Pid, game.CreateTime, entry.Mask, entry.Previous);
    }

    bool Place(GamemodeSystemState state, SafeProcessHandle h, int pid, long created, ulong mask, ulong previous)
    {
        var ids = mask == 0 ? null : topo.CpuSetIds(mask);
        if (!Native.SetProcessDefaultCpuSets(h, ids, (uint)(ids?.Length ?? 0))) return false;
        if (state.Placed.Find(p => p.Pid == pid && p.Create == created) is { } entry) entry.Mask = mask;
        else state.Placed.Add(new PlacedProcess { Pid = pid, Create = created, Mask = mask, Previous = previous });
        return true;
    }

    /// <summary>Other programs, once per start, off the game's cores. Left as they are: the game and what it opened, programs with a rule, anti-cheats,
    /// voice and capture programs, the processes in <see cref="NeverMoved"/>, and ones with CPU sets or an affinity of their own.</summary>
    bool Move(GamemodeSystemState state, IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, bool> leaveAlone)
    {
        var target = OthersMask(topo.AllMask, _gameCores, _cores.Aggregate(0UL, (m, c) => m | c));
        if (target is 0 || target == topo.AllMask) return false;
        var moved = 0;
        foreach (var p in snapshot)
        {
            if (!_moved.Add((p.Pid, p.CreateTime)) || p.Pid is 0 or 4 || p.Pid == Environment.ProcessId || p.Pid == _game?.Pid || NeverMoved.Contains(p.Name)
                || GamemodeSystem.IsAntiCheat(p.Name) || GamemodeSystem.NeverThrottled(p.Name) || leaveAlone(p)) continue;
            using var h = ProcessControl.Open(p.Pid, Native.ProcessQueryLimited | Native.ProcessSetLimited);
            if (h is null || !Affinity.Same(h, p.CreateTime) || ProcessControl.ReadCpuSets(h, topo) is not 0
                || !Native.GetProcessAffinityMask(h, out var mask, out var system) || mask != system) continue;
            if (Place(state, h, p.Pid, p.CreateTime, target, 0)) moved++;
        }
        if (moved > 0) Log.Info($"Thread director: {moved} other program(s) moved to CPUs {CpuSpec.FormatList(target)}.");
        return moved > 0;
    }

    /// <summary>
    /// Other programs at Realtime priority drop to High, and time-critical threads to Highest (once per start). Left alone: the game and
    /// what it opened, programs with a rule, other sessions, anti-cheats, voice and capture programs, Windows' own (Windows folder, or no
    /// readable path) and MMCSS users (avrt.dll loaded).
    /// </summary>
    bool Lower(GamemodeSystemState state, IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, bool> leaveAlone)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snapshot)
        {
            if (p.Pid is 0 or 4 || p.Pid == Environment.ProcessId) continue;
            var key = (p.Pid, p.CreateTime);
            var realtime = p.BasePriority >= 24 && _classChecked.Add(key);
            var critical = new List<ThreadEntry>();
            if (p.BasePriority < 24)
                foreach (var t in p.Threads)
                    if (t.BasePriority == TimeCritical && _threadsChecked.Add((t.Tid, t.CreateTime))) critical.Add(t);
            if (!realtime && critical.Count == 0 || LeftAlone(p, leaveAlone)) continue;
            if (realtime)
            {
                using var h = ProcessControl.Open(p.Pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
                if (h is not null && Affinity.Same(h, p.CreateTime) && Native.GetPriorityClass(h) == Realtime && Native.SetPriorityClass(h, High))
                {
                    state.LoweredRealtime.Add(new ThrottledProcess { Pid = p.Pid, Create = p.CreateTime });
                    names.Add(p.Name);
                }
            }
            foreach (var t in critical)
            {
                using var h = ThreadControl.Open(t.Tid, Native.ThreadQueryLimited | Native.ThreadSetLimited);
                if (h is null || !(Native.GetThreadTimes(h, out var c, out _, out _, out _) && c == t.CreateTime)) continue;
                if (Native.GetThreadPriority(h) != TimeCritical || !Native.SetThreadPriority(h, Highest)) continue;
                state.LoweredThreads.Add(new DirectedThread { Pid = p.Pid, Tid = t.Tid, Create = t.CreateTime });
                names.Add(p.Name);
            }
        }
        if (names.Count > 0) Log.Info($"Thread director: Realtime priority or time-critical threads lowered in {string.Join(", ", names)}.");
        return names.Count > 0;
    }

    bool LeftAlone(ProcessEntry p, Func<ProcessEntry, bool> leaveAlone)
    {
        var key = (p.Pid, p.CreateTime);
        if (_leftAlone.TryGetValue(key, out var alone)) return alone;
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        return _leftAlone[key] = leaveAlone(p) || GamemodeSystem.IsAntiCheat(p.Name) || GamemodeSystem.NeverThrottled(p.Name)
                                 || ProcessControl.ImagePath(p.Pid) is not { } path || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                                 || ModuleMap.Read(p.Pid).Has("avrt.dll") != false;
    }

    /// <summary>Drops what closed from the journal and the per-start sets.</summary>
    bool Prune(GamemodeSystemState state, IReadOnlyList<ProcessEntry> snapshot)
    {
        var alive = new HashSet<(int, long)>(snapshot.Count);
        var threads = new HashSet<(int, long)>();
        foreach (var p in snapshot)
        {
            alive.Add((p.Pid, p.CreateTime));
            foreach (var t in p.Threads) threads.Add((t.Tid, t.CreateTime));
        }
        _moved.RemoveWhere(k => !alive.Contains(k));
        _classChecked.RemoveWhere(k => !alive.Contains(k));
        foreach (var k in _leftAlone.Keys.Where(k => !alive.Contains(k)).ToList()) _leftAlone.Remove(k);
        _threadsChecked.RemoveWhere(k => !threads.Contains(k));
        _refused.RemoveWhere(k => !threads.Contains(k));
        return state.Placed.RemoveAll(p => !alive.Contains((p.Pid, p.Create))) + state.LoweredRealtime.RemoveAll(p => !alive.Contains((p.Pid, p.Create)))
               + state.Isolated.RemoveAll(t => !threads.Contains((t.Tid, t.Create))) + state.LoweredThreads.RemoveAll(t => !threads.Contains((t.Tid, t.Create))) > 0;
    }

    /// <summary>Undoes the journal (gamemode off, the game closed, or a crash at the next start); only what is still as the director left it.</summary>
    public static void Restore(GamemodeSystemState state)
    {
        if (!Journaled(state)) return;
        var topo = CpuTopology.Current;
        var threads = state.Isolated.Count(t => Unpin(t, topo));
        var processes = state.Placed.Count(p => Unplace(p, topo));
        var priorities = state.LoweredRealtime.Count(RaiseClass) + state.LoweredThreads.Count(RaiseThread);
        Log.Info($"Thread director undone: {threads} thread(s) back to the game's cores, {processes} program(s) back to their CPU sets, "
                 + $"{priorities} priority change(s) undone (the rest closed or changed since).");
        state.Isolated.Clear();
        state.Placed.Clear();
        state.LoweredRealtime.Clear();
        state.LoweredThreads.Clear();
    }

    static bool Unpin(DirectedThread t, CpuTopology topo)
    {
        using var h = ThreadControl.Open(t.Tid, Native.ThreadQueryLimited | Native.ThreadSetLimited | Native.ThreadSetInformation)
                      ?? ThreadControl.Open(t.Tid, Native.ThreadQueryLimited | Native.ThreadSetLimited);
        if (h is null || !(Native.GetThreadTimes(h, out var c, out _, out _, out _) && c == t.Create) || ThreadControl.ReadCpuSets(h, topo) != t.Mask) return false;
        if (!Native.SetThreadSelectedCpuSets(h, null, 0)) return false;
        if (t.Ideal >= 0)
        {
            var n = new Native.ProcessorNumber { Number = (byte)t.Ideal };
            Native.SetThreadIdealProcessorEx(h, ref n, out _);
        }
        return true;
    }

    static bool Unplace(PlacedProcess p, CpuTopology topo)
    {
        using var h = ProcessControl.Open(p.Pid, Native.ProcessQueryLimited | Native.ProcessSetLimited);
        if (h is null || !Affinity.Same(h, p.Create) || ProcessControl.ReadCpuSets(h, topo) != p.Mask) return false;
        var ids = p.Previous == 0 ? null : topo.CpuSetIds(p.Previous);
        return Native.SetProcessDefaultCpuSets(h, ids, (uint)(ids?.Length ?? 0));
    }

    static bool RaiseClass(ThrottledProcess p)
    {
        using var h = ProcessControl.Open(p.Pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
        return h is not null && Affinity.Same(h, p.Create) && Native.GetPriorityClass(h) == High && Native.SetPriorityClass(h, Realtime);
    }

    static bool RaiseThread(DirectedThread t)
    {
        using var h = ThreadControl.Open(t.Tid, Native.ThreadQueryLimited | Native.ThreadSetLimited);
        return h is not null && Native.GetThreadTimes(h, out var c, out _, out _, out _) && c == t.Create
               && Native.GetThreadPriority(h) == Highest && Native.SetThreadPriority(h, TimeCritical);
    }
}
