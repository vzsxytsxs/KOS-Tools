using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Tools;

/// <summary>A program in the KOS tools folder. Listing runs nothing; only <see cref="ToolDiscovery.Open"/> runs it.</summary>
public sealed record ToolProgram(string Name, string? Description, string Path, bool IsShortcut, string? Target, string? Arguments,
    string? WorkingDirectory, string? Problem)
{
    /// <summary>File used for the icon: the .exe itself or the .lnk (the shell resolves the shortcut's icon).</summary>
    public string IconPath => Path;
}

/// <summary>
/// Programs in %ProgramData%\TheKOS\tools: .exe and .lnk at the root and, in each subfolder, the shortcuts or the main
/// executable (the one starting with the folder name, preferring 64-bit; otherwise the largest). DLLs and helpers are left out.
/// </summary>
public static class ToolDiscovery
{
    public static string Root => System.IO.Path.Combine(AppPaths.TheKosRoot, "tools");

    static bool IsProgram(string file) =>
        file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    static bool IsUninstaller(string file) => System.IO.Path.GetFileName(file).StartsWith("unins", StringComparison.OrdinalIgnoreCase);

    /// <summary>Choice of a subfolder's programs. Testable without disk: takes names and sizes.</summary>
    internal static List<string> PickFromFolder(string folderName, IReadOnlyList<(string File, long Size)> files)
    {
        var candidates = files.Where(f => IsProgram(f.File) && !IsUninstaller(f.File)).ToList();
        var links = candidates.Where(f => f.File.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (links.Count > 0) return links.Select(f => f.File).ToList();

        var exes = candidates.Where(f => f.File.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var token = new string(folderName.TakeWhile(char.IsLetterOrDigit).ToArray());
        var named = token.Length == 0 ? new() : exes.Where(f => System.IO.Path.GetFileName(f.File).StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = named.Count > 0 ? named : exes;
        if (pool.Count == 0) return new();
        var x64 = pool.Where(f => System.IO.Path.GetFileNameWithoutExtension(f.File).Contains("64", StringComparison.OrdinalIgnoreCase)).ToList();
        if (x64.Count > 0) pool = x64;
        if (named.Count > 0) return pool.Select(f => f.File).ToList();
        return new() { pool.OrderByDescending(f => f.Size).First().File };
    }

    public static List<ToolProgram> Scan()
    {
        var list = new List<ToolProgram>();
        if (!Directory.Exists(Root)) return list;
        foreach (var file in Directory.GetFiles(Root).Where(f => IsProgram(f) && !IsUninstaller(f)))
            list.Add(Describe(file, null));
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var folderName = System.IO.Path.GetFileName(dir);
            var files = Directory.GetFiles(dir).Select(f => (f, new FileInfo(f).Length)).ToList();
            var picked = PickFromFolder(folderName, files);
            foreach (var f in picked) list.Add(Describe(f, picked.Count == 1 ? folderName : null));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Name and description from the file's own metadata (without running it).</summary>
    static ToolProgram Describe(string path, string? folderName)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            try
            {
                var link = ShellLink.Read(path);
                var target = link.Target is null ? null : Environment.ExpandEnvironmentVariables(link.Target);
                string? problem = target is null ? Txt.ShortcutNoTarget : File.Exists(target) || Directory.Exists(target) ? null : Txt.ShortcutBrokenTarget(target);
                var desc = link.Description ?? (target is not null && File.Exists(target) ? Version(target).Description : null);
                return new ToolProgram(name, Different(desc, name), path, true, target, link.Arguments, link.WorkingDirectory, problem);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tools: couldn't read shortcut {path}: {ex.Message}");
                return new ToolProgram(name, null, path, true, null, null, null, Txt.ShortcutUnreadable(ex.Message));
            }
        }
        var (product, description) = Version(path);
        var display = folderName is not null ? folderName.Replace('_', ' ')
            : product ?? System.IO.Path.GetFileNameWithoutExtension(path);
        return new ToolProgram(display, Different(description, display), path, false, null, null, null, null);
    }

    static (string? Product, string? Description) Version(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            var product = Clean(v.ProductName);
            // Generic names don't identify the tool.
            if (product is not null && product.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) product = null;
            return (product, Clean(v.FileDescription));
        }
        catch { return (null, null); }
    }

    static string? Different(string? description, string name) =>
        description is null || description.Equals(name, StringComparison.OrdinalIgnoreCase) ? null : description;

    /// <summary>
    /// With the toolbox elevated, the program would also run elevated. A file not owned by SYSTEM, by
    /// Administrators or by TrustedInstaller may have been replaced by a standard user.
    /// </summary>
    public static string? OwnerProblem(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")) return null;
            return Txt.ToolOwnerProblem(path, owner?.Value);
        }
        catch (Exception ex) { return Txt.ToolOwnerUnknown(path, ex.Message); }
    }

    /// <summary>Opens the program through the shell, like a double-click: shortcuts keep their arguments, folder and window mode.</summary>
    public static void Open(ToolProgram p)
    {
        if (p.Problem is not null) throw new InvalidOperationException(p.Problem);
        if (!File.Exists(p.Path)) throw new FileNotFoundException(Txt.ToolMissing(p.Path), p.Path);
        Log.Info($"Tools: opening {p.Path}{(Elevation.IsAdmin ? " (as administrator)" : "")}");
        Process.Start(new ProcessStartInfo(p.Path)
        {
            UseShellExecute = true,
            WorkingDirectory = p.IsShortcut ? "" : System.IO.Path.GetDirectoryName(p.Path)!,
        })?.Dispose();
    }

    public static void OpenFolder()
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Txt.FolderNotFound(Root));
        Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(Root)) { UseShellExecute = true })?.Dispose();
    }
}
