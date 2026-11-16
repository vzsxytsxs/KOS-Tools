namespace KosToolbox.Core.Text;

public static partial class Txt
{
    // ---------- sources ----------
    public const string SourceRunKeys = "Run and RunOnce registry keys", SourcePolicyRun = "Policy Run keys", SourceStartupFolders = "Startup folders",
        SourceScheduledTasks = "Scheduled tasks (startup and logon triggers)", SourceServices = "Services", SourceDrivers = "Drivers";
    public const string SourceRunHkcu = "Run (HKCU)", SourceRunOnceHkcu = "RunOnce (HKCU)", SourceRunHklm = "Run (HKLM)",
        SourceRunHklm32 = "Run (HKLM 32-bit)", SourceRunOnceHklm = "RunOnce (HKLM)", SourceRunOnceHklm32 = "RunOnce (HKLM 32-bit)",
        SourcePolicyHkcu = "Policy Run (HKCU)", SourcePolicyHklm = "Policy Run (HKLM)",
        SourceStartupFolderUser = "Startup folder (user)", SourceStartupFolderAll = "Startup folder (all users)",
        SourceTask = "Scheduled task", SourceService = "Service", SourceDriver = "Driver";
    public const string GroupLogon = "Logon", GroupTasks = "Scheduled tasks", GroupServices = "Services", GroupDrivers = "Drivers", GroupHooks = "System hooks";
    public const string SourceHooks = "System hooks (AppInit DLLs, image hijacks, boot execute, LSA, print monitors, known DLLs, Winsock)", SourceWmi = "WMI subscriptions";
    public const string SourceAppInit = "AppInit DLLs", SourceIfeo = "Image hijack (IFEO)", SourceBootExecute = "Boot execute", SourceLsa = "LSA package",
        SourcePrintMonitors = "Print monitor", SourceKnownDlls = "Known DLL", SourceWinsock = "Winsock provider";
    public const string TypeAppInit = "Loaded into every program", TypeIfeo = "Runs in place of the program", TypeBootExecute = "Before Windows starts",
        TypeLsa = "Loaded by LSA at boot", TypePrintMonitor = "Loaded by the print spooler", TypeKnownDll = "Loaded from System32 for every program",
        TypeWinsock = "Loaded by programs that use the network", TypeWmiCommand = "WMI event: runs a command", TypeWmiScript = "WMI event: runs a script";
    public const string AppInitOn = "LoadAppInit_DLLs is 1: Windows loads it into every program that uses user32 (unless Secure Boot is on).",
        AppInitOff = "LoadAppInit_DLLs isn't 1: Windows doesn't load it.";
    public static string IfeoDetail(string exe) => $"Starting {exe} runs this command instead, with {exe} as its argument.";
    public static string IfeoKos(string exe) => $"Set by KOS: {exe} doesn't start (Windows runs systray.exe, which exits). The toolbox setting that turns the feature back on removes it.";
    public const string BootExecuteDetail = "Run by the session manager before Windows starts (autocheck autochk * is Windows' own disk check).";
    public static string LsaDetail(string value) => $"Listed in {value}: lsass loads it at boot.";
    public const string PrintMonitorDetail = "The print spooler loads it when it starts.", KnownDllDetail = "Windows always loads this name from System32, never from a program's folder.";
    public const string WinsockDetail = "A Winsock transport or name-space provider: loaded into every program that uses the network.";
    public const string WmiDetail = "A permanent WMI event consumer: it runs when its event filter fires, with no Run entry or task.";
    public const string HooksReadOnly = "Shown for reference: these belong to Windows or to the program that added them, and a wrong change can stop Windows from starting or signing in.";
    public const string WordService = "service", WordDriver = "driver";

    // ---------- startup types ----------
    public const string TypeLogon = "At logon", TypeRunOnce = "Once, at next logon", TypeAtStartup = "At startup", TypeAtLogon = "At logon";
    public static string TriggerDisabled(string trigger) => $"{trigger} (trigger disabled)";

    /// <summary>Start value of services and drivers (0–4).</summary>
    public static string StartName(int start, bool delayed, bool trigger)
    {
        var name = start switch
        {
            0 => "Boot",
            1 => "System",
            2 => delayed ? "Automatic (delayed start)" : "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => $"Unknown ({start})",
        };
        return trigger && start is 2 or 3 ? name + ", trigger start" : name;
    }

    public static string StartDetail(int start) => start == 4
        ? "Startup type is Disabled (Start = 4), so Windows won't start it."
        : $"Startup type allows it to start (Start = {start}). Whether it's running now is shown separately.";
    public const string StartMissing = "The Start value is missing or invalid, so the startup type is unknown.";

    public static string RunStateName(uint state) => state switch
    {
        1 => "Stopped",
        2 => "Start pending",
        3 => "Stop pending",
        4 => "Running",
        5 => "Continue pending",
        6 => "Pause pending",
        7 => "Paused",
        _ => $"Unknown ({state})",
    };

    public static string ErrorControlName(int value) => value switch
    {
        0 => "Ignore (0)",
        1 => "Normal (1)",
        2 => "Severe (2)",
        3 => "Critical (3)",
        _ => value.ToString(),
    };

    public static string TaskState(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    // ---------- state and details ----------
    public const string ApprovedAbsent = "No StartupApproved flag, so it's enabled (Windows default).";
    public static string ApprovedFlag(byte flag, bool enabled) =>
        $"StartupApproved flag 0x{flag:X2}: {(enabled ? "enabled" : "disabled")} (the same flag Task Manager uses).";
    public const string ApprovedUnexpected = "The StartupApproved value has an unexpected type, so the state is unknown.";
    public static string ApprovedReadError(string message) => $"Couldn't read StartupApproved: {message}";
    public const string RunValueNotText = "The value isn't text, so Windows can't run it as a command. State unknown.";
    public const string RunOnceDetail = "RunOnce entries run once at the next logon and are then removed by Windows.";
    public const string RunOnceReadOnly = "RunOnce entries have no enable/disable flag; Windows removes them after they run.";
    public const string SourceActiveSetup = "Active Setup", SourceActiveSetup32 = "Active Setup (32-bit)", SourceWinlogon = "Winlogon";
    public const string TypeActiveSetup = "Once per user, at sign-in", TypeWinlogon = "Every sign-in", TypeOnDemand = "On demand only";
    public static string ActiveSetupDetail(bool installed) => installed
        ? "IsInstalled isn't 0: it runs for each user who hasn't run this version yet."
        : "IsInstalled is 0: Windows skips it.";
    public const string ActiveSetupReadOnly = "Shown for reference: Active Setup entries belong to the programs that installed them and aren't switched here.";
    public const string WinlogonDetail = "Started by Winlogon at every sign-in.";
    public const string WinlogonReadOnly = "Shown for reference: a wrong Winlogon value can stop Windows from signing in, so it isn't changed here.";
    public static string TriggerKind(int type) => type switch
    {
        0 => "On an event", 1 => "At a set time", 2 => "Daily", 3 => "Weekly", 4 => "Monthly", 5 => "Monthly (day of the week)",
        6 => "When idle", 7 => "When created or changed", 11 => "On lock, unlock or connection", 12 => "Custom trigger",
        _ => $"Trigger type {type}",
    };
    public const string PolicyDetail = "Set by policy; runs at every logon. Policy entries have no enable/disable flag.";
    public const string PolicyReadOnly = "Managed by policy. Change it in Group Policy or remove the value from the policy key.";
    public const string DefaultValueName = "(Default)";
    public const string ShortcutBroken = "The shortcut's target doesn't exist.";
    public const string ShortcutNoTarget = "The shortcut has no file target.";
    public static string ShortcutBrokenTarget(string target) => $"Broken shortcut: {target} doesn't exist.";
    public static string ShortcutUnreadable(string message) => $"Couldn't read the shortcut: {message}";
    public const string TaskSchedulerMissing = "The Task Scheduler service isn't available.";
    public static string TaskComHandler(string classId) => $"COM handler {classId}";
    public static string TaskOtherAction(int type) => $"Action type {type}";
    public static string TaskEnabledDetail(bool enabled) => enabled
        ? "The task is enabled in Task Scheduler."
        : "The task is disabled in Task Scheduler; its triggers and actions are kept.";
    public static string TasksSkipped(int count) => $"{count} scheduled tasks couldn't be read. Run the toolbox as administrator to include them.";
    public static string SourceFailed(string source, string message) => $"{source} couldn't be read: {message}";
    public const string ServiceKeyMissing = "The service's registry key wasn't found.";
    public static string ServiceKeyUnreadable(string message) => $"Couldn't read the service's registry key: {message}";
    public const string DriverDefaultImage = @"(no ImagePath; Windows loads System32\drivers\<name>.sys)";
    public const string CriticalBootStart = "Loaded during boot (Boot or System start). Disabling it can stop Windows from starting.";
    public const string CriticalErrorControl = "Windows treats a failure to start it as severe or critical (ErrorControl 2 or 3). Disabling it can stop Windows from starting or force the last known good configuration.";
    public const string ChangeNotConfirmed = "The change wasn't confirmed when the system was read again.";
    public const string StateUnknownCantChange = "The current state is unknown, so it can't be changed safely.";

    public const string DetailRegistryValue = "Registry value", DetailRegistryKey = "Registry key", DetailFile = "File",
        DetailShortcutTarget = "Shortcut target", DetailArguments = "Arguments", DetailWorkingDirectory = "Start in",
        DetailTaskPath = "Task path", DetailServiceName = "Service name", DetailDisplayName = "Display name (from Windows)",
        DetailServiceDll = "Service DLL", DetailErrorControl = "Error control", DetailPreviousStart = "Startup type saved by the toolbox";

    // ---------- warnings after disabling ----------
    public static string CriticalWarning(string kind, string reason) => $"This {kind} is critical. {reason}";
    public static string WindowsComponentWarning(string kind) => $"This {kind} is part of Windows. Disabling it can break Windows features that depend on it.";
}
