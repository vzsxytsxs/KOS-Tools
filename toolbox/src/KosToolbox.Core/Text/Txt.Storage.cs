using System.Globalization;

namespace KosToolbox.Core.Text;

/// <summary>Texts for the Storage page (disk usage and cleanup).</summary>
public static partial class Txt
{
    public const string PageStorage = "Storage", PageStorageSubtitle = "What uses your disk space, and junk files you can delete.";
    public const string TabDiskUsage = "Disk usage", TabCleanup = "Cleanup";

    /// <summary>Human-readable size in base 1024 (like Explorer).</summary>
    public static string Size(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} bytes" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    // ---------- disk usage ----------
    public const string Scan = "Scan", Up = "Up", ShowInExplorer = "Show in Explorer", LargestFiles = "Largest files", Folders = "Folders",
        ColSize = "Size", ColFiles = "Files", ColPath = "Path", DriveOrFolder = "Drive", ChooseFolder = "Folder…";
    public const string DiskNote = "Sizes are file sizes, not size on disk. Junctions, symbolic links and cloud-only files aren't counted. Double-click a folder to open it. Nothing is deleted here.";
    public static string DriveLabel(string name, string label, long free, long total) =>
        $"{name.TrimEnd('\\')}{(label.Length > 0 ? " · " + label : "")} · {Size(free)} free of {Size(total)}";
    public static string Scanning(long files, string folder) => $"Scanning… {files:N0} files · {folder}";
    public static string ScanDone(long files, long bytes, int skipped, TimeSpan time, bool fromMft = false) =>
        $"{files:N0} files, {Size(bytes)}, in {time.TotalSeconds:0.0} s{(fromMft ? " (from the file table)" : "")}." + (skipped > 0 ? fromMft ? $" {skipped:N0} entries inside links and cloud folders weren't counted." : $" {skipped:N0} folders couldn't be read{(Execution.Elevation.IsAdmin ? "" : " (run as administrator to include them)")}." : "");
    public const string ScanCancelled = "Scan cancelled.";
    public const string MenuShowInExplorer = "Show in Explorer", MenuRecycle = "Move to Recycle Bin", MenuDelete = "Delete permanently";
    public const string StorageDeleteNotAPath = "that isn't a full path.", StorageDeleteDrive = "a whole drive isn't deleted from here.",
        StorageDeleteInsideWindows = "Windows' own files aren't deleted from here (only the contents of its Temp folder).",
        StorageDeleteCancelled = "Windows cancelled it (it would have deleted the file for good).";
    public static string StorageDeleteSystemItem(string name) => $"{name} belongs to Windows and isn't deleted from here.";
    public static string StorageDeleteProtected(string name) => $"{name} (or what it holds) is a folder Windows or the toolbox needs, so it isn't deleted from here.";
    public static string MapRecycled(string name, long bytes) => $"{name} ({Size(bytes)}) moved to the Recycle Bin.";
    public static string MapDeleted(string name, long bytes) => $"{name} ({Size(bytes)}) deleted for good.";
    public static string MapDeleteFailed(string name, string message) => $"{name} wasn't deleted completely: {message} Scan again to see what is left.";
    public const string ViewList = "List", ViewMap = "Map", TreemapFiles = "files here",
        MapNote = "Each rectangle is a folder, as big as its size; inside it, its own biggest folders. Click a folder to open it, right-click to show it in Explorer.";
    public static string ScanFailed(string message) => $"The scan failed: {message}";
    public static string FolderSummary(string path, long bytes, long files) => $"{path} · {Size(bytes)} · {files:N0} files";

    // ---------- cleanup ----------
    public const string CleanupNote = "Deletes files only in the locations listed for each category. Files in use are skipped and the folders themselves stay. Deleted files don't go to the Recycle Bin. KOS cleanup (Tools › Maintenance) also clears logs and old devices.";
    public const string Analyze = "Analyze", CleanSelected = "Clean selected", Measuring = "Measuring…", Cleaning = "Deleting…",
        AdminBadge = "Administrator", SizeNotMeasured = "Not measured", SizeNeedsAdmin = "Some folders need administrator to read";
    public const string CleanupNothingSelected = "Select the categories to clean first.";
    public static string CleanupSize(long bytes, long files, bool complete) => $"{Size(bytes)} · {files:N0} files" + (complete ? "" : " · at least");
    public static string CleanupDone(long freed, long left) =>
        $"{Size(freed)} freed (measured again after cleaning)." + (left > 0 ? $" {left:N0} files are still there (in use or protected)." : "");
    public static string CleanupFailed(string message) => $"Cleanup failed: {message}";

    public const string JunkUserTemp = "Your temporary files", JunkUserTempDetail = "%TEMP%. Files changed in the last hour are kept, since programs may still be using them.";
    public const string JunkWindowsTemp = "Windows temporary files", JunkWindowsTempDetail = @"C:\Windows\Temp. Files changed in the last hour are kept.";
    public const string JunkThumbnails = "Thumbnail cache", JunkThumbnailsDetail = "Explorer's picture and video thumbnails (thumbcache_*.db). Files Explorer has open are skipped; thumbnails are rebuilt when needed.";
    public const string JunkInetCache = "Internet cache", JunkInetCacheDetail = "INetCache used by Windows components and older apps. Browsers keep their own caches.";
    public const string JunkUserCrash = "Your crash dumps and error reports", JunkUserCrashDetail = @"CrashDumps and Windows Error Reporting files in your AppData\Local.";
    public const string JunkSystemCrash = "System crash dumps and error reports", JunkSystemCrashDetail = "Minidump, MEMORY.DMP, LiveKernelReports and the shared Windows Error Reporting queue and archive. Delete them only if you don't need to investigate a crash.";
    public const string JunkShaderCache = "Shader caches", JunkShaderCacheDetail = "DirectX, NVIDIA and AMD shader caches. Games rebuild them, so the first launch afterwards can stutter.";
    public const string JunkWuDownloads = "Windows Update downloads", JunkWuDownloadsDetail = @"SoftwareDistribution\Download. Don't clean while updates are installing; Windows downloads again what it still needs.";
    public const string JunkWindowsLogs = "Windows logs", JunkWindowsLogsDetail = @"C:\Windows\Logs: servicing (CBS, DISM), setup, update and other Windows logs. Logs in use are skipped; they only matter for troubleshooting.";
    public const string JunkDeliveryOptimization = "Delivery Optimization cache", JunkDeliveryOptimizationDetail = @"SoftwareDistribution\DeliveryOptimization: update and Store files kept to share with other PCs.";
    public const string JunkPatchCache = "Windows Installer patch cache", JunkPatchCacheDetail = @"C:\Windows\Installer\$PatchCache$: copies kept for removing Office and other MSI updates. Afterwards, removing such an update may ask for the original installer.";
    public const string JunkWindowsOld = "Previous Windows installation", JunkWindowsOldDetail = @"C:\Windows.old, left by a Windows upgrade or reinstall: going back to that version is no longer possible afterwards. Windows' own Disk Cleanup handler (Previous Installations) removes it; a Windows.old that is a link or isn't owned by Windows is refused.";
    public static string CleanupHandlerMissing(string handler) => $"Windows' Disk Cleanup handler \"{handler}\" isn't on this installation.";
    public const string JunkPrefetch = "Prefetch", JunkPrefetchDetail = "Prefetch files (*.pf). Windows recreates them; programs can start a little slower until it does.";
    public const string JunkRecycleBin = "Recycle Bin", JunkRecycleBinDetail = "Empties the Recycle Bin on every drive.";
}
