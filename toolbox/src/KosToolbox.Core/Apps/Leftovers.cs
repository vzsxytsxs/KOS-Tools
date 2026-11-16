using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Apps;

public enum LeftoverKind { Folder, File, RegistryKey, RegistryValue, Service, Task }

/// <summary>
/// Leftover of a program. Path: the folder or file; the key ("HKCU\Software\X", "HKLM\SOFTWARE\X"); the service name;
/// the task path. OnlyIfEmpty: vendor folder or key (or above the program folder), deleted only if it ends up
/// empty after the other items.
/// </summary>
public sealed record Leftover(LeftoverKind Kind, string Path, string? ValueName, string Reason, bool MachineWide, bool OnlyIfEmpty = false)
{
    public long? Size { get; init; }
    public string Id => $"{Kind}|{Path}|{ValueName}";
}

/// <summary>What is known about the program before the uninstaller: afterwards, the folder and Uninstall key may be gone.</summary>
public sealed class ProgramTrace
{
    public string Name { get; init; } = "";
    public string? Publisher { get; init; }
    /// <summary>Program folder: the InstallLocation or the uninstaller/icon folder; null if it could not be determined safely.</summary>
    public string? Folder { get; init; }
    /// <summary>Names of the .exe files in the folder (no extension), read before uninstalling.</summary>
    public IReadOnlyList<string> Executables { get; init; } = Array.Empty<string>();
    /// <summary>Uninstall key ("HKLM\SOFTWARE\...\Uninstall\X").</summary>
    public string? UninstallKey { get; init; }
    public string? MsiProductCode { get; init; }
    /// <summary>Package family (Store apps).</summary>
    public string? PackageFamily { get; init; }
    /// <summary>Id of the program in the list: with Force removal it does not protect its own files.</summary>
    public string? ProgramId { get; init; }
    /// <summary>
    /// Entry the account itself can write (Uninstall in HKCU) or Store app: the scan only returns account items
    /// (HKCU and the profile), never machine ones.
    /// </summary>
    public bool UserOnly { get; init; }
}

/// <summary>
/// Looks for leftovers of an uninstalled program, without deleting anything. Only looks in known places
/// and only accepts what has the program path or its name (exact, or with an architecture suffix). Never
/// accepts Windows folders, roots (Program Files, AppData...), folders of another installed program, or generic names.
/// </summary>
public static partial class Leftovers
{
    // ---------- names ----------

    static readonly Regex TrailingVersion = new(@"[\s_-]*[\(\[]?\s*(v(er(sion)?)?\s*)?\d+(\.\d+)+[a-z]?\s*[\)\]]?\s*$", RegexOptions.IgnoreCase);
    // "x64", "(64-bit)", "(x64 edition)", "(64-bit x64)": architecture or edition at the end of the name.
    static readonly Regex TrailingArch = new(@"[\s_-]*([\(\[][^\(\)\[\]]*\b(x64|x86|amd64|arm64|64[\s-]?bit|32[\s-]?bit|win64|win32|edition)\b[^\(\)\[\]]*[\)\]]|\b(x64|x86|amd64|arm64|64[\s-]?bit|32[\s-]?bit|win64|win32))\s*$", RegexOptions.IgnoreCase);
    static readonly Regex CompanySuffix = new(@"[\s,._-]*\b(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|sa|srl|bv|oy|ab|plc|pty|software|technologies|technology)\.?\s*$", RegexOptions.IgnoreCase);
    // "QuickCPU64", "attack shark driver v4-updater" (Electron): the program name plus a known suffix.
    static readonly Regex NameSuffix = new("^(x?64|x?86|32|64bit|32bit|amd64|arm64|win64|win32|updater)$");
    // Names that do not identify a program: never matched by name (the install folder still counts by path).
    static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "microsoft", "microsoftcorporation", "windows", "intel", "intelcorporation", "nvidia", "nvidiacorporation", "amd", "google", "googlellc",
        "apple", "appleinc", "mozilla", "common", "commonfiles", "program", "programs", "programfiles", "data", "cache", "caches", "temp", "tmp",
        "app", "apps", "application", "applications", "game", "games", "software", "system", "system32", "user", "users", "public", "default",
        "local", "roaming", "locallow", "packages", "documents", "downloads", "desktop", "startmenu", "tools", "tool", "bin", "lib", "x64", "x86",
        "setup", "update", "updater", "updates", "install", "installer", "uninstall", "uninstaller", "unins000", "launcher", "helper", "service",
        "services", "driver", "drivers", "config", "configuration", "settings", "logs", "log", "crashdumps", "crashreports", "steam", "steamapps",
        "epicgames", "plugins", "plugin", "runtime", "redist", "framework", "dotnet", "java", "elevate", "crashpadhandler", "crashreporter",
    };

    /// <summary>Letters and digits only, lowercase: "Attack Shark Driver v4" and "attack_shark-driver V4" become equal.</summary>
    internal static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The name without a trailing version or architecture ("7-Zip 23.01 (x64)" → "7-Zip").</summary>
    internal static string BaseName(string name)
    {
        var s = name.Trim();
        for (string before; (before = s) != (s = TrailingVersion.Replace(TrailingArch.Replace(s, ""), "").Trim()) && s.Length > 0;) { }
        return s.Length > 0 ? s : name.Trim();
    }

    /// <summary>Variations of the program name: the name, the name without version and the install folder name.</summary>
    internal static HashSet<string> ProductNames(string name, string? folder)
    {
        var names = new[] { name, BaseName(name), folder is null ? "" : Path.GetFileName(folder) }.Select(Norm);
        return names.Where(n => n.Length >= 3 && !Generic.Contains(n)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Variations of the vendor ("CoderBag LLC" → coderbagllc, coderbag). Vendor folder and key are only removed when empty.</summary>
    internal static HashSet<string> VendorNames(string? publisher)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(publisher)) return set;
        var s = publisher.Trim();
        set.Add(Norm(s));
        for (string before; (before = s) != (s = CompanySuffix.Replace(s, "").Trim()) && s.Length > 0;) set.Add(Norm(s));
        // Nothing from Microsoft: its folders and keys belong to Windows.
        set.RemoveWhere(n => n.Length < 3 || n.StartsWith("microsoft", StringComparison.Ordinal) || n == "windows");
        return set;
    }

    internal static bool Matches(string candidate, IReadOnlySet<string> names)
    {
        var n = Norm(candidate);
        if (n.Length < 3) return false;
        foreach (var name in names)
            if (n == name || n.StartsWith(name, StringComparison.Ordinal) && NameSuffix.IsMatch(n[name.Length..])) return true;
        return false;
    }

    static readonly HashSet<string> GenericExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "update", "updater", "uninstall", "uninst", "unins000", "unins001", "launcher", "helper", "installer", "elevate", "service",
        "crashpad_handler", "crashreporter", "notification_helper", "squirrel", "createdump", "dotnet", "java", "javaw", "python", "pythonw", "node",
    };

    // ---------- paths ----------

    /// <summary>Full path without a trailing slash (and without quotes, variables expanded); null if it is not a valid local path.</summary>
    internal static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (p.Length < 3 || p[1] != ':' || p[2] != '\\') return null;
            p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            // Short 8.3 name (PROGRA~1): comparisons use the long name; if it cannot be expanded, refuse.
            if (p.Contains('~'))
            {
                var buffer = new System.Text.StringBuilder(1024);
                var n = GetLongPathName(p, buffer, (uint)buffer.Capacity);
                if (n == 0 || n >= buffer.Capacity) return null;
                p = buffer.ToString();
            }
            return p;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);

    /// <summary>The path is the folder or is inside it.</summary>
    internal static bool IsInside(string path, string folder)
    {
        var f = folder.EndsWith('\\') ? folder : folder + "\\";
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    static string[]? _roots, _untouchable;

    /// <summary>Roots that are never deleted nor used as a "program folder": Program Files, ProgramData, the profile, AppData...</summary>
    internal static IReadOnlyList<string> Roots => _roots ??= new[]
    {
        Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.StartMenu,
        Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
        Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
    }.Select(Environment.GetFolderPath)
        .Concat(new[] { KnownFolder(LocalLowId), KnownFolder(UserProgramFilesId), KnownFolder(DownloadsId), Path.GetTempPath(),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) })
        .Select(Full).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Folders where nothing is deleted, not even inside: Windows, WindowsApps, Package Cache, the KOS folder and the toolbox folder.</summary>
    internal static IReadOnlyList<string> Untouchable => _untouchable ??= new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft"),
        AppPaths.TheKosRoot, AppPaths.DataRoot, AppContext.BaseDirectory,
    }.Select(Full).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Disk root, one of the roots (or above one) or inside an untouchable folder.</summary>
    internal static bool IsProtected(string path, IReadOnlyList<string> roots, IReadOnlyList<string> untouchable) =>
        path.Length <= 3 || roots.Any(r => IsInside(r, path)) || untouchable.Any(u => IsInside(path, u));

    // The Start Menu lives inside AppData\Microsoft and ProgramData\Microsoft: what is inside the Programs folders can be removed.
    internal static bool IsProtected(string path) =>
        IsProtected(path, Roots, Untouchable.Where(u => !(InStartMenu(path) && StartMenuFolders.Any(s => IsInside(s, u)))).ToList());

    static string[]? _startMenu;
    static IReadOnlyList<string> StartMenuFolders => _startMenu ??= new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }
        .Select(Environment.GetFolderPath).Select(Full).OfType<string>().ToArray();

    /// <summary>Inside (not equal to) one of the Start Menu Programs folders.</summary>
    internal static bool InStartMenu(string path) => StartMenuFolders.Any(s => IsInside(path, s) && !path.Equals(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Usable as a program folder to find traces by path: not a disk root, not a root, and not above one.</summary>
    internal static bool UsableFolder(string? path) => path is { Length: > 3 } p && !Roots.Any(r => IsInside(r, p));

    internal static bool InUserProfile(string path) =>
        Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } profile && IsInside(path, profile);

    static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return true; } // if the attributes cannot be read, do not risk it
    }

    // Windows known folders by GUID (Windows stores paths as "{GUID}\Folder\app.exe").
    const string LocalLowId = "A520A1A4-1780-4FF6-BD18-167343C5AF16", UserProgramFilesId = "5CD7AEE2-2219-4A67-B85D-6C9CE15660CB",
        DownloadsId = "374DE290-123F-4565-9164-39C4925E467B";

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? KnownFolder(string id)
    {
        if (!Guid.TryParse(id, out var guid) || SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    static readonly Regex KnownFolderPrefix = new(@"^\{([0-9A-Fa-f-]{36})\}(\\.*)?$");

    /// <summary>"{GUID}\Folder\app.exe" → "C:\Program Files\Folder\app.exe"; any other text is returned as is.</summary>
    internal static string ExpandKnownFolder(string text, Func<string, string?> resolve)
    {
        var m = KnownFolderPrefix.Match(text);
        return m.Success && resolve(m.Groups[1].Value) is { } folder ? folder + m.Groups[2].Value : text;
    }

    /// <summary>The command line starts with a file inside the folder (with or without quotes; the file may no longer exist).</summary>
    internal static bool StartsInside(string? command, string folder)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var s = Environment.ExpandEnvironmentVariables(command.Trim());
        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        s = s.TrimStart('"');
        return s.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File of a command or icon ("C:\App\app.exe,0", "\"C:\App\unins000.exe\" /x"); null if it is not a path.</summary>
    internal static string? ExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = Environment.ExpandEnvironmentVariables(command.Trim());
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); s = end > 1 ? s[1..end] : s.Trim('"'); }
        else
        {
            var m = Regex.Match(s, @"^.+?\.(exe|ico|dll)\b", RegexOptions.IgnoreCase);
            if (m.Success) s = m.Value;
        }
        return Full(s);
    }

    static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "bin64", "x64", "x86", "app", "program", "uninstall", "uninst", "uninstaller", "_uninst", "setup", "redist" };

    /// <summary>
    /// Program folder: the InstallLocation; otherwise the uninstaller or icon folder, outside generic places
    /// (Windows, Package Cache, msiexec). "bin" and similar go up one level.
    /// </summary>
    internal static string? FolderOf(InstalledProgram p)
    {
        if (Full(p.InstallLocation) is { } location && UsableFolder(location)) return location;
        foreach (var command in new[] { p.MsiProductCode is null ? p.UninstallString : null, p.Icon })
        {
            if (ExePath(command) is not { } exe || Path.GetDirectoryName(exe) is not { } dir) continue;
            if (GenericFolderNames.Contains(Path.GetFileName(dir)) && Path.GetDirectoryName(dir) is { } up) dir = up;
            if (Full(dir) is { } d && UsableFolder(d) && !Untouchable.Any(u => IsInside(d, u))) return d;
        }
        return null;
    }

    // ---------- data before uninstalling ----------

    public static ProgramTrace Trace(InstalledProgram p)
    {
        var folder = FolderOf(p);
        return new ProgramTrace
        {
            Name = p.Name, Publisher = p.Publisher, Folder = folder, Executables = folder is null ? Array.Empty<string>() : ExecutablesIn(folder),
            UninstallKey = InstalledPrograms.KeyPath(p), MsiProductCode = p.MsiProductCode, ProgramId = p.Id, UserOnly = p.Source == "HKCU",
        };
    }

    public static ProgramTrace Trace(StoreApp a) => new()
    {
        Name = a.Name, Publisher = a.Publisher, Folder = Full(a.Location), PackageFamily = a.FamilyName, UserOnly = true,
    };

    /// <summary>Names of the .exe files in the folder (up to 3 levels), without generic ones (setup, update, unins000...).</summary>
    static List<string> ExecutablesIn(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return new();
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(folder, "*.exe", options).Take(400).Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(n => n.Length >= 3 && !GenericExe.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }
}
