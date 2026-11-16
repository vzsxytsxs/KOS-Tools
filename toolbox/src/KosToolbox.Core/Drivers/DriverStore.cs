using System.Globalization;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Drivers;

/// <summary>Third-party driver package in the driver store (what "pnputil /enum-drivers" lists as oemNN.inf).</summary>
public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider, string Class, DateTime? Date, Version? Version,
    long Size, IReadOnlyList<string> Devices)
{
    public bool InUse => Devices.Count > 0;
}

public sealed class DriverJob
{
    public List<string> Delete { get; set; } = new();
    /// <summary>pnputil /uninstall: removes the driver from the devices using it before deleting the package.</summary>
    public bool Uninstall { get; set; }
    /// <summary>pnputil /force: deletes even with devices using it (full removal of the video driver).</summary>
    public bool Force { get; set; }
    public string? Export { get; set; }
    public string? ExportFolder { get; set; }
    /// <summary>Extracted video INF to install (pnputil /add-driver /install).</summary>
    public string? Install { get; set; }
}

/// <summary>
/// Driver store read directly from Windows (INF published in %windir%\INF, package in DriverDatabase, devices through the
/// class InfPath), without depending on pnputil's translated text. Export and delete use the official pnputil.exe.
/// </summary>
public static partial class DriverStore
{
    static string InfDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
    static string Repository => Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
    static string PnpUtil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedNamePattern();

    public static List<DriverPackage> List()
    {
        var users = DevicesByInf();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var list = new List<DriverPackage>();
        ILookup<long, string>? repository = null; // index of the FileRepository INFs, built only if needed
        foreach (var inf in Directory.EnumerateFiles(InfDir, "oem*.inf"))
        {
            var name = Path.GetFileName(inf);
            if (!PublishedNamePattern().IsMatch(name)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(inf); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"{name} couldn't be read: {ex.Message}"); continue; }
            var info = ParseInf(lines);
            using var map = hklm.OpenSubKey($@"SYSTEM\DriverDatabase\DriverInfFiles\{name}");
            var folder = map?.GetValue("Active") is string package ? Path.Combine(Repository, package) : FindInRepository(inf, ref repository);
            list.Add(new DriverPackage(name, folder is null ? name : Path.GetFileName(folder).Split(".inf_")[0] + ".inf", info.Provider ?? "", info.Class ?? "",
                info.Date, info.Version, folder is null ? 0 : FolderSize(folder), users.GetValueOrDefault(name) ?? new List<string>()));
        }
        return list.OrderBy(p => p.Class, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Package of an oemNN.inf that is not in the DriverDatabase (e.g. driver injected into the image): the published INF is a
    /// byte-for-byte copy of the INF that sits in the package folder in FileRepository.
    /// </summary>
    static string? FindInRepository(string inf, ref ILookup<long, string>? repository)
    {
        repository ??= Directory.Exists(Repository)
            ? Directory.EnumerateDirectories(Repository).SelectMany(d => Directory.EnumerateFiles(d, "*.inf")).ToLookup(f => new FileInfo(f).Length)
            : Enumerable.Empty<string>().ToLookup(f => 0L);
        try
        {
            var bytes = File.ReadAllBytes(inf);
            return repository[bytes.LongLength].FirstOrDefault(f => File.ReadAllBytes(f).AsSpan().SequenceEqual(bytes)) is { } match ? Path.GetDirectoryName(match) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Class, Provider and DriverVer from the [Version] section, with %tokens% resolved from the [Strings] section.</summary>
    internal static (string? Class, string? Provider, DateTime? Date, Version? Version) ParseInf(IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Split(';')[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                current = sections.TryGetValue(section, out var existing) ? existing : sections[section] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            current.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim().Trim('"'));
        }
        var version = sections.GetValueOrDefault("Version") ?? new();
        var strings = sections.GetValueOrDefault("Strings") ?? new();
        string? Resolve(string key) => version.TryGetValue(key, out var v)
            ? Regex.Replace(v, "%([^%]+)%", m => strings.TryGetValue(m.Groups[1].Value, out var s) ? s : m.Value) : null;
        var driverVer = version.GetValueOrDefault("DriverVer")?.Split(',');
        DateTime? date = driverVer is { Length: > 0 } && DateTime.TryParseExact(driverVer[0].Trim(), new[] { "MM/dd/yyyy", "M/d/yyyy" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        Version? ver = driverVer is { Length: > 1 } && Version.TryParse(driverVer[1].Trim(), out var parsed) ? parsed : null;
        return (Resolve("Class"), Resolve("Provider"), date, ver);
    }

    /// <summary>Device instances (name) per published INF, by the InfPath of the class keys.</summary>
    static Dictionary<string, List<string>> DevicesByInf()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var classes = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
        if (classes is null) return map;
        foreach (var cls in classes.GetSubKeyNames())
        {
            using var c = SafeOpen(classes, cls);
            if (c is null) continue;
            foreach (var instance in c.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                using var k = SafeOpen(c, instance);
                if (k?.GetValue("InfPath") is not string inf) continue;
                (map.TryGetValue(inf, out var l) ? l : map[inf] = new()).Add(k.GetValue("DriverDesc") as string ?? $"{cls}\\{instance}");
            }
        }
        return map;
    }

    // Some class keys (e.g. Properties) deny read even to administrators; they are just left out.
    static RegistryKey? SafeOpen(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (System.Security.SecurityException) { return null; }
    }

    static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    /// <summary>Packages with the same original INF and provider as a newer one (version, then date): removal candidates.</summary>
    public static HashSet<string> OldVersions(IEnumerable<DriverPackage> packages) =>
        packages.GroupBy(p => (p.OriginalName.ToLowerInvariant(), p.Provider.ToLowerInvariant()))
            .SelectMany(g => g.OrderByDescending(p => p.Version ?? new Version()).ThenByDescending(p => p.Date ?? DateTime.MinValue).Skip(1))
            .Select(p => p.PublishedName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Wider match: every package older than another of the same provider and class. It can also pick the driver of another
    /// device from the same maker, so the list must be checked before deleting.
    /// </summary>
    public static HashSet<string> OlderByProviderAndClass(IEnumerable<DriverPackage> packages) =>
        packages.Where(p => p.Provider.Length > 0 && p.Class.Length > 0)
            .GroupBy(p => (p.Provider.ToLowerInvariant(), p.Class.ToLowerInvariant()))
            .SelectMany(g => g.OrderByDescending(p => p.Version ?? new Version()).ThenByDescending(p => p.Date ?? DateTime.MinValue).Skip(1))
            .Select(p => p.PublishedName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---------- changes (administrator) ----------

    public static Task DeleteAsync(IReadOnlyList<string> names, bool uninstall, CancellationToken ct = default, bool force = false) =>
        RunAsync(new DriverJob { Delete = names.ToList(), Uninstall = uninstall, Force = force }, ct);

    public static Task ExportAsync(string name, string folder, CancellationToken ct = default) =>
        RunAsync(new DriverJob { Export = name, ExportFolder = folder }, ct);

    /// <summary>Installs an extracted video INF (the driver only, without the vendor installer's panel and services).</summary>
    public static Task InstallAsync(string inf, CancellationToken ct = default) => RunAsync(new DriverJob { Install = inf }, ct);

    static async Task RunAsync(DriverJob job, CancellationToken ct)
    {
        Validate(job);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Drivers = job }, ct).ConfigureAwait(false);
    }

    public static bool Exists(string publishedName) => File.Exists(Path.Combine(InfDir, publishedName));

    internal static bool LocalPath(string? p) => p is not null && Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Only existing oemNN.inf; export to an existing local folder; install only a local Display-class INF.</summary>
    internal static void Validate(DriverJob job)
    {
        var kinds = new[] { job.Delete.Count > 0, job.Export is not null, job.Install is not null }.Count(b => b);
        if (kinds != 1) throw new InvalidOperationException("The driver request must do exactly one thing.");
        if (job.Install is { } inf)
        {
            if (!LocalPath(inf) || !inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || !File.Exists(inf) || !DisplayDrivers.IsDisplayInf(inf))
                throw new InvalidOperationException(Txt.DisplayInfInvalid);
            if (job.Uninstall || job.Force || job.ExportFolder is not null) throw new InvalidOperationException("Invalid driver request.");
            return;
        }
        if (job.Force && job.Delete.Count == 0) throw new InvalidOperationException("Invalid driver request.");
        foreach (var n in job.Export is null ? job.Delete : new List<string> { job.Export })
            if (!PublishedNamePattern().IsMatch(n) || !Exists(n)) throw new InvalidOperationException($"Not a third-party driver package: {n}");
        if (job.Delete.Count > 500 || job.Delete.Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Delete.Count)
            throw new InvalidOperationException("Invalid driver list.");
        if (job.Export is not null)
        {
            if (!LocalPath(job.ExportFolder) || !Directory.Exists(job.ExportFolder)) throw new InvalidOperationException(Txt.DriverExportFolderInvalid);
        }
        else if (job.ExportFolder is not null) throw new InvalidOperationException("Invalid driver request.");
    }

    /// <summary>Runs pnputil; the result is checked afterwards by the UI process (published file gone, exported INF exists).</summary>
    internal static void Execute(DriverJob job)
    {
        Validate(job);
        if (job.Export is { } name)
        {
            var r = ProcessRunner.Run(PnpUtil, $"/export-driver {name} {ProcessRunner.Quote(job.ExportFolder!)}", TimeSpan.FromMinutes(5));
            Log.Info($"pnputil /export-driver {name}: exit {r.ExitCode}");
            if (r.ExitCode != 0) throw new InvalidOperationException(Txt.PnpUtilFailed(r.ExitCode, r.Output));
            return;
        }
        if (job.Install is { } inf)
        {
            var r = ProcessRunner.Run(PnpUtil, $"/add-driver {ProcessRunner.Quote(inf)} /install", TimeSpan.FromMinutes(15));
            Log.Info($"pnputil /add-driver {inf} /install: exit {r.ExitCode}");
            if (r.ExitCode is not (0 or 3010)) throw new InvalidOperationException(Txt.PnpUtilFailed(r.ExitCode, r.Output));
            return;
        }
        var flags = (job.Uninstall ? " /uninstall" : "") + (job.Force ? " /force" : "");
        foreach (var n in job.Delete)
        {
            var r = ProcessRunner.Run(PnpUtil, $"/delete-driver {n}{flags}", TimeSpan.FromMinutes(5));
            Log.Info($"pnputil /delete-driver {n}{flags}: exit {r.ExitCode}");
        }
    }
}
