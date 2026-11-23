using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Drivers;

/// <summary>A USB Bluetooth adapter that Windows runs with its generic driver (no vendor driver, so no firmware for chips that need it).</summary>
public sealed record BluetoothAdapter(string InstanceId, string Name, string HardwareId);

/// <summary>A driver the Microsoft Update Catalog lists for a hardware id.</summary>
public sealed record CatalogDriver(string UpdateId, string Model, string Provider, string Version, DateTime? Date, bool Windows11);

/// <summary>The driver and where its package is: Microsoft's download server and the SHA-256 (base64) the catalog gives.</summary>
public sealed record CatalogPackage(CatalogDriver Driver, string Url, string Sha256);

public sealed class BluetoothDriverJob
{
    /// <summary>The downloaded .cab (a copy is checked and extracted in a protected folder).</summary>
    public string Package { get; set; } = "";
    /// <summary>USB\VID_0BDA&amp;PID_A729: the adapter the driver is for; the INF must list it.</summary>
    public string HardwareId { get; set; } = "";
}

/// <summary>
/// Bluetooth dongles run with Windows' generic driver when no vendor driver comes along (KOS leaves Windows Update's driver updates off).
/// Chips like the Realtek RTL8761 need the vendor driver to load their firmware: without it the radio looks fine and hears nothing.
/// This finds the adapter's own driver in the Microsoft Update Catalog by hardware id, downloads it (checked against the catalog's SHA-256
/// and Microsoft's signature) and installs it for that adapter with pnputil.
/// </summary>
public static partial class BluetoothDriver
{
    const string CatalogUrl = "https://www.catalog.update.microsoft.com/";
    const string GenericInf = "bth.inf";
    const string BluetoothClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    /// <summary>Who signs driver packages in the catalog.</summary>
    public const string Publisher = "Microsoft Windows Hardware Compatibility Publisher";
    static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(40);
    static string WorkFolder => Path.Combine(AppPaths.TheKosRoot, "Toolbox", "BluetoothDriver");
    static string Expand => Path.Combine(Environment.SystemDirectory, "expand.exe");
    static string PnpUtil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    [GeneratedRegex(@"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdPattern();

    // ---------- the adapters ----------

    /// <summary>Present USB Bluetooth adapters whose driver is the generic one (bth.inf).</summary>
    public static List<BluetoothAdapter> GenericAdapters() => PnpDevices.Enumerate("USB", presentOnly: true)
        .Where(d => d.Class.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) && HardwareIdOf(d.InstanceId) is not null && IsGeneric(d.InstanceId))
        .Select(d => new BluetoothAdapter(d.InstanceId, d.Name, HardwareIdOf(d.InstanceId)!))
        .ToList();

    /// <summary>"USB\VID_0BDA&amp;PID_A729\00E04C239987" gives "USB\VID_0BDA&amp;PID_A729"; null for a function of a composite device (&amp;MI_) or another bus.</summary>
    internal static string? HardwareIdOf(string instanceId)
    {
        var m = Regex.Match(instanceId, @"^(USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4})\\[^\\]+$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>The adapter's driver node points at Microsoft's bth.inf (the "Generic Bluetooth Adapter").</summary>
    static bool IsGeneric(string instanceId)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var device = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
            if (device?.GetValue("Driver") is not string node || !Regex.IsMatch(node, @"^\{[0-9a-fA-F-]{36}\}\\\d{4}$")) return false;
            using var driver = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{node}");
            return driver?.GetValue("InfPath") is string inf && inf.Equals(GenericInf, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>True when the adapter now has a driver of its own (anything but bth.inf).</summary>
    public static bool HasVendorDriver(string instanceId) => !IsGeneric(instanceId);

    // ---------- the catalog ----------

    /// <summary>The newest AMD64 driver the catalog lists for the hardware id, preferring one for Windows 11; null when there is none.</summary>
    public static async Task<CatalogPackage?> FindAsync(string hardwareId, CancellationToken ct)
    {
        if (!HardwareIdPattern().IsMatch(hardwareId)) throw new ArgumentException("Not a USB hardware id.", nameof(hardwareId));
        var ids = ParseSearch(await GetAsync(CatalogUrl + "Search.aspx?q=" + Uri.EscapeDataString(hardwareId), ct));
        // The catalog repeats the same package per Windows version: a few distinct titles, each with its own entry.
        var details = new List<CatalogDriver>();
        foreach (var id in ids.Take(24))
        {
            if (ParseDetails(await GetAsync(CatalogUrl + "ScopedViewInline.aspx?updateid=" + id, ct), id, hardwareId) is { } d) details.Add(d);
            if (details.Count >= 8) break;
        }
        var best = Best(details);
        if (best is null) return null;
        var body = "updateIDs=" + Uri.EscapeDataString($"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{best.UpdateId}\",\"updateID\":\"{best.UpdateId}\"}}]");
        using var request = new HttpRequestMessage(HttpMethod.Post, CatalogUrl + "DownloadDialog.aspx") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded") };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CatalogTimeout);
        using var response = await Downloader.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseDownload(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), best);
    }

    static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CatalogTimeout);
        return await Downloader.Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>The ids of the search results: &lt;a id='GUID_link'&gt;.</summary>
    internal static List<string> ParseSearch(string html) => Regex.Matches(html, @"id=['""]([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})_link['""]", RegexOptions.IgnoreCase)
        .Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, @"<(script|style)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase)
        .Replace("<", " <")), @"<[^>]+>|\s+", " ").Trim();

    /// <summary>The details page of one entry; null unless it is a driver for 64-bit Windows that lists <paramref name="hardwareId"/>.</summary>
    internal static CatalogDriver? ParseDetails(string html, string updateId, string hardwareId)
    {
        var text = Text(html);
        if (!Regex.IsMatch(text, @"Architecture:\s*AMD64\b", RegexOptions.IgnoreCase)) return null;
        var ids = Regex.Match(text, @"Hardware IDs\s+(.*?)\s+Supported Update IDs", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!ids.Success || !ids.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(hardwareId, StringComparer.OrdinalIgnoreCase)) return null;
        var version = Regex.Match(text, @"Version:\s*(\d+(?:\.\d+){1,3})\s+Version Date:\s*(\d{1,2}/\d{1,2}/\d{4})");
        if (!version.Success) return null;
        var model = Regex.Match(text, @"Driver Model:\s*(.*?)\s+Driver Provider:\s*(.*?)\s+Version:").Groups;
        var products = Regex.Match(text, @"Supported products:\s*(.*?)\s+(?:Supported languages|Driver Model|Hardware IDs|More information)", RegexOptions.Singleline).Groups[1].Value;
        return new CatalogDriver(updateId, model[1].Value, model[2].Value, version.Groups[1].Value,
            DateTime.TryParseExact(version.Groups[2].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            products.Contains("Windows 11", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The newest by date (then version); entries for Windows 11 first when the PC runs it.</summary>
    internal static CatalogDriver? Best(IEnumerable<CatalogDriver> drivers, bool windows11 = true)
    {
        var list = drivers.ToList();
        if (windows11 && list.Any(d => d.Windows11)) list = list.Where(d => d.Windows11).ToList();
        return list.OrderByDescending(d => d.Date ?? DateTime.MinValue).ThenByDescending(d => Version.TryParse(d.Version, out var v) ? v : new Version()).FirstOrDefault();
    }

    /// <summary>The download dialog's script: downloadInformation[0].files[0].url = '...'; .sha256 = '...'. Microsoft's download servers only, a .cab.</summary>
    internal static CatalogPackage? ParseDownload(string html, CatalogDriver driver)
    {
        string? Field(string name) => Regex.Match(html, $@"files\[0\]\.{name}\s*=\s*'([^']*)'").Groups[1] is { Success: true } g ? g.Value : null;
        var url = Field("url");
        var sha = Field("sha256");
        if (url is null || sha is null || !IsValidUrl(url) || sha.Length != 44) return null;
        return new CatalogPackage(driver, url, sha);
    }

    internal static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Query.Length == 0 && u.UserInfo.Length == 0
        && (u.Host.Equals("download.windowsupdate.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".download.windowsupdate.com", StringComparison.OrdinalIgnoreCase))
        && u.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase);

    // ---------- download and install ----------

    /// <summary>Downloads the package and checks it against the catalog's SHA-256 and Microsoft's signature; a bad file is deleted.</summary>
    public static async Task<string> DownloadAsync(CatalogPackage package, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (!IsValidUrl(package.Url)) throw new InvalidOperationException(Txt.BluetoothDriverBadUrl(package.Url));
        var name = "Bluetooth-" + package.Driver.UpdateId + ".cab";
        var (path, _) = await Downloader.DownloadAsync(package.Url, name, Downloader.Folder, progress, ct).ConfigureAwait(false);
        var ok = await Task.Run(() => Sha256Base64(path) == package.Sha256 && Installer.Signer(path) == Publisher, ct).ConfigureAwait(false);
        if (!ok)
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new InvalidOperationException(Txt.BluetoothDriverBadFile);
        }
        return path;
    }

    static string Sha256Base64(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>The whole fix: finds the first generic adapter's driver in the catalog, downloads it, installs it elevated and reads the result back.</summary>
    public static async Task<string> FixAsync(CancellationToken ct)
    {
        var adapters = await Task.Run(GenericAdapters, ct).ConfigureAwait(false);
        if (adapters.Count == 0) return Txt.BluetoothDriverNone;
        var adapter = adapters[0];
        Log.Info($"Bluetooth driver: {adapter.Name} ({adapter.HardwareId}) runs the generic driver; looking in the Microsoft Update Catalog.");
        var package = await FindAsync(adapter.HardwareId, ct).ConfigureAwait(false);
        if (package is null) return Txt.BluetoothDriverNotInCatalog(adapter.HardwareId);
        var file = await DownloadAsync(package, new Progress<DownloadProgress>(), ct).ConfigureAwait(false);
        var job = new BluetoothDriverJob { Package = file, HardwareId = adapter.HardwareId };
        await InstallAsync(job, ct).ConfigureAwait(false);
        // Confirmed by reading it back: the adapter no longer points at bth.inf.
        var done = false;
        for (var i = 0; i < 10 && !done; i++)
        {
            done = await Task.Run(() => HasVendorDriver(adapter.InstanceId), ct).ConfigureAwait(false);
            if (!done) await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return done ? Txt.BluetoothDriverInstalled(package.Driver.Model, package.Driver.Version) : Txt.BluetoothDriverNotConfirmed;
    }

    /// <summary>Installs elevated (in this process when already administrator).</summary>
    public static async Task InstallAsync(BluetoothDriverJob job, CancellationToken ct = default)
    {
        Validate(job);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { BluetoothDriver = job }, ct).ConfigureAwait(false);
    }

    internal static void Validate(BluetoothDriverJob job)
    {
        if (!HardwareIdPattern().IsMatch(job.HardwareId)) throw new InvalidOperationException($"Invalid hardware id: {job.HardwareId}");
        if (!DriverStore.LocalPath(job.Package) || !job.Package.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) || !File.Exists(job.Package))
            throw new InvalidOperationException($"Invalid driver package: {job.Package}");
    }

    /// <summary>
    /// Elevated. The package is copied into an administrators-only folder and checked there (Microsoft's signature), extracted, and
    /// only an INF that is a Bluetooth-class driver listing the adapter's hardware id is installed (pnputil also checks its catalog).
    /// </summary>
    internal static void Execute(BluetoothDriverJob job)
    {
        Validate(job);
        var work = WorkFolder;
        GpuTuner.PrepareDirectory(Path.GetDirectoryName(work)!);
        GpuTuner.PrepareDirectory(work);
        Clear(work);
        try
        {
            var cab = Path.Combine(work, "package.cab");
            var files = Path.Combine(work, "files");
            File.Copy(job.Package, cab);
            if (Installer.Signer(cab) != Publisher) throw new InvalidOperationException(Txt.BluetoothDriverBadFile);
            Directory.CreateDirectory(files);
            var x = ProcessRunner.Run(Expand, $"{ProcessRunner.Quote(cab)} -F:* {ProcessRunner.Quote(files)}", TimeSpan.FromMinutes(2));
            if (x.ExitCode != 0) throw new InvalidOperationException(Txt.PnpUtilFailed(x.ExitCode, x.Output));
            var inf = Directory.EnumerateFiles(files, "*.inf", SearchOption.TopDirectoryOnly).FirstOrDefault(f => IsDriverFor(File.ReadAllText(f), job.HardwareId))
                ?? throw new InvalidOperationException(Txt.BluetoothDriverNoInf(job.HardwareId));
            var r = ProcessRunner.Run(PnpUtil, $"/add-driver {ProcessRunner.Quote(inf)} /install", TimeSpan.FromMinutes(5));
            Log.Info($"Bluetooth driver: pnputil /add-driver {Path.GetFileName(inf)} /install: exit {r.ExitCode}");
            if (r.ExitCode is not (0 or 3010)) throw new InvalidOperationException(Txt.PnpUtilFailed(r.ExitCode, r.Output));
        }
        finally { Clear(work); }
    }

    /// <summary>A Bluetooth-class INF with a model line for the hardware id ("%Name% = Section, USB\VID_0BDA&amp;PID_A729").</summary>
    internal static bool IsDriverFor(string inf, string hardwareId) =>
        Regex.IsMatch(inf, @"^\s*Class\s*=\s*Bluetooth\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)
        && Regex.IsMatch(inf, @"^[^;\r\n]*=\s*[^,;\r\n]+,\s*" + Regex.Escape(hardwareId) + @"(?![0-9A-Za-z&_])", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static void Clear(string dir)
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(dir))
        {
            try
            {
                if (Directory.Exists(f) && (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) Directory.Delete(f, true);
                else File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Bluetooth driver: couldn't delete {f}: {ex.Message}"); }
        }
    }
}
