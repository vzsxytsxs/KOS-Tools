namespace KosToolbox.Core.Text;

/// <summary>Texts for the Drivers page (driver store).</summary>
public static partial class Txt
{
    public const string PageDrivers = "Drivers", PageDriversSubtitle = "Third-party driver packages in the driver store.";
    public const string DriversNote = "The packages \"pnputil /enum-drivers\" lists (oemNN.inf), read directly from Windows. Drivers that come with Windows aren't listed. Deleting removes the package from the driver store; a device that uses it needs another driver afterwards.";
    public const string ColProvider = "Provider", ColClassName = "Class", ColDate = "Date", ColUsedBy = "Used by", OldVersionPill = "Older version",
        CheckOldVersions = "Check older versions", CheckOlderWide = "Check older by provider and class", DeleteChecked = "Delete checked", ExportSelected = "Export…",
        ForceDelete = "Force: delete even while a device uses it (pnputil /force); that device may lose its driver after a restart",
        UninstallFromDevices = "Also remove it from devices that use it (pnputil /uninstall)", NotInUse = "Not in use", NoDrivers = "No third-party driver packages found.";
    public static string UsedBy(int count) => count == 1 ? "1 device" : $"{count} devices";
    public static string DriversCount(int shown, int total) => shown == total ? $"{total} packages" : $"{shown} of {total} packages";
    public const string DriversNothingChecked = "Check the packages to delete first.",
        DriverExportFolderInvalid = "Choose an existing folder on a local drive.", ExportDriverTitle = "Export driver package";
    public static string DriversDeleted(int deleted, int total) => deleted == total ? $"{deleted} packages deleted and verified." : $"{deleted} of {total} packages deleted. The others are still in the driver store (in use, or Windows refused); see the log.";
    public static string DriverExported(string name, string folder) => $"{name} exported to {folder}.";
    public static string PnpUtilFailed(int code, string output) => $"pnputil failed (exit code {code}). {output.Trim()}";
    public static string DriversReadFailed(string message) => $"The driver store couldn't be read: {message}";

    // ---------- video driver ----------
    public const string DisplayDriverTitle = "Display driver", RemoveDisplayDriver = "Remove display driver…", InstallFromFolder = "Install from folder…",
        AlsoRemoveAudio = "Also remove this vendor's audio drivers (HDMI/DisplayPort audio)", NoDisplayDriver = "No third-party display driver is installed (Windows uses its basic driver).",
        DisplayDownloadNote = "NVIDIA: use the NVIDIA driver card below. AMD and Intel: download the driver from the vendor's site, extract it (AMD extracts to C:\\AMD when started) and use Install from folder to install only the display driver, without the control panel, telemetry and extra services.",
        DisplayInfInvalid = "Choose a folder that contains an extracted display driver (.inf of class Display).", ChooseDriverFolder = "Choose the extracted driver folder";
    public static string DownloadFrom(string vendor) => $"{vendor} drivers";
    public static string DisplayDriverInfo(string provider, string version, string inf, int devices) => $"{provider} {version} · {inf} · {UsedBy(devices)}";
    public static string DisplayRemoved(int removed, int total) => removed == total
        ? $"{removed} driver packages removed and verified. Restart Windows, then install a display driver."
        : $"{removed} of {total} driver packages removed; the others are still in the driver store. See the log.";
    public static string DisplayInstalled(string inf) => $"{Path.GetFileName(inf)} installed and verified. Restart Windows if the screen or Device Manager asks for it.";
    public const string DisplayInstallNotConfirmed = "pnputil finished, but no new display driver package appeared in the driver store.";
    public const string DisplayDriverMoved = "Installing and removing the display driver (NVIDIA driver download and install, and Display Driver Uninstaller) is on the GPU page, Driver tab.",
        OpenGpuDriver = "Open GPU › Driver";

    // ---------- GPU › Driver: NVIDIA driver ----------
    public const string GpuTabDriver = "Driver", NvidiaDriverTitle = "NVIDIA driver", GameReady = "Game Ready", Studio = "Studio", FindDrivers = "Find drivers",
        DownloadDriver = "Download", UseDownloadedFile = "Use a downloaded file…", ChooseNvidiaPackage = "Choose the NVIDIA driver package (.exe)",
        NvidiaComponentsTitle = "Components", NvidiaComponentsNote = "Display driver and NVI2 are always installed. Everything else is optional.",
        NvidiaAfterInstallTitle = "After installing", CleanInstall = "Clean install (removes the previous driver's settings and profiles)",
        InstallDriver = "Install", NvidiaNoGpu = "No NVIDIA GPU was found.", NvidiaNoReleases = "NVIDIA returned no driver for this GPU.",
        NvidiaByDeviceNote = "The GPU's name didn't match NVIDIA's product list (no driver installed?): found by its PCI id, so only the newest driver is offered.",
        NvidiaNotPackage = "That file isn't an NVIDIA driver package (it has no Display.Driver and NVI2 folders).",
        SevenZipMissing = "7-Zip wasn't found (C:\\Program Files\\7-Zip, or the copy in the KOS tools folder): it's needed to read the driver package.",
        NvidiaInstallNote = "The install takes a few minutes; the screen goes black and comes back while the driver changes. Restart Windows when it finishes.";
    public static string NvidiaReleasesFound(int count, string gpu) => count == 1 ? $"1 driver for {gpu}." : $"{count} drivers for {gpu}.";
    public static string NvidiaReleaseLabel(string version, DateTime? date, string? size) =>
        version + (date is { } d ? $" · {d:d}" : "") + (string.IsNullOrEmpty(size) ? "" : $" · {size}");
    public static string NvidiaBadUrl(string url) => $"Not an NVIDIA download link: {url}";
    public static string NvidiaNotSigned(string file) => $"{file} isn't signed by NVIDIA Corporation; it wasn't used.";
    public static string SevenZipFailed(int code, string output) => $"7-Zip failed (exit code {code}). {output.Trim()}";
    public static string NvidiaPackageReady(string file, int components) => $"{file}: {components} components.";
    public static string NvidiaDownloading(long received, long? total) => total is > 0 ? $"Downloading… {Size(received)} of {Size(total.Value)}" : $"Downloading… {Size(received)}";
    /// <summary>The install's current step for the progress bar, with the time since Install was clicked.</summary>
    public static string NvidiaInstallStep(string? step, double? percent, TimeSpan elapsed) => (step switch
    {
        Drivers.NvidiaDriver.StepExtract => percent is { } p ? $"Extracting the chosen components… {p:F0}%" : "Extracting the chosen components…",
        Drivers.NvidiaDriver.StepSetup => "NVIDIA setup is installing the driver; the screen may go black for a moment…",
        Drivers.NvidiaDriver.StepTweaks => "Applying the chosen tweaks…",
        _ => "Copying and checking the package…",
    }) + $" · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    public static string NvidiaInstalled(string version) => $"NVIDIA driver {version} installed and verified. Restart Windows to finish.";
    public static string NvidiaInstallNotConfirmed(string expected, string? found) =>
        $"The installer finished, but the installed driver is {found ?? "not an NVIDIA driver"} instead of {expected}. See the log (setup.exe's exit code).";

    // ---------- GPU › Driver: Display Driver Uninstaller ----------
    public const string DduTitle = "Advanced uninstall (Display Driver Uninstaller)",
        DduNote = "Removes the driver with its services, folders, registry leftovers and devices, then restarts Windows. Save your work first.",
        DduSafeModeNote = "Safe Mode is the most thorough. In Safe Mode, sign in as an administrator with the account password (the Windows Hello PIN may not work there); if you can't, restart: Windows starts normally again.",
        DduVendor = "Vendor", DduOptionsTitle = "Also remove", DduRunNow = "Clean and restart", DduRunSafeMode = "Restart in Safe Mode and clean",
        DduStarted = "Display Driver Uninstaller is removing the driver; Windows restarts when it finishes.",
        DduScheduled = "Restarting into Safe Mode: Display Driver Uninstaller runs there and Windows restarts back to normal mode.";
    public static string DduMissing(string path) => $"Display Driver Uninstaller wasn't found ({path}). It comes in the KOS tools folder.";
    public static string BcdeditFailed(int code, string output) => $"bcdedit couldn't set up the Safe Mode boot (exit code {code}); nothing was changed. {output.Trim()}";
    public static string RestartFailed(int code, string output) => $"The restart failed (exit code {code}); the Safe Mode boot was undone and nothing was removed. {output.Trim()}";
}
