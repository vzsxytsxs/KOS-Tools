namespace KosToolbox.Core.Text;

/// <summary>Texts for the Apps page (installed programs and Store apps).</summary>
public static partial class Txt
{
    public const string PageApps = "Apps", PageAppsSubtitle = "Installed programs and Microsoft Store apps.";
    public const string TabStoreApps = "Store apps", Opened = "Opened.";
    public const string ColPublisher = "Publisher", ColVersion = "Version", ColInstalled = "Installed", ColKind = "Type";
    public static string AppsReadFailed(string message) => $"The list couldn't be read: {message}";

    // ---------- programs ----------
    public const string ProgramsNote = "The same entries as Settings › Apps (Uninstall registry keys). Uninstall runs the program's own uninstaller; when it closes, the list is read again.";
    public const string Uninstall = "Uninstall", OpenLocation = "Open install location",
        WaitingForUninstaller = "Waiting for the uninstaller to finish…";
    public static string UninstallerMissing(string? command) => $"The uninstaller wasn't found: {command}";
    public static string Uninstalled(string name) => $"{name} was removed.";
    public static string StillListed(string name) => $"The uninstaller closed, but {name} is still listed. If it's still open or was cancelled, finish it and press Refresh (F5).";

    // ---------- leftovers and Force removal ----------
    public const string ForceRemoval = "Force removal", ScanningLeftovers = "Looking for leftovers…", DeletingLeftovers = "Deleting the checked items…";
    public const string ForceRemovalTooltip = "For a program whose uninstaller is broken or missing: finds its folder, its uninstall entry and its leftovers without running the uninstaller. Nothing is deleted before you check and confirm the list.";
    public static string UninstalledLeftovers(string name, int count) => $"{name} was removed. {count} leftover{(count == 1 ? "" : "s")} found: check what to delete.";
    public static string UninstalledClean(string name) => $"{name} was removed. No leftovers were found.";
    public static string AppRemovedLeftovers(string name, int count) => $"{name} was removed for your account. {count} leftover{(count == 1 ? "" : "s")} found: check what to delete.";
    public static string ForceRemovalFound(string name, int count) => $"{count} item{(count == 1 ? "" : "s")} of {name} found: check what to delete.";
    public static string NothingFound(string name) => $"Nothing of {name} was found to delete.";
    public static string LeftoversTitle(string name) => $"Leftovers of {name}";
    public static string ForceRemovalTitle(string name) => $"Force removal: {name}";
    public const string LeftoversNote = "Found after the uninstaller closed: folders and shortcuts that point to the program or have its name, and its registry entries, services and scheduled tasks. Checked items are deleted permanently (not to the Recycle Bin); registry entries, services and tasks are saved to a backup first.";
    public const string ForceRemovalNote = "Everything of the program, without running its uninstaller: its folder, its uninstall entry and its leftovers. Checked items are deleted permanently (not to the Recycle Bin); registry entries, services and tasks are saved to a backup first.";
    public const string ColItem = "Item", ColReason = "Why";
    public static string LeftoversStats(int total, int checkedCount, long size) => $"{total} items · {checkedCount} checked · {Size(size)} in checked folders and files";
    public static string LeftoversRemoved(int removed, int kept, int failed, string? backup) =>
        $"{removed} item{(removed == 1 ? "" : "s")} deleted and confirmed." + (kept > 0 ? $" {kept} kept (not empty)." : "") + (failed > 0 ? $" {failed} not deleted." : "")
        + (backup is null ? "" : $" Registry, services and tasks backup: {backup}");
    public static string LeftoversFailed(IEnumerable<string> lines) => "Not deleted: " + string.Join("; ", lines);
    public const string LeftoversAllGone = "Nothing else was found.";
    public const string LeftoverNeedsAdmin = "Deleting this needs the toolbox running as administrator (Settings › Restart as administrator).";
    public const string LeftoverNotAllowed = "For safety, the toolbox doesn't delete this location.";
    public static string LeftoverBackupFailed(string message) => $"Not deleted: the backup failed ({message}).";
    public static string LeftoverFolderPartly(string message) => $"Some files couldn't be deleted (in use or protected): {message}";
    public const string KindFolder = "Folder", KindFile = "Shortcut", KindRegistryKey = "Registry key", KindRegistryValue = "Registry value",
        KindService = "Service", KindTask = "Scheduled task";
    public const string GroupFiles = "Files and folders", GroupShortcuts = "Shortcuts", GroupRegistry = "Registry", GroupServicesTasks = "Services and scheduled tasks";
    public const string LeftoverInstallFolder = "Install folder", LeftoverNamed = "Has the program's name", LeftoverVendor = "Publisher folder or key (deleted only if empty)",
        LeftoverDotNetSettings = "Settings of the program's .exe", LeftoverShortcut = "Shortcut to the program's folder", LeftoverStartMenu = "Start menu folder with the program's name",
        LeftoverUninstallEntry = "Uninstall entry", LeftoverMsi = "Windows Installer registration", LeftoverRun = "Starts the program at sign-in",
        LeftoverMuiCache = "Program name remembered by Windows (MuiCache)", LeftoverCompat = "Compatibility setting of the program",
        LeftoverUsage = "Usage record of the program (taskbar and Start)", LeftoverAppPaths = "App Paths entry of the program", LeftoverTrayIcon = "Tray icon settings of the program",
        LeftoverTracing = "Network tracing settings created for the program's .exe", LeftoverService = "Service that runs from the program's folder",
        LeftoverTask = "Scheduled task that runs from the program's folder", LeftoverPackageData = "App data folder", LeftoverPackageSettings = "App settings of your account";

    // ---------- Store apps ----------
    public const string StoreAppsNote = "Packages installed for your account: Microsoft Store apps and the apps that come with Windows. Removing affects only your account; apps can be installed again from the Microsoft Store.";
    public const string StoreServiceDisabled = "Store apps can't be listed or removed while the AppX Deployment Service (AppXSvc) is disabled. Turn on Windows › System › App installs and licenses and restart.";
    public const string KindApps = "Apps", KindSystem = "System apps", KindFrameworks = "Frameworks", KindAll = "All packages";
    public const string RemoveApp = "Remove", SearchStore = "Search in Microsoft Store", Removing2 = "Removing…";
    public static string AppRemoved(string name) => $"{name} was removed for your account.";
    public static string AppRemovedAllUsers(string name) => $"{name} was removed for every user and for new accounts.";
    public const string RemoveForAllUsers = "Every user and new accounts (system apps too)";
    public const string RemoveForAllUsersTip = "Removes the app for every account and deprovisions it, so new accounts and feature updates don't bring it back. System apps can be removed this way too: removing the wrong one can break parts of Windows such as Start, Settings or sign-in. Needs the toolbox running as administrator.";
    public const string AllUsersNeedsAdmin = "Removing an app for every user needs the toolbox running as administrator (Settings › Restart as administrator).";
    public static string AppStillThere(string name) => $"Windows reported success, but {name} is still installed.";
    public static string PackagesCount(int shown, int total) => shown == total ? $"{total} packages" : $"{shown} of {total} packages";

    // ---------- Windows optional features ----------
    public const string TabWindowsFeatures = "Windows features", ColState = "State", LoadingFeatures = "Reading Windows features (DISM)…";
    public const string FeaturesNote = "The same list as \"Turn Windows features on or off\", read with the Windows DISM tool. Turning a feature on also turns on the features it depends on; some download files from Windows Update and can take several minutes.";
    public const string FeaturesNeedAdmin = "Listing and changing Windows features needs administrator rights. Use Settings › Restart as administrator.";
    public const string TurnOn = "Turn on", TurnOff = "Turn off", ChangingFeature = "Changing the feature (DISM)… this can take several minutes.";
    public static string FeatureChanged(string name, bool enabled, bool reboot) =>
        $"{name} turned {(enabled ? "on" : "off")} and verified." + (reboot ? " Restart Windows to finish." : "");
    public static string FeatureNotConfirmed(string name, string state) => $"DISM finished, but {name} now reads as \"{state}\".";
    public static string FeatureDetail(string displayName, string description, string restart) => $"{displayName}\n{description}\nRestart required: {restart}";
    public static string FeaturesCount(int shown, int total) => shown == total ? $"{total} features" : $"{shown} of {total} features";
    public static string DismFailed(int code, string output) =>
        $"DISM failed (exit code {code}). " + (output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("Error", StringComparison.Ordinal) || l.Length > 0) ?? "");
}
