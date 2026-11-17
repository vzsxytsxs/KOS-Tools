using System.Text.RegularExpressions;
using KosToolbox.Core.SystemInfo;

namespace KosGovernor.Core;

/// <summary>
/// The engine: every interval it reads the process list and applies each program's rule once per start (and again
/// when the configuration changes or gamemode switches). Thread rules are checked every interval on the threads that
/// have not matched yet (the description may appear after the thread is created).
/// </summary>
public sealed class Engine
{
    readonly CpuTopology _topo = CpuTopology.Current;
    readonly Dictionary<int, long> _seen = new(), _seenThreads = new();
    readonly Dictionary<int, (long Create, ModuleMap Map)> _modules = new();
    /// <summary>Processes whose rule also covers the programs they open, and those programs (pid → start, rule).</summary>
    readonly Dictionary<int, (long Create, ProcessRule Rule)> _tree = new();
    /// <summary>Games given the gamemode cores (pid → start): back to all cores when gamemode turns off.</summary>
    readonly Dictionary<int, long> _gameCores = new();
    /// <summary>CPU sets for games without a rule while gamemode is on; null = off.</summary>
    ProcessRule? _gameRule;
    readonly SystemLoad _load = new();
    readonly AutoGamemode _auto = new();
    readonly CpuUsage _usage = new();
    readonly CpuLimiter _limiter = new();
    readonly Restraint _restraint = new();
    readonly ThreadDirector _director = new(CpuTopology.Current);
    /// <summary>The last interrupt trace (thread director), written by its own thread.</summary>
    volatile InterruptLoad? _interrupts;
    volatile bool _tracing;
    bool _traceFailed;
    long _lastTraceMs = -60_000;
    readonly RuleExtras _extras = new();
    /// <summary>Games told to keep their timer resolution in the background (pid, start).</summary>
    readonly HashSet<(int, long)> _timerGames = new();
    long _gamemodeSinceMs, _lastPurgeMs;
    bool _memoryCleaned, _audio, _dwmMmcss;
    readonly int _session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
    Dictionary<string, ProcessRule> _active = new(StringComparer.OrdinalIgnoreCase);
    GameList _games = new(Array.Empty<GameEntry>());
    string? _game, _autoGame;
    GamemodeSystemState? _system;
    SessionRecorder? _recorder;
    DateTime _stamp;
    bool _loaded;
    volatile bool _toggle;
    EventWaitHandle? _gamemodeFlag;

    public GovernorConfig Config { get; private set; } = new();
    public bool Gamemode { get; private set; }
    public string Tooltip { get; private set; } = "KOS Governor";
    /// <summary>Wakes the loop (hotkey and tray menu).</summary>
    public AutoResetEvent Wake { get; } = new(false);
    /// <summary>Configuration reread (the gamemode hotkey may have changed). Called on the loop thread.</summary>
    public event Action? ConfigChanged;
    /// <summary>Gamemode turned on (true) or off, after Windows' own changes. Called on the loop thread.</summary>
    public event Action<bool>? GamemodeSwitched;
    /// <summary>A gamemode session's report was saved (gamemode off or engine exiting). Called on the loop thread.</summary>
    public event Action<GamemodeSession>? SessionEnded;

    public void RequestToggle()
    {
        _toggle = true;
        Wake.Set();
    }

    public void Tick()
    {
        ReloadIfChanged();
        if (_toggle) { _toggle = false; Toggle(); }

        var snapshot = Snapshot.Take();
        var nowMs = Environment.TickCount64;
        _usage.Update(snapshot, nowMs, _topo.Cpus.Count);
        Automatic(snapshot);
        TimerResolution.Sync(Config.Timer, Gamemode);
        if (Gamemode && _system is { KeepStopped.Count: > 0 } system) GamemodeSystem.Keep(system);
        var services = _active.Values.Any(r => r.Services.Count > 0) ? Services.ByProcess() : null;
        var alive = new Dictionary<int, long>(snapshot.Count);
        foreach (var p in snapshot) alive[p.Pid] = p.CreateTime;
        var aliveThreads = new HashSet<int>();
        var now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var p in snapshot)
        {
            if (!_active.TryGetValue(p.Name, out var rule))
            {
                if (!Inherit(p, alive, now) && _gameRule is not null && p.SessionId == _session && _games.IsGame(p.Name)) ApplyGameCores(p);
                continue;
            }
            if (rule.IncludeChildren && rule.Action == RuleAction.Apply) _tree[p.Pid] = (p.CreateTime, rule);
            if (Waiting(rule, p.CreateTime, now)) continue;
            if (!(_seen.TryGetValue(p.Pid, out var created) && created == p.CreateTime))
            {
                _seen[p.Pid] = p.CreateTime;
                ApplyProcess(p, rule, services);
            }
            if (rule.Action == RuleAction.Apply && rule.Threads.Count > 0) ApplyThreads(p, rule, aliveThreads);
        }
        if (Gamemode && _system is { Background: true } background)
        {
            var byPid = new Dictionary<int, ProcessEntry>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;
            try { GamemodeSystem.Background(background, snapshot, alive, p => Throttleable(p, byPid)); }
            catch (Exception ex) { Log.Warn($"Gamemode: efficiency mode for background programs failed: {ex.Message}"); }
        }
        if (Gamemode && _system is { } restrain && Config.Active.System.RestrainBackground)
        {
            var byPid = new Dictionary<int, ProcessEntry>(snapshot.Count);
            foreach (var p in snapshot) byPid[p.Pid] = p;
            try { _restraint.Tick(restrain, snapshot, _usage, p => Throttleable(p, byPid) && !GamemodeSystem.NeverThrottled(p.Name), nowMs); }
            catch (Exception ex) { Log.Warn($"Gamemode: lowering busy background programs failed: {ex.Message}"); }
        }
        if (Gamemode && _system is { } directed)
        {
            try
            {
                if (Config.Active.System.ThreadDirector) Direct(directed, snapshot, nowMs);
                else _director.Stop(directed);
            }
            catch (Exception ex) { Log.Warn($"Thread director failed: {ex.Message}"); }
        }
        SampleInterrupts(nowMs);
        _limiter.Tick(snapshot, p => _active.GetValueOrDefault(p.Name), _usage, nowMs);
        _extras.Tick(snapshot, p => p.SessionId == _session ? _active.GetValueOrDefault(p.Name) : null, Gamemode && Config.Active.System.PowerPlan);
        GameTimers(snapshot);
        if (Gamemode) Memory(snapshot, nowMs);
        if (_recorder is not null && !Config.General.SessionReport)
        {
            _recorder.Dispose(); // turned off during the session: nothing saved
            _recorder = null;
            Log.Info("Session report: turned off; this session isn't recorded.");
        }
        if (Gamemode && _recorder is not null)
        {
            try { _recorder.Tick(snapshot, _usage, _games.IsGame, _games.Label); }
            catch (Exception ex) { Log.Warn($"Session report: sampling failed: {ex.Message}"); }
        }
        PruneTree(_tree, alive);
        Prune(_seen, alive.Keys.ToHashSet());
        Prune(_seenThreads, aliveThreads);
        foreach (var pid in _gameCores.Keys.Where(k => !alive.ContainsKey(k)).ToList()) _gameCores.Remove(pid);
        foreach (var pid in _modules.Keys.Where(k => !alive.ContainsKey(k)).ToList()) _modules.Remove(pid);

        var mode = !Gamemode ? "off" : _auto.TurnedOnByAuto && _autoGame is not null ? $"on ({_autoGame})" : "on";
        var tip = $"KOS Governor\n{Config.Active.Name} · Gamemode {mode}\nCPU {_load.CpuPercent():0}%";
        Tooltip = tip.Length > 127 ? tip[..127] : tip;
    }

    /// <summary>The thread director's game: the one it has while it runs, else the busiest listed game of this session without a CPU rule of its own.</summary>
    void Direct(GamemodeSystemState state, List<ProcessEntry> snapshot, long nowMs)
    {
        var games = snapshot.Where(p => p.SessionId == _session && _games.IsGame(p.Name)
                                        && !(_active.TryGetValue(p.Name, out var r) && (r.CpuSets is not null || r.Affinity is not null))).ToList();
        var game = games.Find(p => (p.Pid, p.CreateTime) == _director.Game) ?? games.MaxBy(p => _usage.Percent(p));
        var tree = game is null ? new HashSet<int>() : GameTree(snapshot);
        // Services and other users' programs stay as they are, as with the memory clean and the restraint.
        _director.Tick(state, Config.Active.System, snapshot, game,
            p => p.SessionId != _session || tree.Contains(p.Pid) || _active.ContainsKey(p.Name) || _tree.ContainsKey(p.Pid),
            (tid, created) => _seenThreads.TryGetValue(tid, out var c) && c == created, _interrupts, nowMs);
    }

    /// <summary>Thread director: a 2 s interrupt trace at most once a minute, never in gamemode (the game's own load would steer the placement).</summary>
    void SampleInterrupts(long nowMs)
    {
        var s = Config.Active.System;
        if (Gamemode || !s.ThreadDirector || !s.AvoidInterrupts || _tracing || nowMs - _lastTraceMs < 60_000) return;
        _lastTraceMs = nowMs;
        _tracing = true;
        new Thread(() =>
        {
            try { _interrupts = EtwDriverTrace.Sample(TimeSpan.FromSeconds(2)); }
            catch (Exception ex)
            {
                if (!_traceFailed) Log.Warn($"Thread director: the interrupt trace failed (logged once): {ex.Message}");
                _traceFailed = true;
            }
            finally { _tracing = false; }
        }) { IsBackground = true, Name = "Interrupt trace" }.Start();
    }

    /// <summary>With a timer resolution mode on, listed games keep their own timer request in the background (once per start).</summary>
    void GameTimers(List<ProcessEntry> snapshot)
    {
        if (Config.Timer.Mode == TimerMode.Off) { _timerGames.Clear(); return; }
        var alive = new HashSet<(int, long)>();
        foreach (var p in snapshot)
        {
            if (p.SessionId != _session || !_games.IsGame(p.Name)) continue;
            alive.Add((p.Pid, p.CreateTime));
            if (_timerGames.Add((p.Pid, p.CreateTime)) && !TimerResolution.KeepInBackground(p.Pid, p.CreateTime))
                Log.Warn($"{p.Name} (pid {p.Pid}) couldn't be told to keep its timer resolution in the background.");
        }
        _timerGames.RemoveWhere(k => !alive.Contains(k));
    }

    /// <summary>Gamemode's memory clean, after its delay, and the standby list purge when it grows past the limit.</summary>
    void Memory(List<ProcessEntry> snapshot, long nowMs)
    {
        var s = Config.Active.System;
        if (s.CleanMemory && !_memoryCleaned && nowMs - _gamemodeSinceMs >= Math.Max(0, s.CleanMemoryDelaySeconds) * 1000L)
        {
            _memoryCleaned = true;
            _lastPurgeMs = nowMs;
            var keep = GameTree(snapshot);
            GamemodeSystem.CleanMemory(snapshot, p => keep.Contains(p.Pid) || p.SessionId != _session); // services and other users stay
        }
        if (!s.PurgeStandby || nowMs - _lastPurgeMs < 60_000 || GamemodeSystem.MemoryLists() is not { } lists) return;
        if (lists.StandbyMb < s.PurgeStandbyMb || lists.FreeMb >= s.PurgeFreeMb) return;
        _lastPurgeMs = nowMs;
        GamemodeSystem.PurgeStandbyList($"standby {lists.StandbyMb:N0} MB, free {lists.FreeMb:N0} MB");
    }

    /// <summary>Listed games, everything they opened (at any depth) and what opened them (a launcher): left alone by the memory clean.</summary>
    internal HashSet<int> GameTree(IReadOnlyList<ProcessEntry> snapshot) => GameTree(snapshot, _games.IsGame);

    internal static HashSet<int> GameTree(IReadOnlyList<ProcessEntry> snapshot, Func<string, bool> isGame)
    {
        var byPid = new Dictionary<int, ProcessEntry>(snapshot.Count);
        foreach (var p in snapshot) byPid[p.Pid] = p;
        var games = snapshot.Where(p => isGame(p.Name)).ToList();
        var set = games.Select(p => p.Pid).ToHashSet();
        for (var added = true; added;)
        {
            added = false;
            foreach (var p in snapshot)
                if (!set.Contains(p.Pid) && set.Contains(p.ParentPid) && byPid[p.ParentPid].CreateTime <= p.CreateTime && set.Add(p.Pid)) added = true;
        }
        foreach (var g in games)
            if (byPid.TryGetValue(g.ParentPid, out var parent) && parent.CreateTime <= g.CreateTime) set.Add(parent.Pid);
        return set;
    }

    static void Prune(Dictionary<int, long> seen, HashSet<int> alive)
    {
        foreach (var id in seen.Keys.Where(k => !alive.Contains(k)).ToList()) seen.Remove(id);
    }

    void ReloadIfChanged()
    {
        var stamp = ConfigStore.Stamp;
        if (_loaded && stamp == _stamp) return;
        _stamp = stamp;
        _loaded = true;
        try { Config = ConfigStore.Load(); }
        catch (Exception ex)
        {
            Log.Error("The configuration can't be read; the previous rules stay", ex);
            return;
        }
        try { _games = new GameList(Config.AutoGamemode.Games); }
        catch (Exception ex) { Log.Error("The game list can't be read; automatic gamemode is off", ex); _games = new(Array.Empty<GameEntry>(), Array.Empty<GameEntry>()); }
        if (!Config.AutoGamemode.Enabled) _auto.Reset();
        Rebuild();
        Log.Info($"Configuration loaded: profile \"{Config.Active.Name}\", {_active.Count} rule(s){(Gamemode ? " with gamemode" : "")}, automatic gamemode {(Config.AutoGamemode.Enabled ? "on" : "off")}.");
        ConfigChanged?.Invoke();
    }

    /// <summary>
    /// Automatic gamemode: a listed game open in this Windows session turns it on; when the last one closes it turns off after the delay.
    /// Launchers and programs marked "never a game" don't count.
    /// </summary>
    void Automatic(List<ProcessEntry> snapshot)
    {
        var settings = Config.AutoGamemode;
        _game = settings.Enabled ? snapshot.FirstOrDefault(p => p.SessionId == _session && _games.IsGame(p.Name))?.Name : null;
        if (!settings.Enabled) return;
        switch (_auto.Next(_game is not null, Gamemode, DateTime.UtcNow, TimeSpan.FromSeconds(settings.OffDelaySeconds)))
        {
            case AutoGamemode.Step.TurnOn:
                _autoGame = _games.Label(_game!);
                SetGamemode(true, $"{_autoGame} ({_game}) started");
                break;
            case AutoGamemode.Step.TurnOff:
                SetGamemode(false, $"no game open for {settings.OffDelaySeconds} s");
                _autoGame = null;
                break;
        }
    }

    /// <summary>Profile rules; with gamemode, the "On Enable" ones on top (same program: the gamemode one wins).</summary>
    internal static Dictionary<string, ProcessRule> ActiveRules(Profile profile, bool gamemode)
    {
        var rules = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in profile.Rules) rules[r.Exe] = r;
        if (gamemode) foreach (var r in profile.OnEnable.Rules) rules[r.Exe] = r;
        return rules;
    }

    void Rebuild()
    {
        var all = ActiveRules(Config.Active, Gamemode);
        _active = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, r) in all)
        {
            var list = Gamemode && Config.Active.OnEnable.Rules.Contains(r) ? Config.Active.OnEnable.Rules : Config.Active.Rules;
            if (Validation.RuleProblem(r, list, Config.Masks, _topo) is { } problem) Log.Warn($"Rule {exe} ignored: {problem}");
            else _active[exe] = r;
        }
        var system = Config.Active.System;
        _gameRule = null;
        if (Gamemode && system.GameCores)
        {
            if (Validation.GameCoresProblem(system.GameCoresSpec, Config.Masks, _topo) is { } problem) Log.Warn($"Gamemode: {problem}");
            else _gameRule = new ProcessRule { Exe = "games", CpuSets = system.GameCoresSpec };
        }
        _seen.Clear();
        _seenThreads.Clear();
        _tree.Clear();
    }

    /// <summary>
    /// A program without a rule of its own, opened by one whose rule includes the programs it opens (or by one of those), gets
    /// that rule's process settings once per start.
    /// shortcut: a parent that opens a program and closes between two ticks isn't seen, so that program isn't covered. And when a
    /// recorded parent closes, its pid is reused, and the new process opens a program and closes, all between two ticks, that
    /// program is covered by mistake.
    /// </summary>
    /// <returns>Whether the program is covered by a parent's rule (even if that rule is still waiting its delay).</returns>
    bool Inherit(ProcessEntry p, Dictionary<int, long> alive, long now)
    {
        if (!_tree.TryGetValue(p.ParentPid, out var parent) || !Opened(parent.Create, p, alive)) return false;
        _tree[p.Pid] = (p.CreateTime, parent.Rule);
        if (Waiting(parent.Rule, p.CreateTime, now) || _seen.TryGetValue(p.Pid, out var created) && created == p.CreateTime) return true;
        _seen[p.Pid] = p.CreateTime;
        if (!parent.Rule.IsEmpty) Report(p, $"{parent.Rule.Exe} (opened {p.Name})", ProcessControl.Apply(p.Pid, parent.Rule, Config.Masks, _topo, p.CreateTime));
        return true;
    }

    /// <summary>
    /// The rule's delay since the program started (FILETIME, like the snapshot) hasn't passed yet. Checked every interval, so the rule
    /// applies up to one interval after the delay.
    /// </summary>
    internal static bool Waiting(ProcessRule rule, long created, long now) => rule.DelaySeconds > 0 && now - created < rule.DelaySeconds * TimeSpan.TicksPerSecond;

    /// <summary>Gamemode cores, once per start, for a listed game without a rule of its own.</summary>
    void ApplyGameCores(ProcessEntry p)
    {
        if (_seen.TryGetValue(p.Pid, out var created) && created == p.CreateTime) return;
        _seen[p.Pid] = p.CreateTime;
        _gameCores[p.Pid] = p.CreateTime;
        Report(p, $"gamemode cores ({p.Name})", ProcessControl.Apply(p.Pid, _gameRule!, Config.Masks, _topo, p.CreateTime));
    }

    /// <summary>
    /// Can go into efficiency mode in the background: a program of this session at normal priority or lower (audio and capture programs
    /// often raise theirs), not a game or something a game opened, without a rule of its own or from the program that opened it.
    /// </summary>
    bool Throttleable(ProcessEntry p, Dictionary<int, ProcessEntry> byPid) =>
        p.SessionId == _session && p.BasePriority <= 8 && !_games.IsGame(p.Name) && !_active.ContainsKey(p.Name) && !_tree.ContainsKey(p.Pid)
        && !(byPid.TryGetValue(p.ParentPid, out var parent) && parent.CreateTime <= p.CreateTime && _games.IsGame(parent.Name));

    /// <summary>
    /// After a tick, drops the recorded processes that closed or whose pid now belongs to another process. Run after the loop, so a
    /// parent that closed since the last tick still passed its rule on to what it opened.
    /// </summary>
    internal static void PruneTree(Dictionary<int, (long Create, ProcessRule Rule)> tree, IReadOnlyDictionary<int, long> alive)
    {
        foreach (var pid in tree.Where(e => !alive.TryGetValue(e.Key, out var now) || now != e.Value.Create).Select(e => e.Key).ToList()) tree.Remove(pid);
    }

    /// <summary>The recorded parent (started at <paramref name="parentCreate"/>) opened <paramref name="child"/>: it started first and its
    /// pid, if still in use, wasn't reused by another process.</summary>
    internal static bool Opened(long parentCreate, ProcessEntry child, IReadOnlyDictionary<int, long> alive) =>
        parentCreate <= child.CreateTime && (!alive.TryGetValue(child.ParentPid, out var now) || now == parentCreate);

    /// <summary>Hotkey, tray or interface: the user takes over (automatic mode doesn't turn off what the user turned on or turn back on what the user turned off).</summary>
    void Toggle()
    {
        SetGamemode(!Gamemode, null);
        _auto.Manual(Gamemode, _game is not null);
        _autoGame = null;
    }

    /// <param name="automatic">Reason when it was automatic gamemode; null = by the user.</param>
    void SetGamemode(bool on, string? automatic)
    {
        Gamemode = on;
        var profile = Config.Active;
        Log.Info($"Gamemode {(Gamemode ? "on" : "off")} (profile \"{profile.Name}\"{(automatic is null ? "" : $", automatic: {automatic}")}).");
        if (Gamemode)
        {
            _gamemodeFlag ??= new EventWaitHandle(false, EventResetMode.ManualReset, Names.GamemodeOn);
            // Windows changes (plan, services, transparency...): the previous state is saved so it can be undone.
            if (_system is null) _system = GamemodeSystem.Apply(profile.System);
            try { if (Config.General.SessionReport) _recorder ??= new SessionRecorder(profile.Name); }
            catch (Exception ex) { Log.Warn($"Session report: recording didn't start: {ex.Message}"); }
            _gamemodeSinceMs = Environment.TickCount64;
            _memoryCleaned = false;
            if (profile.System.KeepAwake) { KeepAwake.Set(true); Log.Info("Gamemode: the PC and the screen stay awake."); }
            if (profile.System.DwmMmcss) SetDwmMmcss(true);
            if (profile.System.LowLatencyAudio)
            {
                _audio = true;
                try { LowLatencyAudio.Start(); }
                catch (Exception ex) { Log.Warn($"Gamemode: low-latency audio failed: {ex.Message}"); }
            }
        }
        else
        {
            _gamemodeFlag?.Dispose();
            _gamemodeFlag = null;
            KeepAwake.Set(false);
            SetDwmMmcss(false);
            StopAudio();
            RestoreSystem();
            ResetGameCores();
            // "On Disable": once, on the programs open now.
            var once = profile.OnDisable.Rules.Where(r => Validation.RuleProblem(r, profile.OnDisable.Rules, Config.Masks, _topo) is null)
                .ToDictionary(r => r.Exe, StringComparer.OrdinalIgnoreCase);
            if (once.Count > 0)
            {
                var services = once.Values.Any(r => r.Services.Count > 0) ? Services.ByProcess() : null;
                foreach (var p in Snapshot.Take())
                    if (once.TryGetValue(p.Name, out var r)) ApplyProcess(p, r, services);
            }
        }
        GamemodeSwitched?.Invoke(Gamemode);
        if (!Gamemode) EndSession(); // after everything is undone: the log read and the file write don't delay the restore
        // Programs: always on a manual switch; on an automatic one only if the profile asks (opening programs by itself can surprise).
        foreach (var s in automatic is null || profile.AutoOpensPrograms ? (Gamemode ? profile.OnEnable : profile.OnDisable).Start : new List<StartEntry>())
        {
            try { UserLaunch.Start(s); }
            catch (Exception ex) { Log.Warn($"Gamemode: {s.Path} wasn't started: {ex.Message}"); }
        }
        Rebuild();
    }

    /// <summary>Games still open go back to all cores (a rule of their own, if any, is applied again after the rebuild).</summary>
    void ResetGameCores()
    {
        if (_gameCores.Count == 0) return;
        var all = new ProcessRule { Exe = "games", CpuSets = CpuSpec.All };
        foreach (var p in Snapshot.Take())
            if (_gameCores.TryGetValue(p.Pid, out var created) && created == p.CreateTime)
                Report(p, $"gamemode cores undone ({p.Name})", ProcessControl.Apply(p.Pid, all, Config.Masks, _topo, p.CreateTime));
        _gameCores.Clear();
    }

    void SetDwmMmcss(bool on)
    {
        if (on == _dwmMmcss) return;
        _dwmMmcss = on;
        var hr = Native.DwmEnableMMCSS(on ? 1 : 0);
        if (hr < 0) Log.Warn($"Gamemode: DWM MMCSS {(on ? "on" : "off")} failed (0x{hr:X8}).");
        else Log.Info($"Gamemode: DWM {(on ? "in" : "out of")} the MMCSS scheduling class.");
    }

    void StopAudio()
    {
        if (!_audio) return;
        _audio = false;
        try { LowLatencyAudio.Stop(); }
        catch (Exception ex) { Log.Warn($"Gamemode: turning low-latency audio off failed: {ex.Message}"); }
    }

    void RestoreSystem()
    {
        _director.Reset();
        if (_system is null) return;
        GamemodeSystem.Restore(_system);
        _system = null;
    }

    /// <summary>Saves the running session's report (once).</summary>
    void EndSession()
    {
        if (_recorder is not { } recorder) return;
        _recorder = null;
        try
        {
            var report = recorder.Finish();
            GamemodeSessions.Add(report);
            Log.Info($"Gamemode session report saved: {report.Summary}");
            SessionEnded?.Invoke(report);
        }
        catch (Exception ex) { Log.Warn($"Session report: saving failed: {ex.Message}"); }
        finally { recorder.Dispose(); }
    }

    /// <summary>Engine exiting (Exit in the tray): undoes the Windows changes of a gamemode still on.</summary>
    public void Shutdown()
    {
        try { KeepAwake.Set(false); SetDwmMmcss(false); StopAudio(); RestoreSystem(); ResetGameCores(); EtwDriverTrace.Close(); }
        catch (Exception ex) { Log.Error("Restoring gamemode's system changes at exit failed", ex); }
        try { _limiter.Release(); _extras.Release(); TimerResolution.Release(); }
        catch (Exception ex) { Log.Error("Undoing the rules' CPU limits, power plan, screen mode or timer resolution at exit failed", ex); }
        EndSession();
    }

    void ApplyProcess(ProcessEntry p, ProcessRule rule, Dictionary<int, List<HostedService>>? services)
    {
        // Already suspended (rule reapplied after rereading the configuration): suspending again would add another suspension.
        if (!(rule.Action == RuleAction.Suspend && p.Suspended) && !rule.IsEmpty) Report(p, rule.Exe, ProcessControl.Apply(p.Pid, rule, Config.Masks, _topo, p.CreateTime));
        if (rule.Services.Count == 0 || services?.TryGetValue(p.Pid, out var hosted) != true) return;
        foreach (var s in rule.Services)
            if (!s.IsEmpty && hosted!.Any(h => h.Name.Equals(s.Service, StringComparison.OrdinalIgnoreCase)))
                Report(p, $"{rule.Exe} ({s.Service})", ProcessControl.Apply(p.Pid, s, Config.Masks, _topo, p.CreateTime));
    }

    static void Report(ProcessEntry p, string what, List<string> errors)
    {
        if (errors.Count == 0) Log.Info($"Rule {what} applied to pid {p.Pid}.");
        else Log.Warn($"Rule {what} on pid {p.Pid}: {string.Join("; ", errors)}");
    }

    void ApplyThreads(ProcessEntry p, ProcessRule rule, HashSet<int> aliveThreads)
    {
        var needModule = rule.Threads.Any(t => !string.IsNullOrWhiteSpace(t.Module));
        var needDescription = rule.Threads.Any(t => !string.IsNullOrWhiteSpace(t.Description));
        var applied = 0;
        var errors = new HashSet<string>();
        ulong? processAffinity = null;
        var refreshed = false;
        foreach (var t in p.Threads)
        {
            aliveThreads.Add(t.Tid);
            if (_seenThreads.TryGetValue(t.Tid, out var created) && created == t.CreateTime) continue;
            var description = needDescription ? ThreadControl.Description(t.Tid) : null;
            string? module = null;
            if (needModule)
            {
                var start = ThreadControl.StartAddress(t.Tid);
                module = Modules(p, refresh: false).Find(start);
                if (module is null && !refreshed) { refreshed = true; module = Modules(p, refresh: true).Find(start); }
            }
            if (rule.Threads.FirstOrDefault(r => Matches(r, description, module)) is not { } tr) continue;
            _seenThreads[t.Tid] = t.CreateTime;
            if (tr.IsEmpty) continue;
            processAffinity ??= ProcessControl.Read(p.Pid, false, _topo).Affinity ?? 0;
            foreach (var e in ThreadControl.Apply(t.Tid, t.CreateTime, tr,processAffinity.Value, Config.Masks, _topo)) errors.Add(e);
            applied++;
        }
        if (applied > 0) Log.Info($"Thread rules of {rule.Exe} applied to {applied} thread(s) of pid {p.Pid}{(errors.Count > 0 ? ": " + string.Join("; ", errors) : ".")}");
    }

    ModuleMap Modules(ProcessEntry p, bool refresh)
    {
        if (!refresh && _modules.TryGetValue(p.Pid, out var m) && m.Create == p.CreateTime) return m.Map;
        var map = ModuleMap.Read(p.Pid);
        _modules[p.Pid] = (p.CreateTime, map);
        return map;
    }

    /// <summary>Description and module of the rule (the ones filled in) must match; * matches any text.</summary>
    internal static bool Matches(ThreadRule r, string? description, string? module) =>
        (string.IsNullOrWhiteSpace(r.Description) || Glob(r.Description, description))
        && (string.IsNullOrWhiteSpace(r.Module) || Glob(r.Module, module))
        && !(string.IsNullOrWhiteSpace(r.Description) && string.IsNullOrWhiteSpace(r.Module));

    internal static bool Glob(string pattern, string? text) =>
        text is not null && (pattern.Contains('*')
            ? Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : pattern.Equals(text, StringComparison.OrdinalIgnoreCase));
}
