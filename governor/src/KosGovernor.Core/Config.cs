using System.Text.Json.Serialization;

namespace KosGovernor.Core;

public enum RuleAction { Apply, Suspend, Terminate }
public enum CpuPriority { Idle, BelowNormal, Normal, AboveNormal, High, Realtime }
public enum ThreadPriority { Idle, Lowest, BelowNormal, Normal, AboveNormal, Highest, TimeCritical }
public enum IoPriority { VeryLow, Low, Normal, High }
public enum MemoryPriority { VeryLow, Low, Medium, BelowNormal, Normal }
public enum Efficiency { Off, On, Auto }
/// <summary>GPU scheduling priority class of a process (D3DKMT_SCHEDULINGPRIORITYCLASS: the numbers are the kernel's).</summary>
public enum GpuPriority { Idle = 0, Normal = 1, High = 2, Realtime = 3, BelowNormal = 4, AboveNormal = 5 }

/// <summary>What every rule can change. Null = leave unchanged.</summary>
public abstract class RuleBase
{
    public RuleAction Action { get; set; }
    public IoPriority? Io { get; set; }
    public MemoryPriority? Memory { get; set; }
    /// <summary>Windows priority boost on (true) or off (false).</summary>
    public bool? Boost { get; set; }
    /// <summary>See <see cref="CpuSpec"/>.</summary>
    public string? Affinity { get; set; }
    public string? CpuSets { get; set; }

    [JsonIgnore] public virtual bool IsEmpty => Action == RuleAction.Apply && Io is null && Memory is null && Boost is null && Affinity is null && CpuSets is null;
}

public abstract class ProcessLevelRule : RuleBase
{
    public CpuPriority? Priority { get; set; }
    public Efficiency? Efficiency { get; set; }
    public GpuPriority? GpuPriority { get; set; }
    [JsonIgnore] public override bool IsEmpty => base.IsEmpty && Priority is null && Efficiency is null && GpuPriority is null;
}

/// <summary>Per-executable rule, with per-service rules (+S, svchost processes) and per-thread rules (+T).</summary>
public sealed class ProcessRule : ProcessLevelRule
{
    public string Exe { get; set; } = "";
    /// <summary>Programs this one opens (a launcher's game, for example) get the same settings. "Apply" rules only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IncludeChildren { get; set; }
    /// <summary>Waits this long after the program starts before applying (games and anti-cheats that check while loading). 0 = right away.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int DelaySeconds { get; set; }
    /// <summary>Power plan while the program runs (back to the previous one when its last instance closes). Gamemode's plan wins.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? PowerPlan { get; set; }
    /// <summary>Takes cores away from the program while it uses too much CPU.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CpuLimit? CpuLimit { get; set; }
    /// <summary>Screen mode while the program runs (a game's stretched resolution); the previous mode comes back when it closes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DisplayModeSetting? DisplayMode { get; set; }
    /// <summary>Something the engine does besides the process settings (an otherwise empty rule still counts).</summary>
    [JsonIgnore] public bool HasExtras => PowerPlan is not null || CpuLimit is not null || DisplayMode is not null;
    public List<ServiceRule> Services { get; set; } = new();
    public List<ThreadRule> Threads { get; set; } = new();
}

/// <summary>Applies to the process that hosts the service (each svchost usually hosts only one). "Apply" only.</summary>
public sealed class ServiceRule : ProcessLevelRule
{
    public string Service { get; set; } = "";
}

/// <summary>Threads by description (thread name) and/or by the module where they start.</summary>
public sealed class ThreadRule : RuleBase
{
    public string? Description { get; set; }
    public string? Module { get; set; }
    public ThreadPriority? Priority { get; set; }
    public int? IdealCpu { get; set; }
    [JsonIgnore] public override bool IsEmpty => base.IsEmpty && Priority is null && IdealCpu is null;
}

/// <summary>CPU limiter: above <see cref="Percent"/> % of the whole CPU for <see cref="OverSeconds"/> s, the program loses <see cref="Cores"/>
/// logical processors for <see cref="ForSeconds"/> s.</summary>
public sealed class CpuLimit
{
    public int Percent { get; set; } = 50;
    public int OverSeconds { get; set; } = 10;
    public int Cores { get; set; } = 2;
    public int ForSeconds { get; set; } = 30;
}

/// <summary>Resolution of the main screen; a null refresh rate keeps the current one (or the highest the mode has).</summary>
public sealed class DisplayModeSetting
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int? RefreshHz { get; set; }
    public override string ToString() => $"{Width}×{Height}{(RefreshHz is { } hz ? $" @ {hz} Hz" : "")}";
}

public sealed class StartEntry
{
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
}

public sealed class GamemodePhase
{
    public List<ProcessRule> Rules { get; set; } = new();
    /// <summary>Programs opened (as a standard user, not elevated) on the switch.</summary>
    public List<StartEntry> Start { get; set; } = new();
}

public sealed class Profile
{
    public string Name { get; set; } = "Profile 1";
    /// <summary>When gamemode turns on or off by itself (game detected), also opens the On Enable/On Disable programs.</summary>
    public bool AutoOpensPrograms { get; set; }
    /// <summary>Windows changes while gamemode is on (power plan, services, transparency...).</summary>
    public GamemodeSystemSettings System { get; set; } = new();
    public List<ProcessRule> Rules { get; set; } = new();
    /// <summary>Rules active while gamemode is on (on top of the profile's) and programs opened on enable.</summary>
    public GamemodePhase OnEnable { get; set; } = new();
    /// <summary>Applied once to the open programs when gamemode turns off, and programs opened on disable.</summary>
    public GamemodePhase OnDisable { get; set; } = new();
}

public sealed class Hotkey
{
    public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
    public uint Modifiers { get; set; } = Ctrl | Alt;
    /// <summary>Virtual-key code (VK_*).</summary>
    public uint Key { get; set; } = 0x47; // G
}

/// <summary>Automatic gamemode: turns on when a game from the list opens and off after the last one closes.</summary>
public sealed class AutoGamemodeSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Wait after the last game closes (launchers that reopen the game, crash and relaunch).</summary>
    public int OffDelaySeconds { get; set; } = 15;
    /// <summary>User changes to the base list: games added (IsBlacklisted = false) and "never a game" (true).</summary>
    public List<GameEntry> Games { get; set; } = new();
}

public enum TimerMode { Off, Gamemode, Always }

/// <summary>System timer resolution the engine asks for (NtSetTimerResolution), in 100 ns units: 5000 = 0.5 ms.</summary>
public sealed class TimerSettings
{
    public TimerMode Mode { get; set; }
    public int Resolution { get; set; } = 5000;
}

public sealed class GeneralSettings
{
    public List<string> HiddenProcessColumns { get; set; } = new();
    public List<string> HiddenThreadColumns { get; set; } = new();
    public int GuiIntervalMs { get; set; } = 500;
    /// <summary>Engine interval (tray): how often new programs are checked.</summary>
    public int EngineIntervalMs { get; set; } = 3000;
    /// <summary>Records the gamemode session report (CPU, memory, GPU and what interfered) while gamemode is on.</summary>
    public bool SessionReport { get; set; } = true;
}

public sealed class GovernorConfig
{
    /// <summary>2: the engine's default interval went from 2 s to 3 s. 3: KOS default rules (<see cref="DefaultRules"/>).</summary>
    public int Version { get; set; } = 3;
    public List<Profile> Profiles { get; set; } = new() { new Profile { Rules = DefaultRules.Create() } };
    public int ActiveProfile { get; set; }
    public List<CpuMask> Masks { get; set; } = new();
    public Hotkey Hotkey { get; set; } = new();
    public AutoGamemodeSettings AutoGamemode { get; set; } = new();
    public TimerSettings Timer { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    [JsonIgnore] public Profile Active => Profiles[Math.Clamp(ActiveProfile, 0, Profiles.Count - 1)];
}

/// <summary>Names of the objects shared between the interface and the engine (same session).</summary>
public static class Names
{
    public const string EngineMutex = @"Local\KosGovernor.Engine", UiMutex = @"Local\KosGovernor.Ui";
    public const string Stop = @"Local\KosGovernor.Stop", Reload = @"Local\KosGovernor.Reload", ShowUi = @"Local\KosGovernor.ShowUi";
    public const string ToggleGamemode = @"Local\KosGovernor.ToggleGamemode";
    /// <summary>Exists while gamemode is on (the engine keeps the event open).</summary>
    public const string GamemodeOn = @"Local\KosGovernor.GamemodeOn";

    public static bool Exists(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var e)) return false;
        e.Dispose();
        return true;
    }

    public static bool EngineRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(EngineMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
    }

    /// <summary>Signals an engine event (if it is running).</summary>
    public static void Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var e)) return;
        using (e) e.Set();
    }
}
