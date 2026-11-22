using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KosToolbox.Core.Storage;

/// <summary>The biggest files directly inside one folder, and what the rest of its files add up to (for the disk map's "files here" cell).</summary>
public sealed record FolderFiles(IReadOnlyList<LargeFile> Top, long RestSize, int RestCount)
{
    /// <summary>
    /// Lists the files of one folder (no links, as in the scan), the <paramref name="top"/> biggest by size and a total for the others.
    /// Reads one folder, so it is quick even where a whole scan wasn't.
    /// </summary>
    public static FolderFiles Read(string folder, int top = 150, CancellationToken ct = default)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var queue = new PriorityQueue<LargeFile, long>();
        long restSize = 0;
        var restCount = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                long size;
                try { size = file.Length; } catch (IOException) { continue; }
                queue.Enqueue(new LargeFile(file.FullName, size), size);
                if (queue.Count > top)
                {
                    queue.TryDequeue(out var dropped, out _);
                    restSize += dropped!.Size;
                    restCount++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was listed stays */ }
        var list = new List<LargeFile>(queue.Count);
        while (queue.TryDequeue(out var f, out _)) list.Add(f);
        list.Reverse();
        return new FolderFiles(list, restSize, restCount);
    }
}

/// <summary>
/// Deleting what the disk map shows: to the Recycle Bin or for good. Refuses the places a mistake would hurt most (a drive's root, the
/// Windows, Program Files and user folders themselves, anything inside Windows except its Temp folder, the page file), and never
/// follows a link. Nothing here asks: the caller's menu item is the choice.
/// </summary>
public static class StorageDelete
{
    /// <summary>Why this path must not be deleted from here, or null.</summary>
    public static string? Problem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return Text.Txt.StorageDeleteNotAPath;
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var root = Path.GetPathRoot(Path.GetFullPath(path))!.TrimEnd('\\');
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return Text.Txt.StorageDeleteDrive;
        var name = Path.GetFileName(full);
        if (string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
            && (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)))
            return Text.Txt.StorageDeleteSystemItem(name);

        // The folders the system and its users live in: deleting one, or a folder above one, would take it along.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var keep = new[]
        {
            windows, Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            profile, Path.GetDirectoryName(profile) ?? "", AppContext.BaseDirectory.TrimEnd('\\'),
        };
        foreach (var k in keep.Where(k => k.Length > 0))
            if (TakesAlong(full, k)) return Text.Txt.StorageDeleteProtected(Path.GetFileName(k.TrimEnd('\\')) is { Length: > 0 } n ? n : k);
        // Inside Windows only the contents of its Temp folder are fair game: the rest is the operating system.
        var temp = Path.Combine(windows, "Temp");
        if (IsInside(windows, full) && !(IsInside(temp, full) && !string.Equals(full, temp, StringComparison.OrdinalIgnoreCase)))
            return Text.Txt.StorageDeleteInsideWindows;
        return null;
    }

    /// <summary><paramref name="path"/> is <paramref name="protectedFolder"/> or a folder above it.</summary>
    static bool TakesAlong(string path, string protectedFolder) =>
        (protectedFolder.TrimEnd('\\') + '\\').StartsWith(path.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    static bool IsInside(string folder, string path) =>
        (path.TrimEnd('\\') + '\\').StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    /// <summary>Moves a file or folder to the Recycle Bin; null when done, else why not. Windows warns before anything too big for the bin is deleted for good.</summary>
    public static string? Recycle(string path)
    {
        if (Problem(path) is { } problem) return problem;
        var op = new ShFileOp
        {
            Function = 3, // FO_DELETE
            From = path + "\0\0",
            Flags = 0x0040 /* FOF_ALLOWUNDO */ | 0x4000 /* FOF_WANTNUKEWARNING */ | 0x0400 /* FOF_NOERRORUI */ | 0x0010 /* FOF_NOCONFIRMATION: the bin is the safety */,
        };
        var code = SHFileOperation(ref op);
        if (code != 0) return new Win32Exception(code).Message;
        return op.Aborted ? Text.Txt.StorageDeleteCancelled : null;
    }

    /// <summary>Deletes for good (a link inside a folder is removed, not followed); null when done, else the first error.</summary>
    public static string? Permanent(string path)
    {
        if (Problem(path) is { } problem) return problem;
        try
        {
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return null;
            }
            return Directory.Exists(path) ? Apps.Leftovers.DeleteTree(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    // SHFILEOPSTRUCTW (x64 layout); the paths end with two nulls.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOp op);
}
