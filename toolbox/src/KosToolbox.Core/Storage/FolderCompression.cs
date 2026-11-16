using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Storage;

/// <summary>Size of a folder's files and what they take on disk (smaller when compressed).</summary>
public sealed record FolderSize(long Logical, long OnDisk, long Files);

/// <summary>
/// Windows' executable compression (compact /EXE, the same WOF compression CompactOS uses) for a folder such as a game: files stay
/// readable by every program and are decompressed in memory as they are read. A file a game update rewrites comes back uncompressed.
/// </summary>
public static class FolderCompression
{
    public static readonly string[] Algorithms = { "XPRESS4K", "XPRESS8K", "XPRESS16K", "LZX" };
    static string Compact => Path.Combine(Environment.SystemDirectory, "compact.exe");

    /// <summary>Why this folder isn't compressed here (Windows itself, a drive root, missing); null = it can be.</summary>
    public static string? Problem(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\');
        if (!Directory.Exists(full)) return "The folder doesn't exist.";
        if (Path.GetPathRoot(full)?.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) == true) return "A whole drive isn't compressed here; choose a folder.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            return "Windows' own files aren't compressed here (Windows decides that itself with CompactOS).";
        if (LinkInPath(full) is { } link) return $"{link} is a link to another place; choose the real folder.";
        return null;
    }

    /// <summary>The folder itself or one of its parents when it is a junction or symbolic link (its text says nothing of where it leads).</summary>
    static string? LinkInPath(string folder)
    {
        for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return d.FullName;
        return null;
    }

    /// <summary>A DirectStorage game: compressed files can't use BypassIO, so its loading can get slower.</summary>
    public static bool UsesDirectStorage(string folder) =>
        Files(folder).Any(f => Path.GetFileName(f) is { } n && (n.Equals("dstorage.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("dstoragecore.dll", StringComparison.OrdinalIgnoreCase)));

    public static FolderSize Measure(string folder, CancellationToken ct = default)
    {
        long logical = 0, onDisk = 0, files = 0;
        foreach (var f in Files(folder))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                logical += new FileInfo(f).Length;
                var low = GetCompressedFileSizeW(f, out var high);
                if (low != uint.MaxValue || Marshal.GetLastWin32Error() == 0) onDisk += ((long)high << 32) | low;
                files++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new FolderSize(logical, onDisk, files);
    }

    /// <summary>
    /// Compresses every file with <paramref name="algorithm"/>, or uncompresses them when it's null, then measures again.
    /// <paramref name="filesDone"/> gets the number of files compact has reported so far.
    /// </summary>
    public static async Task<(FolderSize Before, FolderSize After, int ExitCode)> RunAsync(string folder, string? algorithm, IProgress<int>? filesDone = null, CancellationToken ct = default)
    {
        if (Problem(folder) is { } problem) throw new InvalidOperationException(problem);
        if (algorithm is not null && !Algorithms.Contains(algorithm)) throw new ArgumentException($"Unknown algorithm: {algorithm}");
        if (await Task.Run(() => LinkInside(folder), ct).ConfigureAwait(false) is { } link)
            throw new InvalidOperationException($"The folder has a link to another place ({link}); compact could follow it, so it isn't compressed here.");
        var before = await Task.Run(() => Measure(folder, ct), ct).ConfigureAwait(false);
        // One folder at a time and never /S, which follows junctions: a folder that became a link since the check is refused before compact
        // gets to it. /A: hidden and system files too, /I: go on after a file that's in use.
        var dirs = await Task.Run(() => Directory.EnumerateDirectories(folder, "*", Real).Prepend(folder).ToList(), ct).ConfigureAwait(false);
        var options = algorithm is null ? "/U /A /I /EXE" : $"/C /A /I /EXE:{algorithm}";
        Log.Info($"Compression: compact {options} in {dirs.Count} folder(s) of {folder} ({before.Files} files, {before.OnDisk} bytes on disk)");
        var lines = 0;
        var code = 0;
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (LinkInPath(dir) is { } changed) throw new InvalidOperationException($"{changed} became a link to another place while compressing; stopped.");
            var r = await ProcessRunner.RunAsync(Compact, $"{options} {ProcessRunner.Quote(Path.Combine(dir, "*"))}", workingDirectory: dir, timeout: TimeSpan.FromHours(2), ct: ct,
                onLine: _ => filesDone?.Report(Interlocked.Increment(ref lines))).ConfigureAwait(false);
            if (r.ExitCode != 0) code = r.ExitCode;
        }
        var after = await Task.Run(() => Measure(folder, CancellationToken.None)).ConfigureAwait(false);
        Log.Info($"Compression: compact finished{(code != 0 ? $" (last error code {code})" : "")}; {after.OnDisk} bytes on disk now.");
        return (before, after, code);
    }

    static readonly EnumerationOptions Real = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>A link (junction, symbolic link to a folder or a file) anywhere under the folder; null when there's none.</summary>
    internal static string? LinkInside(string folder)
    {
        var here = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", Real).Prepend(folder))
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", here))
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsDedupOrCloud(entry)) return entry.FullName;
        return null;
    }

    /// <summary>Files Windows itself stores as reparse points without pointing anywhere else: WOF-compressed (what this does), dedup and cloud files.</summary>
    static bool IsDedupOrCloud(FileSystemInfo entry) => entry is FileInfo && entry.LinkTarget is null;

    /// <summary>Every file under the folder, without following links or junctions (a game folder linking elsewhere isn't walked).</summary>
    static IEnumerable<string> Files(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(folder, "*", options);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
