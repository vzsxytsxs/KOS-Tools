using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using KosToolbox.Core.Debloat;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Storage;

/// <summary>Contents of a folder (optionally only files matching <see cref="Pattern"/>) or a single file.</summary>
public sealed record JunkTarget(string Path, string Pattern = "*", bool SingleFile = false);

/// <summary>
/// A category of junk files. The paths come from here (never from the elevated request); <see cref="MachineWide"/>
/// requires administrator and is cleaned by the elevated process, the user's own by the UI process.
/// </summary>
public sealed record JunkCategory(string Id, string Name, string Description, bool MachineWide, bool DefaultChecked, TimeSpan MinAge, Func<IReadOnlyList<JunkTarget>> Targets)
{
    public bool IsRecycleBin => Id == JunkCleaner.RecycleBinId;
    /// <summary>Removed by this Windows Disk Cleanup handler (VolumeCaches) instead of file by file: Windows.old belongs to TrustedInstaller.</summary>
    public string? WindowsHandler { get; init; }
}

public sealed record JunkMeasure(long Bytes, long Files, bool Complete);
public sealed record JunkResult(long FreedBytes, long Deleted, long Skipped);

public static class JunkCleaner
{
    public const string RecycleBinId = "recycle-bin";
    static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    static string Env(string path) => Environment.ExpandEnvironmentVariables(path);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static JunkTarget Dir(string path, string pattern = "*") => new(path, pattern);

    public static IReadOnlyList<JunkCategory> Categories { get; } = new JunkCategory[]
    {
        new("user-temp", Txt.JunkUserTemp, Txt.JunkUserTempDetail, false, true, Hour, () => new[] { Dir(Path.GetTempPath()) }),
        new("windows-temp", Txt.JunkWindowsTemp, Txt.JunkWindowsTempDetail, true, true, Hour, () => new[] { Dir(Path.Combine(Win, "Temp")) }),
        new("thumbnails", Txt.JunkThumbnails, Txt.JunkThumbnailsDetail, false, true, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db") }),
        new("inet-cache", Txt.JunkInetCache, Txt.JunkInetCacheDetail, false, true, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Local, @"Microsoft\Windows\INetCache")) }),
        new("user-crash", Txt.JunkUserCrash, Txt.JunkUserCrashDetail, false, true, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Local, "CrashDumps")), Dir(Path.Combine(Local, @"Microsoft\Windows\WER")) }),
        new("system-crash", Txt.JunkSystemCrash, Txt.JunkSystemCrashDetail, true, true, TimeSpan.Zero, () => new[]
        {
            Dir(Path.Combine(Win, "Minidump")), new JunkTarget(Path.Combine(Win, "MEMORY.DMP"), SingleFile: true),
            Dir(Path.Combine(Win, "LiveKernelReports")),
            Dir(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive")), Dir(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
        }),
        new("shader-cache", Txt.JunkShaderCache, Txt.JunkShaderCacheDetail, false, false, TimeSpan.Zero, () => new[]
        {
            Dir(Path.Combine(Local, "D3DSCache")), Dir(Path.Combine(Local, @"NVIDIA\DXCache")), Dir(Path.Combine(Local, @"NVIDIA\GLCache")),
            Dir(Path.Combine(Local, @"AMD\DxCache")), Dir(Path.Combine(Local, @"AMD\DxcCache")), Dir(Path.Combine(Local, @"AMD\VkCache")),
        }),
        new("wu-downloads", Txt.JunkWuDownloads, Txt.JunkWuDownloadsDetail, true, false, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Win, @"SoftwareDistribution\Download")) }),
        new("windows-logs", Txt.JunkWindowsLogs, Txt.JunkWindowsLogsDetail, true, false, TimeSpan.Zero, () => new[] { Dir(Path.Combine(Win, "Logs")) }),
        new("delivery-optimization", Txt.JunkDeliveryOptimization, Txt.JunkDeliveryOptimizationDetail, true, false, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization")) }),
        new("installer-patch-cache", Txt.JunkPatchCache, Txt.JunkPatchCacheDetail, true, false, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Win, @"Installer\$PatchCache$")) }),
        new("windows-old", Txt.JunkWindowsOld, Txt.JunkWindowsOldDetail, true, false, TimeSpan.Zero,
            () => new[] { Dir(Path.Combine(Path.GetPathRoot(Win)!, "Windows.old")) }) { WindowsHandler = "Previous Installations" },
        new("prefetch", Txt.JunkPrefetch, Txt.JunkPrefetchDetail, true, false, TimeSpan.Zero, () => new[] { Dir(Path.Combine(Win, "Prefetch"), "*.pf") }),
        new(RecycleBinId, Txt.JunkRecycleBin, Txt.JunkRecycleBinDetail, false, false, TimeSpan.Zero, Array.Empty<JunkTarget>),
    };

    public static JunkCategory? Find(string id) => Categories.FirstOrDefault(c => c.Id == id);

    // ---------- measure ----------

    public static JunkMeasure Measure(JunkCategory c)
    {
        if (c.IsRecycleBin)
        {
            var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? new(info.Bytes, info.Items, true) : new(0, 0, false);
        }
        long bytes = 0, files = 0;
        var complete = true;
        foreach (var t in c.Targets())
            foreach (var f in Files(t, c.MinAge, ref complete))
            {
                bytes += f.Length;
                files++;
            }
        return new(bytes, files, complete);
    }

    // ---------- clean ----------

    /// <summary>
    /// Deletes the category's files (files in use are skipped) and any subfolders left empty; the root folder stays. Every delete goes
    /// through a handle opened on the listed item itself (see <see cref="DeleteExact"/>), so a link swapped into its path after the
    /// listing can't turn an elevated delete against another folder.
    /// </summary>
    public static JunkResult Clean(JunkCategory c)
    {
        if (c.IsRecycleBin)
        {
            var before = Measure(c);
            var hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirmation, progress or sound: the toolbox already confirmed
            if (hr != 0 && hr != unchecked((int)0x8000FFFF)) Log.Warn($"Recycle Bin: SHEmptyRecycleBin returned 0x{hr:X8}");
            var after = Measure(c);
            return new(Math.Max(0, before.Bytes - after.Bytes), Math.Max(0, before.Files - after.Files), after.Files);
        }
        if (c.WindowsHandler is { } handler) return CleanThroughWindows(c, handler);
        long freed = 0, deleted = 0, skipped = 0;
        foreach (var t in c.Targets())
        {
            var complete = true;
            foreach (var f in Files(t, c.MinAge, ref complete).ToList())
            {
                long size;
                try { size = f.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { skipped++; continue; }
                if (DeleteExact(f.FullName)) { freed += size; deleted++; }
                else skipped++;
            }
            if (!t.SingleFile) RemoveEmptyFolders(t.Path);
        }
        Log.Info($"Cleanup {c.Id}: {deleted} files deleted ({freed} bytes), {skipped} skipped.");
        return new(freed, deleted, skipped);
    }

    const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    const int SageSet = 4731;

    /// <summary>
    /// A category Windows' own Disk Cleanup handler removes (VolumeCaches\<paramref name="handler"/>): cleanmgr /sagerun with a set
    /// number of the toolbox's, marked on that handler only and unmarked afterwards. Refused when the folder is a link or doesn't
    /// belong to SYSTEM, Administrators or TrustedInstaller (a folder a user created under that name).
    /// </summary>
    static JunkResult CleanThroughWindows(JunkCategory c, string handler)
    {
        foreach (var t in c.Targets())
            if (Directory.Exists(t.Path) && GpuTuner.FolderProblem(Path.GetPathRoot(t.Path)!, t.Path) is { } problem)
                throw new InvalidOperationException(problem);
        var before = Measure(c);
        using (var key = Registry.LocalMachine.OpenSubKey($@"{VolumeCaches}\{handler}", writable: true)
                         ?? throw new InvalidOperationException(Txt.CleanupHandlerMissing(handler)))
        {
            var flag = $"StateFlags{SageSet:D4}";
            key.SetValue(flag, 2, RegistryValueKind.DWord);
            try
            {
                var r = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), $"/sagerun:{SageSet}", TimeSpan.FromHours(1));
                Log.Info($"Cleanup {c.Id}: cleanmgr ({handler}) exit code {r.ExitCode}");
            }
            finally { key.DeleteValue(flag, throwOnMissingValue: false); }
        }
        var after = Measure(c);
        return new(Math.Max(0, before.Bytes - after.Bytes), Math.Max(0, before.Files - after.Files), after.Files);
    }

    /// <summary>Files of a target, without following junctions/links. Unreadable folders mark the measurement as incomplete.</summary>
    internal static IEnumerable<FileInfo> Files(JunkTarget t, TimeSpan minAge, ref bool complete)
    {
        var result = new List<FileInfo>();
        var limit = DateTime.UtcNow - minAge;
        // Long names (a %TEMP% with 8.3 names, for example), so each listed path compares with the item's real path when deleting.
        var path = LongPath(t.Path) ?? t.Path;
        if (t.SingleFile)
        {
            var f = new FileInfo(path);
            if (f.Exists && (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.LastWriteTimeUtc <= limit) result.Add(f);
            return result;
        }
        var root = new DirectoryInfo(path);
        if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0) return result;
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.EnumerateFiles(t.Pattern, options))
                    if (f.LastWriteTimeUtc <= limit) result.Add(f);
                if (t.Pattern == "*")
                    foreach (var d in dir.EnumerateDirectories("*", options)) stack.Push(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { complete = false; }
        }
        return result;
    }

    /// <summary>Empty subfolders, deepest first (a folder that isn't empty just fails to delete). A root that is a link isn't entered.</summary>
    static void RemoveEmptyFolders(string root)
    {
        if (!DebloatFiles.IsFolder(root)) return;
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var d in Directory.EnumerateDirectories(LongPath(root) ?? root, "*", options).OrderByDescending(p => p.Length).ToList())
            DeleteExact(d);
    }

    /// <summary>
    /// Deletes exactly the file or empty folder at <paramref name="path"/>: opened without following a link, then only if the handle's
    /// real path is still that path (no junction was swapped into a parent after the listing), deleted through that same handle.
    /// A link in its place is itself what gets deleted, never its target.
    /// </summary>
    internal static bool DeleteExact(string path)
    {
        var full = Path.GetFullPath(path);
        using var h = CreateFile(@"\\?\" + full, Delete | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (h.IsInvalid || !string.Equals(FinalPath(h), full, StringComparison.OrdinalIgnoreCase)) return false;
        var flags = 0x1u | 0x2u | 0x10u; // FILE_DISPOSITION_FLAG_DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
        return SetFileInformationByHandle(h, 21, ref flags, sizeof(uint)); // FileDispositionInfoEx
    }

    /// <summary>The path with long names, as Windows resolves it; null when it doesn't exist.</summary>
    static string? LongPath(string path)
    {
        using var h = CreateFile(@"\\?\" + Path.GetFullPath(path), ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        return h.IsInvalid ? null : FinalPath(h);
    }

    static string? FinalPath(SafeFileHandle h)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandle(h, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length) length = GetFinalPathNameByHandle(h, buffer = new char[length], (uint)buffer.Length, 0);
        if (length == 0 || length > buffer.Length) return null;
        var text = new string(buffer, 0, (int)length);
        return text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + text[8..] : text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }

    const uint Delete = 0x00010000, ReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    // ---------- elevated request ----------

    /// <summary>The selected machine-wide categories: in the current process if administrator, otherwise through the elevated process.</summary>
    public static async Task CleanMachineAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Validate(ids);
        if (Elevation.IsAdmin) await Task.Run(() => { foreach (var id in ids) Clean(Find(id)!); }, ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { CleanJunk = ids.ToList() }, ct).ConfigureAwait(false);
    }

    /// <summary>Only known machine-wide category ids, without repeats; the elevated process receives no paths.</summary>
    internal static void Validate(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count) throw new InvalidOperationException("Invalid cleanup request.");
        foreach (var id in ids)
            if (Find(id) is not { MachineWide: true }) throw new InvalidOperationException($"Not a system cleanup category: {id}");
    }

    // shellapi.h uses natural packing on x64 (pack 1 is x86 only): cbSize at 0, the two __int64s at 8 and 16.
    [StructLayout(LayoutKind.Sequential)]
    struct ShQueryRbInfo { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
