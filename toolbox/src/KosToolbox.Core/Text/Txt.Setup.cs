namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public const string PageInstall = "Install", PageInstallSubtitle = "Download and install browsers, launchers, tools and peripheral software from the official sources.";
    public const string TabSetupApps = "Apps", TabSetupDownloads = "Downloads", TabSetupUpdates = "Updates", TabSetupStore = "Microsoft Store";
    public const string SetupInstall = "Install", SetupReinstall = "Reinstall", SetupDownload = "Download", SetupOpenStore = "Install",
        SetupInstallSelected = "Install selected", SetupClearSelection = "Clear selection", SetupRun = "Run", SetupRetry = "Retry",
        SetupRemove = "Remove", SetupClearFinished = "Clear finished";
    public const string SetupNotInstalled = "Not installed", SetupPortable = "Portable (no installer)", SetupStoreOnly = "Microsoft Store app (installs through Install › Microsoft Store)";
    public const string SetupChoose = "Install…", SetupSelect = "Select";
    public static string SetupChooseTitle(string name) => $"Install {name}";
    public static string SetupSelectedVariants(string list) => $"Selected: {list}";
    public static string SetupInstalled(string? version) => string.IsNullOrWhiteSpace(version) ? "Installed" : $"Installed · {version}";
    public static string SetupSelectedCount(int n) => n == 0 ? "" : n == 1 ? "1 selected" : $"{n} selected";
    public static string SetupActiveCount(int n) => n == 0 ? "" : n == 1 ? "1 in progress" : $"{n} in progress";
    public const string SetupNoDownloads = "Nothing downloaded yet. Pick apps in the Apps tab.";
    public static string SetupFolder(string folder, string portable) => $"Installers are downloaded to {folder} and deleted after installing; portable programs go to {portable}.";
    public static string SetupPortableDownloaded(string file) => $"Downloaded · {file}";
    public static string SetupAria2(string? path) => path is null
        ? @"Built-in downloader. To download with aria2 (8 connections), put aria2c.exe in C:\ProgramData\TheKOS\tools."
        : $"Downloading with aria2: {path}";

    // ---------- states of a download ----------
    public const string SetupQueued = "Waiting for a free download slot…", SetupResolving = "Finding the latest version…", SetupDownloading = "Downloading…",
        SetupVerifying = "Checking the signature…", SetupWaitingInstaller = "Waiting for the previous installer to close…", SetupInstalling = "Installer open…",
        SetupOpened = "Opened", SetupInstalledDone = "Installed. The installer was deleted.", SetupInstallerClosed = "Installer closed", SetupCancelled = "Cancelled",
        SetupInstallerCancelled = "The installer was cancelled at the administrator prompt.";
    public static string SetupInstallerExit(int code) => $"Installer closed with code {code}";
    public static string SetupFailed(string message) => $"Failed: {message}";
    public static string SetupSignedBy(string signer) => $"Signed by {signer}";
    public const string SetupUnsigned = "Not signed, or the signature doesn't match. It wasn't opened.";
    public const string SetupUnsignedByDeveloper = "Downloaded. The developer doesn't sign this installer, so it doesn't open by itself: click Run.";
    public static string SetupInstalledKept(string error) => $"Installed. The installer couldn't be deleted ({error}); Remove deletes it.";
    public static string SetupDeleteFailed(string list) => $"Couldn't delete (probably still in use):\n{list}";
    // ---------- Chocolatey ----------
    public const string ChocoInstalling = "Installing Chocolatey (first time only)…", ChocoNotStarted = "Chocolatey couldn't be started.",
        ChocoDoneNotDetected = "Chocolatey finished, but the program isn't in Programs and Features yet.";
    public static string ChocoInstallingPackage(string id) => $"Installing with Chocolatey ({id})…";
    public static string ChocoFailed(int code) => $"Chocolatey failed (exit code {code}). Details are in the log.";
    public static string ChocoUnsigned(string? signer) => signer is null
        ? "The Chocolatey installer isn't signed, so it wasn't run."
        : $"The Chocolatey installer is signed by {signer}, not by Chocolatey, so it wasn't run.";
    public static string ChocoNotInstalled(int? code) => $"Chocolatey isn't installed after its installer closed (code {code?.ToString() ?? "none"}).";
    public static string SetupProgress(long received, long? total, double speed, bool aria2) =>
        (total is > 0 ? $"{Size(received)} of {Size(total.Value)}" : Size(received)) + (speed > 0 ? $" · {Size((long)speed)}/s" : "") + (aria2 ? " · aria2" : "");
}
