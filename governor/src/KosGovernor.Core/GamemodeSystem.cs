using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>Windows changes while gamemode is on (per profile). They apply to both manual and automatic gamemode.</summary>
public sealed class GamemodeSystemSettings
{
    /// <summary>Activates <see cref="PowerPlanId"/> and goes back to the previous plan.</summary>
    public bool PowerPlan { get; set; }
    public Guid? PowerPlanId { get; set; }
    /// <summary>Stops wuauserv, UsoSvc and DoSvc (without changing startup) and stops them again if Windows turns them back on.</summary>
    public bool StopWindowsUpdate { get; set; }
    /// <summary>Stops WSearch (indexing) the same way.</summary>
    public bool StopSearch { get; set; }
    public bool TransparencyOff { get; set; }
    /// <summary>"Processor idle disable" = 1 on the active plan (no C-states); raises power draw and temperature.</summary>
    public bool CStatesOff { get; set; }
    /// <summary>Once on enable: empties the working set of programs (not Windows ones, games, what they open, or anti-cheats) and the standby list.</summary>
    public bool CleanMemory { get; set; }
    /// <summary>Waits this long after gamemode turns on before <see cref="CleanMemory"/> (so it doesn't run while the game loads). 0 = right away.</summary>
    public int CleanMemoryDelaySeconds { get; set; } = 60;
    /// <summary>During the game: purges the standby list whenever it holds more than <see cref="PurgeStandbyMb"/> while free memory is under
    /// <see cref="PurgeFreeMb"/> (at most once a minute). Only the file cache: the memory of running programs isn't touched.</summary>
    public bool PurgeStandby { get; set; }
    public int PurgeStandbyMb { get; set; } = 1024;
    public int PurgeFreeMb { get; set; } = 1024;
    /// <summary>Keeps the PC and the screen awake (controller play and loading screens don't reset the idle timer).</summary>
    public bool KeepAwake { get; set; }
    /// <summary>Wi-Fi: no background scans (ping spikes) and the driver's streaming mode on the connected interfaces.</summary>
    public bool WifiLowLatency { get; set; }
    /// <summary>Background programs that keep using a lot of CPU drop to Below normal priority until they calm down.</summary>
    public bool RestrainBackground { get; set; }
    /// <summary>The default playback device runs at the driver's smallest shared buffer (lower audio latency).</summary>
    public bool LowLatencyAudio { get; set; }
    /// <summary>The desktop compositor (DWM) runs in the MMCSS scheduling class (DwmEnableMMCSS), for the engine's lifetime: a crash undoes it.</summary>
    public bool DwmMmcss { get; set; }
    /// <summary>Efficiency mode (EcoQoS) for programs in the background; see <see cref="GamemodeSystem.Background"/>. On by default.</summary>
    public bool BackgroundEfficiency { get; set; } = true;
    /// <summary>Games without a rule of their own get <see cref="GameCoresSpec"/> as CPU sets (the engine does it, not <see cref="GamemodeSystem"/>).</summary>
    public bool GameCores { get; set; }
    public string? GameCoresSpec { get; set; }
    /// <summary>While a listed game runs, its busiest threads get cores of their own (see <see cref="Core.ThreadDirector"/>).</summary>
    public bool ThreadDirector { get; set; }
    /// <summary>How many of the game's busiest threads get a core of their own (1–4).</summary>
    public int HeavyThreads { get; set; } = 2;
    /// <summary>Thread director: other programs move off the game's cores.</summary>
    public bool MoveBackground { get; set; } = true;
    /// <summary>Thread director: other programs' Realtime priority drops to High and their time-critical threads to Highest.</summary>
    public bool NormalizePriorities { get; set; } = true;
    /// <summary>Thread director: a short interrupt trace (once a minute outside gamemode) keeps the busiest threads off the processors with the most DPC/ISR work.</summary>
    public bool AvoidInterrupts { get; set; } = true;
    /// <summary>The governor's readings (GPU, CPU, memory) on RivaTuner Statistics Server's on-screen display, with RTSS's own FPS. Needs RTSS running.</summary>
    public bool RtssOsd { get; set; }
    /// <summary>RTSS's global frame limit while gamemode is on, in frames per second (0 = RTSS's own setting stays). Put back on disable if still as set.</summary>
    public int RtssFrameLimit { get; set; }

    [JsonIgnore] public bool Any => PowerPlan || StopWindowsUpdate || StopSearch || TransparencyOff || CStatesOff || CleanMemory || BackgroundEfficiency
        || PurgeStandby || KeepAwake || WifiLowLatency || RestrainBackground || LowLatencyAudio || DwmMmcss || ThreadDirector || RtssOsd || RtssFrameLimit > 0;
}

public sealed class ThrottledProcess
{
    public int Pid { get; set; }
    public long Create { get; set; }
}

/// <summary>What gamemode changed and the previous value. Saved at every step: after a crash, the engine undoes it on startup.</summary>
public sealed class GamemodeSystemState
{
    public Guid? PlanBefore { get; set; }
    public Guid? PlanSet { get; set; }
    public Guid? IdlePlan { get; set; }
    public uint? IdleAcBefore { get; set; }
    public uint? IdleDcBefore { get; set; }
    /// <summary>Services kept stopped while gamemode is on.</summary>
    public List<string> KeepStopped { get; set; } = new();
    /// <summary>The ones that were running before: started again on disable.</summary>
    public List<string> WereRunning { get; set; } = new();
    public bool TransparencySet { get; set; }
    /// <summary>Null = the value didn't exist (Windows default: transparency on).</summary>
    public int? TransparencyBefore { get; set; }
    /// <summary>Background programs get efficiency mode while gamemode is on.</summary>
    public bool Background { get; set; }
    /// <summary>The ones put in efficiency mode (pid and start time): back to automatic on disable.</summary>
    public List<ThrottledProcess> Throttled { get; set; } = new();
    /// <summary>Background programs lowered to Below normal for using too much CPU: back to Normal on disable.</summary>
    public List<ThrottledProcess> Restrained { get; set; } = new();
    /// <summary>Wi-Fi interfaces (GUID) whose background scan was turned off: turned back on on disable.</summary>
    public List<Guid> Wifi { get; set; } = new();
    /// <summary>Thread director: the game's threads given a core of their own.</summary>
    public List<DirectedThread> Isolated { get; set; } = new();
    /// <summary>Thread director: the game and other programs given default CPU sets.</summary>
    public List<PlacedProcess> Placed { get; set; } = new();
    /// <summary>Thread director: programs lowered from Realtime to High, and threads from time-critical to Highest.</summary>
    public List<ThrottledProcess> LoweredRealtime { get; set; } = new();
    public List<DirectedThread> LoweredThreads { get; set; } = new();
    /// <summary>RTSS's global frame limit before gamemode (limit and denominator), and the limit it set.</summary>
    public uint? RtssLimitBefore { get; set; }
    public uint? RtssDenominatorBefore { get; set; }
    public uint? RtssLimitSet { get; set; }
}

/// <summary>Applies and undoes the <see cref="GamemodeSystemSettings"/>. Each step continues even if another fails (it goes to the log).</summary>
public static class GamemodeSystem
{
    public static readonly string[] UpdateServices = { "wuauserv", "UsoSvc", "DoSvc" }, SearchServices = { "WSearch" };
    static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00"), IdleDisable = new("5d76a2ca-e8c0-402f-a133-2158492d58ad");
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    static readonly HashSet<string> Restarted = new(StringComparer.OrdinalIgnoreCase);

    static string StatePath => Path.Combine(ConfigStore.Directory, "gamemode-state.json");

    /// <summary>On gamemode enable. Returns the state to undo (null = nothing was changed).</summary>
    public static GamemodeSystemState? Apply(GamemodeSystemSettings s)
    {
        if (!s.Any) return null;
        var state = new GamemodeSystemState { Background = s.BackgroundEfficiency };
        Restarted.Clear();
        Decided.Clear();
        Step("power plan", () =>
        {
            if (!s.PowerPlan || s.PowerPlanId is not { } plan) return;
            var before = Power.Active();
            if (before == plan) return;
            state.PlanBefore = before;
            state.PlanSet = plan;
            Save(state);
            Power.Activate(plan);
            Log.Info($"Gamemode: power plan {plan} (was {before}).");
        });
        Step("processor idle states", () =>
        {
            if (!s.CStatesOff) return;
            var plan = Power.Active();
            var (ac, dc) = (Power.ReadIdle(plan, ac: true), Power.ReadIdle(plan, ac: false));
            if (ac == 1 && dc == 1) return;
            (state.IdlePlan, state.IdleAcBefore, state.IdleDcBefore) = (plan, ac, dc);
            Save(state);
            Power.WriteIdle(plan, 1, 1);
            Log.Info("Gamemode: processor idle states (C-states) off.");
        });
        foreach (var name in (s.StopWindowsUpdate ? UpdateServices : Array.Empty<string>()).Concat(s.StopSearch ? SearchServices : Array.Empty<string>()))
            Step($"service {name}", () =>
            {
                if (Scm.State(name) is null) return; // doesn't exist on this installation
                state.KeepStopped.Add(name);
                if (Scm.IsRunning(name)) state.WereRunning.Add(name);
                Save(state);
                if (Scm.IsRunning(name)) { Scm.Stop(name); Log.Info($"Gamemode: service {name} stopped."); }
            });
        Step("Wi-Fi", () =>
        {
            if (!s.WifiLowLatency) return;
            var set = WifiLatency.Apply(guid => { state.Wifi.Add(guid); Save(state); });
            Log.Info(set == 0 ? "Gamemode: no connected Wi-Fi interface." : $"Gamemode: Wi-Fi background scan off and streaming mode on ({set} interface(s)).");
        });
        Step("transparency", () =>
        {
            if (!s.TransparencyOff) return;
            using var key = Registry.CurrentUser.CreateSubKey(Personalize);
            var before = key.GetValue("EnableTransparency") as int?;
            if (before == 0) return;
            state.TransparencySet = true;
            state.TransparencyBefore = before;
            Save(state);
            key.SetValue("EnableTransparency", 0, RegistryValueKind.DWord);
            Native.BroadcastSettingChange("ImmersiveColorSet");
            Log.Info("Gamemode: transparency effects off.");
        });
        Step("RTSS frame limit", () =>
        {
            if (s.RtssFrameLimit <= 0) return;
            var before = Rtss.ReadLimit();
            var want = new Rtss.FrameLimit((uint)s.RtssFrameLimit, 1);
            if (before == want) return;
            (state.RtssLimitBefore, state.RtssDenominatorBefore, state.RtssLimitSet) = (before.Limit, before.Denominator, want.Limit);
            Save(state);
            Rtss.WriteLimit(want);
            Log.Info($"Gamemode: RTSS frame limit {want} (was {before}).");
        });
        return state;
    }

    /// <summary>Every interval while gamemode is on: Windows turns on-demand services back on; stop them again.</summary>
    public static void Keep(GamemodeSystemState state)
    {
        foreach (var name in state.KeepStopped)
        {
            try
            {
                if (!Scm.IsRunning(name)) continue;
                Scm.Stop(name);
                if (Restarted.Add(name)) Log.Info($"Gamemode: service {name} was started again by Windows and was stopped (logged once).");
            }
            catch (Exception ex) { if (Restarted.Add(name)) Log.Warn($"Gamemode: service {name} couldn't be kept stopped: {ex.Message}"); }
        }
    }

    /// <summary>Voice, capture and audio-mixing programs: efficiency mode can make them stutter. Names or name prefixes (lowercase).</summary>
    static readonly string[] NeverThrottle = { "obs64.exe", "obs32.exe", "streamlabs obs.exe", "discord", "voicemeeter", "teamspeak", "ts3client" };
    /// <summary>Programs already looked at in this gamemode (pid, start): each is decided once.</summary>
    static readonly HashSet<(int Pid, long Create)> Decided = new();

    internal static bool NeverThrottled(string name) => NeverThrottle.Any(n => n.EndsWith(".exe") ? name.Equals(n, StringComparison.OrdinalIgnoreCase)
        : name.StartsWith(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every interval while gamemode is on: puts programs in the background into efficiency mode (EcoQoS: lower clocks, efficient cores) once
    /// per start. Left alone: what <paramref name="eligible"/> refuses (the engine: games and what they open, programs with a rule, other
    /// sessions, above-normal priority), Windows' own (Windows folder), the voice and capture programs above, ones that already chose their
    /// own mode, and the program in front with its other processes. One that comes to the front goes back to automatic and stays there.
    /// </summary>
    public static void Background(GamemodeSystemState state, IReadOnlyList<ProcessEntry> snapshot, IReadOnlyDictionary<int, long> alive, Func<ProcessEntry, bool> eligible)
    {
        var front = Native.ForegroundPid();
        var frontName = front == 0 ? null : snapshot.FirstOrDefault(p => p.Pid == front)?.Name;
        var changed = state.Throttled.RemoveAll(t => !alive.TryGetValue(t.Pid, out var c) || c != t.Create) > 0;
        Decided.RemoveWhere(d => !alive.TryGetValue(d.Pid, out var c) || c != d.Create);
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        var added = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in snapshot)
        {
            var key = (p.Pid, p.CreateTime);
            if (frontName is not null && p.Name.Equals(frontName, StringComparison.OrdinalIgnoreCase))
            {
                Decided.Add(key);
                if (state.Throttled.FindIndex(t => t.Pid == p.Pid && t.Create == p.CreateTime) is var i and >= 0)
                {
                    ProcessControl.SwitchEfficiency(p.Pid, p.CreateTime, Efficiency.On, Efficiency.Auto);
                    state.Throttled.RemoveAt(i);
                    changed = true;
                }
                continue;
            }
            if (!Decided.Add(key) || p.Pid is 0 or 4 || p.Pid == Environment.ProcessId || NeverThrottled(p.Name) || !eligible(p)) continue;
            // No path (protected processes): can't tell if it's a Windows one, so it stays.
            if (ProcessControl.ImagePath(p.Pid) is not { } path || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ProcessControl.SwitchEfficiency(p.Pid, p.CreateTime, Efficiency.Auto, Efficiency.On)) continue;
            state.Throttled.Add(new ThrottledProcess { Pid = p.Pid, Create = p.CreateTime });
            added.Add(p.Name);
            changed = true;
        }
        if (changed) Save(state);
        if (added.Count > 0) Log.Info($"Gamemode: efficiency mode on {string.Join(", ", added)}.");
    }

    /// <summary>
    /// On disable: undoes in reverse order, and only what is still as gamemode left it (if the user changed the power plan
    /// during the game, their change stays).
    /// </summary>
    public static void Restore(GamemodeSystemState state)
    {
        Step("RTSS frame limit", () =>
        {
            if (state.RtssLimitSet is not { } set || Rtss.ReadLimit() != new Rtss.FrameLimit(set, 1)) return;
            var before = new Rtss.FrameLimit(state.RtssLimitBefore ?? 0, state.RtssDenominatorBefore ?? 1);
            Rtss.WriteLimit(before);
            Log.Info($"Gamemode: RTSS frame limit back to {before}.");
        });
        Step("thread director", () => ThreadDirector.Restore(state));
        Step("efficiency mode", () =>
        {
            var restored = state.Throttled.Count(t => ProcessControl.SwitchEfficiency(t.Pid, t.Create, Efficiency.On, Efficiency.Auto));
            if (state.Throttled.Count > 0) Log.Info($"Gamemode: efficiency mode back to automatic on {restored} background program(s) ({state.Throttled.Count - restored} closed or changed since).");
        });
        Step("background priority", () =>
        {
            var restored = state.Restrained.Count(t => Restraint.Release(t.Pid, t.Create));
            if (state.Restrained.Count > 0) Log.Info($"Gamemode: Normal priority back on {restored} background program(s) ({state.Restrained.Count - restored} closed or changed since).");
        });
        Step("Wi-Fi", () =>
        {
            if (state.Wifi.Count == 0) return;
            WifiLatency.Restore(state.Wifi);
            Log.Info("Gamemode: Wi-Fi background scan and streaming mode back to normal.");
        });
        Step("transparency", () =>
        {
            if (!state.TransparencySet) return;
            using var key = Registry.CurrentUser.CreateSubKey(Personalize);
            if (key.GetValue("EnableTransparency") as int? != 0) return;
            if (state.TransparencyBefore is { } v) key.SetValue("EnableTransparency", v, RegistryValueKind.DWord);
            else key.DeleteValue("EnableTransparency", false);
            Native.BroadcastSettingChange("ImmersiveColorSet");
            Log.Info("Gamemode: transparency effects restored.");
        });
        foreach (var name in state.WereRunning.Where(n => UpdateServices.Contains(n) || SearchServices.Contains(n)))
            Step($"service {name}", () =>
            {
                if (Scm.IsRunning(name)) return;
                Scm.Start(name);
                Log.Info($"Gamemode: service {name} started again.");
            });
        Step("processor idle states", () =>
        {
            if (state.IdlePlan is not { } plan || Power.ReadIdle(plan, ac: true) != 1) return;
            Power.WriteIdle(plan, state.IdleAcBefore ?? 0, state.IdleDcBefore ?? 0);
            Log.Info("Gamemode: processor idle states restored.");
        });
        Step("power plan", () =>
        {
            if (state.PlanBefore is not { } before || Power.Active() != state.PlanSet) return;
            Power.Activate(before);
            Log.Info($"Gamemode: power plan {before} restored.");
        });
        try
        {
            // A folder that is a link (or not the administrators') could point the delete at another file.
            if (ConfigStore.FolderProblem() is { } problem) Log.Warn($"Gamemode: {StatePath} wasn't deleted: {problem}");
            else File.Delete(StatePath);
        }
        catch (Exception ex) { Log.Warn($"Gamemode: {StatePath} wasn't deleted: {ex.Message}"); }
    }

    /// <summary>Engine starting: a saved state belongs to a gamemode that didn't turn off (crash, PC shut down). Undo it.</summary>
    public static void RecoverAtStart()
    {
        // After the journal: RTSS's shared memory is the one step here that another program in the session controls.
        try { RestoreJournal(); }
        finally { Step("RTSS on-screen display", () => { if (Rtss.ReleaseOsd() > 0) Log.Info("RTSS on-screen display: the slot left by the last session was released."); }); }
    }

    static void RestoreJournal()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            // The elevated engine acts on this file at start: the same folder and owner checks as config.json.
            if ((ConfigStore.FolderProblem() ?? ConfigStore.OwnerProblem(StatePath)) is { } problem) { Log.Warn($"Gamemode: {problem}"); return; }
            var state = JsonSerializer.Deserialize(File.ReadAllText(StatePath), GovernorJson.Default.GamemodeSystemState);
            Log.Warn("Gamemode: the last session ended with gamemode on; restoring its system changes.");
            if (state is not null) Restore(state);
            else File.Delete(StatePath);
        }
        catch (Exception ex) { Log.Error("Restoring the last gamemode's system changes failed", ex); }
    }

    internal static void Save(GamemodeSystemState state)
    {
        ConfigStore.PrepareDirectory();
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, GovernorJson.Default.GamemodeSystemState));
    }

    internal static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn($"Gamemode: {what} failed: {ex.Message}"); }
    }

    // ---------- power plan (PowrProf) ----------
    internal static class Power
    {
        public static Guid Active()
        {
            Check(PowerGetActiveScheme(IntPtr.Zero, out var ptr), "PowerGetActiveScheme");
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }

        public static void Activate(Guid plan) => Check(PowerSetActiveScheme(IntPtr.Zero, ref plan), "PowerSetActiveScheme");

        public static uint ReadIdle(Guid plan, bool ac)
        {
            var (sub, setting) = (SubProcessor, IdleDisable);
            Check(ac ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out var v) : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out v),
                "PowerReadValueIndex");
            return v;
        }

        /// <summary>Writes AC and DC and reactivates the plan (if it is the active one) so Windows applies it right away.</summary>
        public static void WriteIdle(Guid plan, uint ac, uint dc)
        {
            var (sub, setting) = (SubProcessor, IdleDisable);
            Check(PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, ac), "PowerWriteACValueIndex");
            Check(PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, dc), "PowerWriteDCValueIndex");
            if (Active() == plan) Activate(plan);
        }

        static void Check(uint error, string fn)
        {
            if (error != 0) throw new Win32Exception((int)error, $"{fn}: {new Win32Exception((int)error).Message}");
        }

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    }

    // ---------- services (SCM) ----------
    static class Scm
    {
        const uint Connect = 0x0001, QueryStatus = 0x0004, StartAccess = 0x0010, StopAccess = 0x0020, ControlStop = 1;
        const uint Stopped = 1, StartPending = 2, Running = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

        public static uint? State(string name) => With(name, QueryStatus, h => QueryServiceStatus(h, out var s) ? s.State : (uint?)null, missingIsNull: true);
        public static bool IsRunning(string name) => State(name) is Running or StartPending;

        public static void Stop(string name) => With<object?>(name, StopAccess, h =>
        {
            if (!ControlService(h, ControlStop, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return null;
        });

        public static void Start(string name) => With<object?>(name, StartAccess, h =>
        {
            if (!StartService(h, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(Marshal.GetLastWin32Error()); // 1056: already running
            return null;
        });

        static T? With<T>(string name, uint access, Func<IntPtr, T?> action, bool missingIsNull = false)
        {
            var scm = OpenSCManager(null, null, Connect);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(scm, name, access);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (missingIsNull && error == 1060) return default; // ERROR_SERVICE_DOES_NOT_EXIST
                    throw new Win32Exception(error);
                }
                try { return action(service); }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(scm); }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(IntPtr service, uint count, IntPtr args);
    }

    // ---------- memory ----------

    /// <summary>Gamemode's memory clean (the engine calls it <see cref="GamemodeSystemSettings.CleanMemoryDelaySeconds"/> after turning on).</summary>
    public static void CleanMemory(IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, bool> keep) =>
        Step("memory", () => Log.Info($"Gamemode: {Memory.Clean(snapshot, keep)}"));

    /// <summary>Standby list and free memory in MB; null when Windows doesn't say (the engine lacks the privilege).</summary>
    public static (long StandbyMb, long FreeMb)? MemoryLists() => Memory.Lists();

    /// <summary>Purges the standby list (the file cache): running programs keep their memory.</summary>
    public static bool PurgeStandbyList(string why)
    {
        var status = Memory.Purge();
        if (status == 0) Log.Info($"Gamemode: standby list purged ({why}).");
        else Log.Warn($"Gamemode: the standby list wasn't purged: 0x{status:X8}.");
        return status == 0;
    }

    /// <summary>Anti-cheats run beside the game, and emptying their memory while the game loads can make them end it. Name prefixes, lowercase.</summary>
    static readonly string[] AntiCheats = { "easyanticheat", "eaanticheat", "eac_", "beservice", "battleye", "vgc", "vgtray", "vgk", "faceit", "gameguard", "gamemon",
        "pnkbstr", "xigncode", "sguard", "acesvc", "ace-", "ucldr", "zakynthos", "xhunter", "nprotect", "atvi-", "randgrid" };

    public static bool IsAntiCheat(string name) => AntiCheats.Any(a => name.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    static class Memory
    {
        const uint QueryLimited = 0x1000, SetQuota = 0x0100;
        const int SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4, ListSize = 22 * 8;

        /// <summary>
        /// Empties the working set of open programs, except what <paramref name="keep"/> keeps (games, what they open and what opened them,
        /// anti-cheats), those in the Windows folder (explorer, winlogon, dwm, svchost... get slow after being emptied) and the governor itself;
        /// then clears the standby list (like ISLC).
        /// </summary>
        public static string Clean(IReadOnlyList<ProcessEntry> snapshot, Func<ProcessEntry, bool> keep)
        {
            var before = AvailableMb();
            var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
            int trimmed = 0, failed = 0;
            foreach (var p in snapshot)
            {
                if (p.Pid is 0 or 4 || p.Pid == Environment.ProcessId || keep(p) || IsAntiCheat(p.Name)) continue;
                using var h = Native.OpenProcess(QueryLimited | SetQuota, false, p.Pid);
                if (h.IsInvalid || !Affinity.Same(h, p.CreateTime)) { failed++; continue; }
                // No path (Registry, Memory Compression, protected ones): can't tell if it's a Windows one, so it stays.
                if (ProcessControl.ImagePath(p.Pid) is not { } path || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (SetProcessWorkingSetSizeEx(h, (IntPtr)(-1), (IntPtr)(-1), 0)) trimmed++; else failed++;
            }
            var purge = Purge();
            var after = AvailableMb();
            return $"memory cleaned: {trimmed} program(s) trimmed ({failed} not accessible), standby list {(purge == 0 ? "purged" : $"not purged (0x{purge:X8})")}; available {before:N0} → {after:N0} MB.";
        }

        public static int Purge()
        {
            var command = MemoryPurgeStandbyList;
            return NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        }

        /// <summary>SYSTEM_MEMORY_LIST_INFORMATION: zero, free, modified, modified-no-write, bad, standby by priority[8], repurposed[8], modified pagefile.</summary>
        public static (long StandbyMb, long FreeMb)? Lists()
        {
            var buffer = Marshal.AllocHGlobal(ListSize);
            try
            {
                if (Native.NtQuerySystemInformation(SystemMemoryListInformation, buffer, ListSize, out _) < 0) return null;
                long Page(int i) => Marshal.ReadInt64(buffer, i * 8);
                long standby = 0;
                for (var i = 5; i < 13; i++) standby += Page(i);
                const long PagesPerMb = 1024 * 1024 / 4096;
                return (standby / PagesPerMb, (Page(0) + Page(1)) / PagesPerMb);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        static long AvailableMb()
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.AvailPhys / (1024 * 1024)) : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessWorkingSetSizeEx(Microsoft.Win32.SafeHandles.SafeProcessHandle h, IntPtr min, IntPtr max, uint flags);
        [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    }
}
