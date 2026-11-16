using KosToolbox.Core.Apps;

namespace KosToolbox.Core.Debloat;

/// <summary>
/// Finding and deleting an app's files without ever following a link: with the toolbox elevated, a link planted in a folder the
/// user can write would otherwise turn these deletes against system files.
/// </summary>
internal static class DebloatFiles
{
    static readonly EnumerationOptions NoLinks = new() { AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Win32, IgnoreInaccessible = true };

    internal static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // unreadable: don't risk it
    }

    /// <summary>The folder exists and is a real folder.</summary>
    internal static bool IsFolder(string path) => Directory.Exists(path) && !IsLink(path);

    internal static IEnumerable<string> Folders(string parent, string pattern) =>
        IsFolder(parent) ? Directory.EnumerateDirectories(parent, pattern, NoLinks) : Array.Empty<string>();

    internal static IEnumerable<string> Files(string parent, string pattern) =>
        IsFolder(parent) ? Directory.EnumerateFiles(parent, pattern, NoLinks) : Array.Empty<string>();

    /// <summary>Deletes files and folders (a link inside is removed, not followed); throws with the first failure.</summary>
    internal static void Delete(IEnumerable<string> paths)
    {
        string? error = null;
        foreach (var path in paths.ToList())
        {
            try
            {
                if (Directory.Exists(path)) error ??= Leftovers.DeleteTree(path);
                else if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= $"{Path.GetFileName(path)}: {ex.Message}"; }
        }
        if (error is not null) throw new InvalidOperationException(error);
    }

    /// <summary>A folder that is a link hides its contents from the options, so it must not read as already cleaned.</summary>
    internal static bool IsRealOrMissing(string path) => !Directory.Exists(path) || !IsLink(path);

    internal static DebloatState StateOfRemoval(int remaining) => remaining == 0 ? DebloatState.Applied : DebloatState.NotApplied;
}
