using KosToolbox.Core.Startup;

namespace KosToolbox.Core.Text;

/// <summary>UI texts (window, pages, dialogs). The XAML uses {x:Static t:Txt.Name}.</summary>
public static partial class Txt
{
    // ---------- window and navigation ----------
    public const string WindowTitle = "KOS Toolbox", WindowTitleAdmin = "KOS Toolbox (Administrator)", Navigation = "Navigation", Tabs = "Tabs";
    public const string SearchPlaceholder = "Search settings (Ctrl+F)", SearchSettings = "Search settings", ClearSearch = "Clear search",
        RefreshTooltip = "Read the system state again (F5)", FilterPlaceholder = "Filter", SearchSubtitle = "Matches by name and description on every page.",
        NoSearchResults = "No setting or action matches the search.";
    public static string SearchResults(string query) => $"Results for \"{query}\"";

    public const string NavOverview = "Overview", NavTune = "Tune", NavSystem = "System", NavSoftware = "Software", NavDiagnose = "Diagnose";
    public const string NavCollapse = "Collapse", NavCollapseTooltip = "Collapse the menu", NavExpandTooltip = "Expand the menu";
    public const string PageHome = "Home", PageHomeSubtitle = "Hardware, system status and pending changes.";

    // ---------- Home: list and inspector ----------
    public const string HomeHardwareHeader = "HARDWARE", HomeSystemHeader = "SYSTEM", HomeHardwareCrumb = "Home › Hardware", HomeSystemCrumb = "Home › System";
    public const string HomeSystemStatus = "System status", HomePendingTitle = "Pending changes", HomeSixtySeconds = " · 60 seconds";
    public const string HomeGraphUsage = "Utilization", HomeGraphMemory = "In use", HomeGraphDisk = "Active time", HomeGraphNetwork = "Send and receive";
    public static string HomePending(int count) => count == 0 ? NothingPendingRestart : count == 1 ? "1 change takes effect after a restart" : $"{count} changes take effect after a restart";
    public static string HomeStatusCount(int count) => $"{count} settings and checks, read now";

    // ---------- Home › System status checks ----------
    public static string StatusRefresh(string? monitor) => monitor is null ? "Refresh rate" : $"Refresh rate · {monitor}";
    public static string StatusRefreshValue(int hz, int best, int width, int height) => best > hz
        ? $"{hz} Hz, but the monitor can do {best} Hz at {width}×{height}: choose it in Windows display settings or on the GPU page"
        : $"{hz} Hz (the highest at {width}×{height})";
    public const string StatusRebar = "Resizable BAR", StatusGpuLink = "GPU PCIe link";
    public static string StatusRebarValue(ulong bar1, ulong vram) => bar1 >= vram / 2
        ? $"On ({bar1 / 1048576:N0} MB window)"
        : $"Off ({bar1 / 1048576:N0} MB window; needs a card, board and BIOS that support it)";
    public static string StatusGpuLinkValue(uint? linkGen, uint? width, uint? cardGen) =>
        $"Up to Gen {linkGen?.ToString() ?? "?"} x{width?.ToString() ?? "?"}" + (cardGen > linkGen ? $" (the card supports Gen {cardGen}: the slot or the BIOS limits it)" : "");
    public const string StatusStability = "Crashes (30 days)";

    public static string StatusBypassIo(string drive) => $"BypassIO (DirectStorage) · {drive}";
    public static string StatusBypassIoValue(bool supported, string? driver, string? reason) => supported ? "Supported"
        : $"Blocked{(driver is null ? "" : $" by {driver}")}{(reason is null ? "" : $": {reason}")}";
    public const string PageWindows = "Windows", PageWindowsSubtitle = "Performance, gaming, visuals, system, peripherals, security and connectivity.";
    public const string PageStartup = "Startup", PageStartupSubtitle = "What starts with Windows, by source. Disabling keeps the entry so it can be enabled again.";
    public const string PagePower = "Power", PagePowerSubtitle = "KOS power profiles and the power plan editor.";
    public const string PageServices = "Services", PageServicesSubtitle = "Service snapshots: capture, compare and restore startup types.";
    public const string PageTools = "Tools", PageToolsSubtitle = "KOS programs, maintenance and fixes.";
    public const string PageSettings = "Settings", PageSettingsSubtitle = "Preferences, logs and information about the toolbox.";

    // ---------- operations ----------
    public const string Applying = "Applying…", Restoring = "Restoring…", RunningAction = "Running…", Reading = "Reading…",
        RestartingExplorer = "Restarting Explorer…", WaitingForOther = "Waiting for another change to finish…",
        WaitingForElevation = "Waiting for the administrator prompt (UAC)…", CancelledBeforeStart = "Cancelled before it started. Nothing was changed.";
    public static string PendingDiscarded(int count) => count == 1 ? "1 unsaved change discarded." : $"{count} unsaved changes discarded.";
    public static string NotOpened(string problem) => $"{problem} It wasn't opened.";
    public const string ChangeInProgress = "A change is still being applied.", ChangeInProgressTitle = "Change in progress",
        CloseWhileBusy = "A change is still being applied. Close anyway? The running process continues until it finishes.";
    public const string UnexpectedError = "Unexpected error";
    public static string UnexpectedErrorDetail(string message, string? logFile) =>
        message + "\n\n" + (logFile is null ? "The log couldn't be written (see Settings)." : $"Details are in the log: {logFile}");
    public const string Activate = "Activate", RestartNow = "Restart now", RestartAsAdmin = "Restart as administrator", Modified = "Modified";

    // ---------- tweak cards ----------
    public const string PartialTooltip = "Some of the values this setting uses match an option and others don't. Choosing an option applies all of its values.";
    public const string RestartPendingTooltip = "The setting was written; Windows uses it only after a restart.";
    public const string NeedsRestartTooltip = "Takes effect after a restart.", NeedsRestartAfterTooltip = "Needs a restart afterwards.",
        NeedsAdminTooltip = "Asks for administrator confirmation (UAC).";
    public const string RestartExplorerNow = "Restart Explorer now", MoreOptions = "More options", MoreOptionsTooltip = "Restore, defaults and details",
        RestorePreviousMenu = "Restore previous state", RestoreOriginalMenu = "Restore original state (before the toolbox)", DetailsMenu = "Details and source";
    /// <summary><paramref name="profile"/>: security profile, only for tweaks whose default depends on it.</summary>
    public static string KosDefaultMenu(string? label, string? profile = null) => (label, profile) switch
    {
        (null, null) => "KOS default: not defined",
        (null, _) => $"KOS default: not defined in the {profile} profile",
        (_, null) => $"Apply KOS default ({label})",
        _ => $"Apply KOS default for {profile} ({label})",
    };
    public static string WindowsDefaultMenu(string? label) => label is null ? "Windows default: not documented" : $"Apply Windows default ({label})";

    public const string DetailState = "Current state", DetailSource = "Source", DetailScript = "Applied by script",
        DetailAdmin = "Administrator", DetailRestart = "Restart", DetailRequired = "Required", DetailNotRequired = "Not required",
        DetailSaved = "Saved states", DetailReadings = "Values read now", DetailSetting = "Setting", DetailValue = "Value",
        DetailMissing = "(missing)",
        DetailHowDetermined = "✓ matches the option in that column, ✗ doesn't, – the option doesn't use this value. An option is selected only when all its values match; a missing value matches only options defined by its absence.";
    public static string DetailSavedStates(DateTime? original, DateTime? previous) => string.Join("  ·  ",
        new[] { original is { } o ? $"Original {o:g}" : null, previous is { } p ? $"Before the last change {p:g}" : null }.Where(s => s is not null));

    // ---------- KOS defaults ----------
    public const string ApplyKosDefaults = "Apply KOS defaults",
        ApplyKosDefaultsTooltip = "Applies right away every setting on this page whose KOS default was verified in the project files. "
            + "Security settings use the defaults of the KOS security profile (Basic or Advanced).";
    public const string KosDefaultsNothing = "The settings on this page that have a KOS default already use it (or couldn't be read).";
    public static string KosDefaultsResult(int ok, int total) => ok == total
        ? $"{ok} settings applied and verified."
        : $"{ok} of {total} settings applied and verified. See the message on the cards that failed.";
    public static string KosDefaultsWarned(IEnumerable<string> names) =>
        $"These reduce Windows protections or can't be undone: {string.Join(", ", names)}.";

    // ---------- Home ----------
    public const string NothingPendingRestart = "No change is waiting for a restart.";
    public const string HomeLocationWarning = @"For system changes, install the toolbox in C:\Program Files\KOS Toolbox. In folders standard users can change (Downloads, Documents, ProgramData), a file placed next to the .exe could run with the administrator rights the toolbox asks for.";
    public const string PostSetupTitle = "KOS post-setup", OpenPostSetupLogs = "Open logs", ShowDetails = "Show all details";
    public static string ChangedByToolbox(string name) => $"{name} (changed by the toolbox)";

    // ---------- Settings and logs ----------
    public const string SectionPreferences = "Preferences", SectionData = "Data", SectionAbout = "About";
    public const string PrefMemoryRead = "Read memory SPD and timings at startup",
        PrefMemoryReadDetail = "When the toolbox opens, runs CPU-Z from the KOS tools folder in the background (no window, about 10 seconds) so Home › Memory already shows the SPD and timings. Only as administrator.";
    public const string PrefAnimations = "Enable animations",
        PrefAnimationsDetail = "Short transitions on pages, groups, menus and buttons, and smooth scrolling. Independent of the Windows animation setting (Accessibility › Visual effects).";
    public const string LogsTitle = "Logs", OpenLogsFolder = "Open logs folder", LogsDetailFormat = "Daily logs, rotated by size and kept for 30 days, in {0}";
    public const string LogProblemTitle = "The log can't be written",
        LogProblemDetail = "The toolbox keeps working and doesn't write the log anywhere else. Fix permissions prepares only the Logs\\Toolbox folder (one UAC prompt).";
    public const string FixPermissions = "Fix permissions", LogsFixed = "The log folder is ready.";
    public const string SavedStatesTitle = "Saved states and preferences";
    public static string DataFolderDetail(string path) => $"backups.json (previous states), history.json (change history), startup-backups.json and settings.json in {path}";

    // ---------- change history (Settings) ----------
    public const string SectionHistory = "Change history";
    public const string HistoryDetail = "Settings changed on the Windows, Power, GPU and Tools pages, newest first. Undo puts back the state from before a change, as long as nothing changed it since.";
    public const string HistoryEmpty = "No changes yet.";
    public const string HistoryUndo = "Undo";
    public const string HistoryCustom = "Custom values";
    public const string HistoryRestoredValues = "Restored values";
    public const string HistoryNotConfirmed = "Not confirmed when read back";
    public const string HistoryAbsent = "(absent)";
    public const string HistoryChangedSince = "It was changed again since, so nothing was undone.";
    public const string HistoryNotInCatalog = "This setting is no longer in the catalog.";
    public static string HistoryMore(int count) => $"{count} older change(s) in history.json.";
    public const string RunAsAdminTitle = "Run as administrator", RunAsAdminDetail = "Lets the toolbox read BCD settings and apply changes with a single UAC prompt.";
    public const string OpenGitHub = "Open the KOS GitHub";
    public static string VersionText(string? version) => $"KOS Toolbox · Version {version}";
    public static string ScriptsFolder(string path) => $"KOS scripts: {path}";

    // ---------- Startup ----------
    public const string Mode = "Mode", SourceFilter = "Source", FilterAll = "All sources", FilterAllNoDrivers = "All sources (except drivers)";
    public const string WordEntry = "entry", WordTask = "task";
    public const string ColName = "Name", ColSource = "Source", ColStatus = "Status", ColStartupType = "Startup type", ColCommand = "Command", ColSigner = "Signer",
        ColRunState = "Run state", ColLocation = "Location";
    public const string CriticalTooltip = "Critical: disabling it can stop Windows from starting.";
    public const string StartupStandardDetail = "Standard: logon entries, scheduled tasks outside the Microsoft folder and services that aren't from Microsoft.";
    public const string StartupAdvancedDetail = "Advanced: also Microsoft and hidden scheduled tasks, services, Winlogon, Active Setup and system hooks (read-only). Drivers have their own filter.";
    public const string HideMicrosoft = "Hide Microsoft-signed", HideMicrosoftTip = "Hides entries whose file is signed by Microsoft (its own signature or a Windows catalog). Unsigned and missing files always show.";
    public const string DriversWarning = "Drivers load before Windows starts. Disabling the wrong driver can leave Windows unable to start, and you may need recovery tools to undo it.";
    public static string StartupCoverage(IEnumerable<string> sources) =>
        "Read: " + string.Join(", ", sources) + ". Not covered: Explorer add-ons and codecs.";
    public static string ReadingSource(string source) => $"Reading {source}…";
    public static string EntriesCount(int shown, int total) => shown == total ? $"{total} entries" : $"{shown} of {total} entries";
    public static string StartupScanFailed(string message) => $"The startup entries couldn't be read: {message}";
    public static string StartupChanged(string name, bool enabled) => $"\"{name}\" {(enabled ? "enabled" : "disabled")} and verified.";
    public static string GroupName(StartupGroup group) => group switch
    {
        StartupGroup.Logon => GroupLogon,
        StartupGroup.ScheduledTasks => GroupTasks,
        StartupGroup.Services => GroupServices,
        StartupGroup.Hooks => GroupHooks,
        _ => GroupDrivers,
    };

    // ---------- Power ----------
    public const string PowerPlan = "Power plan", PowerSource = "Power source", PowerAc = "Plugged in (AC)", PowerDc = "On battery (DC)",
        ShowAdvancedSettings = "Show advanced settings";
    public const string PlanIsActive = "This plan is active. Saved changes take effect right away.",
        PlanNotActive = "You're editing a plan that isn't active. Changes are saved to it; Activate makes it the active plan.";
    public const string PowerEditorNote = "Plan editor: changes stay pending until Apply. The ⋯ button of a setting has a custom value, the Windows default and showing it in Windows power options. Settings Windows doesn't describe are read-only.";
    public const string PowerNoMatches = "No setting matches the filter.", PowerEnterNumber = "Enter a whole number (decimal, or hexadecimal starting with 0x).";
    public const string PlanNotConfirmed = "Windows didn't confirm the plan change.";
    public static string PlanActive(string name) => $"{name} — {Active}";
    public static string PlanActivated(string name) => $"\"{name}\" activated and verified.";
    public static string PowerPending(int count) => count == 1 ? "1 setting changed and not saved." : $"{count} settings changed and not saved.";
    public static string PowerSaved(int count) => count == 1 ? "1 value saved and verified." : $"{count} values saved and verified.";
    public static string PowerNotConfirmed(int failed, int total) => $"{failed} of {total} values weren't confirmed when the plan was read again. The list shows what Windows has now.";
    public static string PowerPlansFailed(string message) => $"The power plans couldn't be read: {message}";
    public static string PowerPlanReadFailed(string plan, string message) => $"\"{plan}\" couldn't be read: {message}";
    public static string PowerValueNotListed(uint value) => $"Value {value} (0x{value:X}) isn't in the list";
    public static string PowerRangeHint(uint min, uint max, uint step, string units) =>
        $"{min}–{max}{(units.Length == 0 ? "" : " " + units)}{(step > 1 ? $", steps of {step}" : "")}";

    // ---------- Services ----------
    public const string SnapshotsExplanation = "A service snapshot saves only the startup type of each service (Start, DelayedAutoStart, StartOverride), in the same format as the KOS ServiceBackup script. It isn't a system image or a restore point: it doesn't save files, drivers, apps or other settings, and it can't recreate deleted services. Restoring needs a restart to take effect.";
    public const string SavedSnapshots = "Saved snapshots", NoSnapshots = "No snapshots yet.", FullExport = "Full export (.full.reg)",
        ServicesCountFormat = "{0} services", Differences = "Differences", NoDifferences = "No differences from the current state.";
    public const string ColService = "Service", ColInSnapshot = "In snapshot", ColCurrent = "Current";
    public const string CheckAll = "Check all", UncheckAll = "Uncheck all", ExportCsv = "Export CSV", FilterServices = "Filter services",
        RestoreChecked = "Restore checked", CsvFilter = "CSV files (*.csv)|*.csv";
    public const string RestoreNote = "Only checked services are restored. A safety snapshot (PreRestore) is saved first. Changes take effect after a restart.";
    public const string NotInSnapshot = "Not in the snapshot, so there's nothing to restore.";
    public const string NewSnapshotTitle = "Create snapshot", NewSnapshotPrompt = "Name for the snapshot (letters, numbers, dot, underscore and hyphen):",
        FullExportOption = "Also export the whole Services key (.full.reg), the only way to bring back services KOS removes";
    public const string Capturing = "Reading the services…", Moving = "Moving files…";
    public const string BaselineKept = "Windows.Default.Services already exists and was kept.";
    public static string SnapshotCaptured(string name, int count, bool full, int skipped) =>
        $"{name} saved with {count} services." + (full ? skipped == 0 ? " Full export saved." : $" Full export saved; {skipped} keys couldn't be read and are listed in the file." : "");
    public static string SnapshotCapturedElevated(string name) => $"{name} saved by the elevated process.";
    public static string SnapshotStats(int services, int differ, int checkedCount) => $"Services: {services} · Differ: {differ} · Checked: {checkedCount}";
    public static string RestoreSummary(int ok, int total) => ok == total
        ? $"{ok} services restored and verified. Restart required."
        : $"{ok} of {total} services restored and verified. The others weren't changed or didn't match.";
    public static string RestoreReport(IEnumerable<(string Name, bool Ok, string? Error)> items, string? safety)
    {
        var list = items.ToList();
        var ok = list.Count(i => i.Ok);
        var failed = list.Where(i => !i.Ok).Select(i => $"• {i.Name}: {i.Error}").ToList();
        return RestoreSummary(ok, list.Count)
            + (failed.Count > 0 ? "\n\nNot restored:\n" + string.Join("\n", failed) : "")
            + "\n\n" + (safety is null ? "A PreRestore safety snapshot was saved before restoring." : $"Safety snapshot: {safety}.");
    }
    public const string MoveLegacy = "Move to Backups", LegacyMovedElevated = "Files moved by the elevated process.";
    public static string LegacySnapshots(int count, string folder) => $"{count} snapshot files from an older version are in {folder}.";
    public static string LegacyMoved(int count) => $"{count} files moved.";
    public static string CsvSaved(string path) => $"Saved {path}.";

    // ---------- Tools ----------
    public const string TabPrograms = "Programs", TabMaintenance = "Maintenance";
    public const string ToolsFolderFormat = "Programs in {0}. Listing never runs them; a program opens only when you click Open.";
    public const string ToolsElevatedNote = "The toolbox is running as administrator, so programs open as administrator too.";
    public const string ReadingToolsFolder = "Reading the tools folder…", NoProgramsMatch = "No program matches the filter.";
    public static string NoPrograms(string folder) => $"No programs found in {folder}.";
    public static string ProgramsCount(int shown, int total) => shown == total ? $"{total} programs" : $"{shown} of {total} programs";
    public static string ToolsScanFailed(string folder, string message) => $"{folder} couldn't be read: {message}";
    public static string OpenFailed(string message) => $"The program couldn't be opened: {message}";
}
