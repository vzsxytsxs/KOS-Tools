using KosToolbox.Core.Tweaks;

namespace KosToolbox.Core.Text;

/// <summary>
/// All the text the toolbox displays, in English and in one place (core and UI; the XAML uses
/// {x:Static t:Txt.Name}). The UI doesn't depend on the Windows language. Proper names, paths, commands,
/// GUIDs and raw system messages are shown as they come, always accompanied by an explanation from here.
/// </summary>
public static partial class Txt
{
    // ---------- standard terms ----------
    public const string Enabled = "Enabled", Disabled = "Disabled", Partial = "Partial", NotConfigured = "Not configured",
        NotConfiguredOption = "Not configured", Unknown = "Unknown", Unavailable = "Unavailable", ReadError = "Read error",
        RestartRequired = "Restart required", Active = "Active", Running = "Running", Stopped = "Stopped";
    public const string Apply = "Apply", Discard = "Discard", Restore = "Restore", Refresh = "Refresh", Open = "Open", OpenFolder = "Open folder",
        Cancel = "Cancel", Ok = "OK", Close = "Close", Run = "Run", Restart = "Restart", Standard = "Standard", Advanced = "Advanced",
        Enable = "Enable", Disable = "Disable", CreateSnapshot = "Create snapshot", Details = "Details",
        ViewDetails = "View details";
    public const string NotFound = "Not found";
    public static string ReadErrorWith(string message) => $"Read error: {message}";

    // ---------- tweak state ----------
    public const string NoComponentsPresent = "None of the components this setting uses exist on this installation.";
    public const string NotConfiguredDetail = "None of the values this setting uses are present, and no option is defined by their absence. Windows uses its built-in behavior.";
    public const string UnknownDetail = "Values are present, but they don't match any option.";
    public static string ReadFailed(string display, string message) => $"Couldn't read {display}: {message}";
    public static string PartialDetail(int hits, int total, string label) => $"{hits} of {total} values match \"{label}\"; the others are missing or different.";

    /// <summary>Short name of the state; <paramref name="label"/> is the option when there is a match.</summary>
    public static string StatusName(TweakStatus status, string? label) => status switch
    {
        TweakStatus.Matched => label ?? Unknown,
        TweakStatus.Partial => Partial,
        TweakStatus.NotConfigured => NotConfigured,
        TweakStatus.Unavailable => Unavailable,
        TweakStatus.ReadError => ReadError,
        _ => Unknown,
    };

    public static string CantApplyReadError(string? detail) => $"The current state couldn't be read, so nothing was changed. {detail}".TrimEnd();
    public static string AlreadySet(string label) => $"Already set to \"{label}\".";
    public static string AppliedRestart(string label) => $"Set to \"{label}\" and verified. Restart required.";
    public static string AppliedVerified(string label) => $"Set to \"{label}\" and verified.";
    public static string NotConfirmed(string state) => $"The change wasn't confirmed when the system was read again. Current state: {state}. Details are in the log.";
    public const string NoSavedState = "There is no saved state to restore.";
    public const string CustomStateScriptRestore = "The saved state doesn't match any option, and this setting is applied by a script. Choose an option instead.";
    public const string CustomStateNeedsAdmin = "Restoring custom values needs the toolbox to run as administrator (Settings › Restart as administrator).";
    public const string ValuesRestored = "Values restored and verified.";
    public static string SomeValuesNotRestored(string list) => $"Some values weren't restored: {list}";
    public const string ActionNotImplemented = "This action has no implementation.";
    public const string Done = "Done.";
    public const string DoneRestart = "Done. Restart required.";
    public const string ScriptNonZero = "The script returned a nonzero exit code; see the log.";
    // ---------- anti-cheats (after turning VBS or memory integrity off) ----------
    public const string AntiCheatVanguard = "Riot Vanguard is installed: Riot can ask for memory integrity, which needs VBS (error VAN: RESTRICTION: 5).";
    public const string AntiCheatFaceitVbs = "FACEIT is installed: FACEIT requires VBS and IOMMU on some accounts.";
    public const string AntiCheatFaceitHvci = "FACEIT is installed: it asks for memory integrity only on some accounts, and tells you when it does.";

    public const string IntelOnly = "Available only on Intel processors.";
    public const string AmdOnly = "Available only on AMD processors.";
    /// <summary>Why Windows refuses the RW driver (RwDrv.sys); null when it can load.</summary>
    public static string RwBlocked(string reason) => $"Windows blocks the RW driver this needs: {reason}. Turn that off on the Security tab first.";
    public const string RwBlocklistOn = "the vulnerable driver blocklist is on", RwVbsRunning = "virtualization-based security (VBS) is running",
        RwHvciRunning = "memory integrity (HVCI) is running";

    // ---------- Start menu ----------
    public const string StartSearchOpenShell = "Open-Shell is the active Start menu and has its own search. To change this, set \"Start menu\" to Windows first.";
    public const string StartSearchNoWindowsMenu = "The Windows Start menu is off (StartMenuExperienceHost.exe is renamed or missing), so its search isn't used.";
    public const string StartMenuHostMissing = "The Windows Start menu app (StartMenuExperienceHost) isn't on this installation.";
    public const string StartMenuHostBoth = "Both StartMenuExperienceHost.exe and StartMenuExperienceHost.old exist, so it can't be told whether the Windows Start menu is on. Remove the extra file first.";
    public const string OpenShellMissing = "Open-Shell isn't installed (KOS installs it on first boot).";

    // ---------- other settings ----------
    public const string FirewallRulesMissing = "None of these firewall rules exist (the Advanced profile deletes every rule).";
    public const string NoDisks = "No internal disks were found.";
    public static string FileMissing(string path) => $"{path} isn't on this installation.";
    public static string RenamedBoth(string path) => $"Both {Path.GetFileName(path)} and its .old copy exist, so it can't be told whether it's on. Remove the extra file first.";

    // ---------- elevation ----------
    public static string ElevatedRefused(string logs) => $"The elevated process refused the request. Details are in the log ({logs}).";
    public static string ElevatedFailed(int code, string logs) => $"The elevated task failed (exit code {code}). Details are in the log ({logs}).";

    // ---------- folders and actions ----------
    public static string FolderNotFound(string path) => $"Folder not found: {path}";
    public const string FolderOpened = "Folder opened.";
    public const string ExplorerRestarted = "Explorer restarted.";
    public const string ExplorerNotBack = "Explorer didn't come back on its own. Start it from Task Manager (the elevated toolbox shouldn't start it).";
    public static string EventLogsCleared(int count) => $"{count} event logs cleared.";
    public static string EventLogsPartial(int ok, int total, IEnumerable<string> failed) =>
        $"{ok} of {total} event logs cleared. Not cleared: {string.Join("; ", failed)}";

    // ---------- Home ----------
    public const string InfoKosVersion = "KOS version", InfoPostSetup = "Post-setup";
    public const string RebootComponentServicing = "Windows component servicing";
    public const string RebootWindowsUpdate = "Windows Update";
    public const string RebootPendingFiles = "Files waiting to be replaced";
    public static string RebootCheckFailed(string message) => $"Couldn't check ({message})";
}
