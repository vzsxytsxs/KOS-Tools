using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.Core.Drivers;

/// <summary>A driver NVIDIA publishes for the card.</summary>
public sealed record NvidiaRelease(string Version, DateTime? Date, string Url, string? Size, bool Studio, string Name)
{
    public string FileName => Path.GetFileName(new Uri(Url).AbsolutePath);
}

/// <summary>The NVIDIA card the lookup was made for and the drivers found (newest first).</summary>
public sealed record NvidiaLookup(string Gpu, List<NvidiaRelease> Releases, bool ByDeviceId);

/// <summary>A top-level folder of the driver package: one component setup.exe installs when the folder is there.</summary>
public sealed record NvidiaComponent(string Folder, string Name, bool Required);

/// <summary>Where a running install is: <see cref="NvidiaDriver.StepCopy"/> and the other steps; Percent null = no measure (setup.exe).</summary>
public sealed record NvidiaInstallProgress(string Step, double? Percent);

public sealed class NvidiaInstallJob
{
    /// <summary>The downloaded package; the elevated side copies it into a protected folder before checking NVIDIA's signature.</summary>
    public string Package { get; set; } = "";
    /// <summary>Optional components (top-level folders of the package) to install besides <see cref="NvidiaDriver.Required"/>.</summary>
    public List<string> Components { get; set; } = new();
    /// <summary>setup.exe -clean: removes the previous driver's settings and profiles.</summary>
    public bool Clean { get; set; }
    /// <summary>Ids from <see cref="NvidiaDriver.PostInstallTweaks"/>, applied after setup.</summary>
    public List<string> Tweaks { get; set; } = new();
}

/// <summary>
/// NVIDIA driver from NVIDIA's own services: the drivers for the card (by its name, or by its PCI device id when Windows
/// only shows the basic display adapter), the download, and an install with only the chosen components: the package is
/// extracted with 7-Zip without the other component folders and setup.exe runs silently, then the chosen tweaks are applied.
/// </summary>
public static class NvidiaDriver
{
    const string ProductsUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    const string SeriesUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2";
    const string ReleasesUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={0}&pfid={1}&osID=57&languageCode=1033&dch=1&upCRD={2}&sort1=0&numberOfResults={3}";
    /// <summary>The public PCI ID database: the card's name by its PCI device id, for a PC without an NVIDIA driver (Windows then calls the card "Microsoft Basic Display Adapter").</summary>
    const string PciIdsUrl = "https://raw.githubusercontent.com/pciutils/pciids/master/pci.ids";
    const string DeviceUrl = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";
    /// <summary>The newest drivers come back in about 10 s; every driver NVIDIA still lists for the card (hundreds, from the first of its series) takes a minute or two, at about 0.4 s each.</summary>
    public const int RecentCount = 25, AllCount = 500;
    public const string Publisher = "NVIDIA Corporation";
    // Setup stays under the 30 minutes the unelevated side waits, so a second Install can't overlap a running one.
    static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(25), SevenZipTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Always installed: without them setup.exe has nothing to install.</summary>
    public static readonly IReadOnlyList<string> Required = new[] { "Display.Driver", "NVI2" };

    /// <summary>Settings applied after setup, with the option they get. Recomputed after setup: a clean install can change the card's class key.</summary>
    public static readonly IReadOnlyList<(string Id, string Option, string Name, string Description)> PostInstallTweaks = new[]
    {
        ("nv-msi", Tweak.On, "MSI mode", "Message-signaled interrupts for the GPU (GPU › Tweaks › MSI mode)."),
        ("nv-hdcp", Tweak.Off, "Turn off HDCP", "Protected video (streaming services, Blu-ray) may stop playing (GPU › Tweaks › HDCP)."),
        ("nv-telemetry", Tweak.Off, "Turn off driver telemetry", "SendTelemetryData = 0 (GPU › Tweaks › Driver telemetry)."),
    };

    /// <summary>Folder names of the 600-series packages, plus a few of older ones. The Control Panel app comes inside Display.Driver.</summary>
    static readonly Dictionary<string, string> KnownComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Display.Driver"] = "Display driver (with the Control Panel app)",
        ["NVI2"] = "Installer core (NVI2)",
        ["HDAudio"] = "HD Audio (sound over HDMI and DisplayPort)",
        ["PhysX"] = "PhysX system software",
        ["NvApp"] = "NVIDIA app",
        ["NvApp.MessageBus"] = "Message bus (NVIDIA app)",
        ["NvBackend"] = "Backend (NVIDIA app)",
        ["NvContainer"] = "NVIDIA Container (NVIDIA app services)",
        ["ShadowPlay"] = "ShadowPlay (overlay and capture)",
        ["NvDLISR"] = "DL image super resolution (NVIDIA app)",
        ["FrameViewSDK"] = "FrameView SDK",
        ["MSVCRT"] = "Visual C++ runtime (for the NVIDIA app)",
        ["NvCpl"] = "NvCpl (Control Panel extras)",
        ["NvTelemetry"] = "Telemetry",
        ["NvVAD"] = "Virtual audio device (streaming)",
        ["NVPCF"] = "Dynamic Boost (laptops, NVPCF)",
        ["PPC"] = "USB-C driver (VirtualLink)",
        ["GFExperience"] = "GeForce Experience",
        ["Display.Nview"] = "nView desktop manager",
        ["Display.Optimus"] = "Optimus (laptops with two GPUs)",
        ["ShieldWirelessController"] = "SHIELD wireless controller",
    };

    /// <summary>Where the elevated side copies, checks and extracts the package (only administrators can write).</summary>
    static string WorkFolder => Path.Combine(AppPaths.TheKosRoot, "Toolbox", "NvidiaDriver");
    /// <summary>Written by the (elevated) install, read by the window: "step|percent". Next to the work folder, so only administrators write it.</summary>
    static string ProgressFile => WorkFolder + ".progress";
    public const string StepCopy = "copy", StepExtract = "extract", StepSetup = "setup", StepTweaks = "tweaks";

    // ---------- lookup ----------

    /// <summary>NVIDIA display adapters: device name ("NVIDIA GeForce GTX 1650 SUPER", or the basic display adapter without a driver) and PCI device id ("2187").</summary>
    public static List<(string Name, string DeviceId)> Adapters() => PnpDevices.Enumerate("PCI", presentOnly: true)
        .Where(d => d.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && d.InstanceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        .Select(d => (d.Name, Regex.Match(d.InstanceId, @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant()))
        .Where(a => a.Item2.Length == 4)
        .ToList();

    /// <summary>
    /// Drivers for the first NVIDIA card: by its name in NVIDIA's product list (a version list); when the name doesn't match
    /// (no driver installed) or that lookup fails, the newest driver by PCI device id. <paramref name="all"/>: every driver NVIDIA lists for
    /// the card, old ones too, instead of the newest <see cref="RecentCount"/>.
    /// </summary>
    public static async Task<NvidiaLookup> ListAsync(bool studio, bool all, CancellationToken ct)
    {
        var adapters = Adapters();
        if (adapters.Count == 0) throw new InvalidOperationException(Txt.NvidiaNoGpu);
        var (name, deviceId) = adapters[0];
        var laptop = HasBattery();
        try
        {
            var products = ParseLookup(await GetAsync(ProductsUrl, ct));
            var matches = MatchProducts(products, name);
            if (matches.Count == 0)
            {
                // No driver, so no name: the PCI id gives the card's real name (GeForce GTX 1650 SUPER), then the full list works.
                try { if (PciName(await GetAsync(PciIdsUrl, ct, TimeSpan.FromSeconds(60)), deviceId) is { } pciName) matches = MatchProducts(products, pciName); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                { Log.Warn($"NVIDIA driver lookup: the PCI id database couldn't be read: {ex.Message}"); }
            }
            if (matches.Count > 0)
            {
                var product = matches[0];
                if (matches.Count > 1)
                {
                    var notebooks = ParseLookup(await GetAsync(SeriesUrl, ct)).Where(s => s.Name.Contains("(Notebooks)", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToHashSet();
                    product = matches.FirstOrDefault(m => notebooks.Contains(m.Parent) == laptop) ?? product;
                }
                var url = string.Format(CultureInfo.InvariantCulture, ReleasesUrl, product.Parent, product.Value, studio ? 1 : 0, all ? AllCount : RecentCount);
                var releases = ParseReleases(await GetAsync(url, ct, all ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(30)), studio);
                if (releases.Count > 0) return new(name, releases, false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"NVIDIA driver lookup by name failed for {name}: {ex.Message}");
        }
        var latest = ParseDeviceLookup(await GetAsync(DeviceUrl + Uri.EscapeDataString(DeviceQuery(deviceId, laptop, studio)), ct), studio);
        return new(name, latest is null ? new() : new() { latest }, true);
    }

    static async Task<string> GetAsync(string url, CancellationToken ct, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(30));
        return await Downloader.Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
    }

    internal static string DeviceQuery(string deviceId, bool laptop, bool studio)
    {
        var crd = studio ? "1" : "0";
        var build = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        return $"{{\"dIDa\":[\"{deviceId}_10DE\"],\"osC\":\"10.0\",\"osB\":\"{build}\",\"is6\":\"1\",\"lg\":\"1033\",\"iLp\":\"{(laptop ? 1 : 0)}\",\"prvMd\":\"0\",\"gcV\":\"3.28.0.417\",\"gIsB\":\"0\",\"dch\":\"1\",\"upCRD\":\"{crd}\",\"isCRD\":\"{crd}\"}}";
    }

    /// <summary>
    /// The name of an NVIDIA device in pci.ids: under the line "10de  NVIDIA Corporation", "\t2187  TU116 [GeForce GTX 1650 SUPER]" gives
    /// "GeForce GTX 1650 SUPER" (the part in brackets when there is one). Null when the id isn't listed.
    /// </summary>
    internal static string? PciName(string pciIds, string deviceId)
    {
        var inNvidia = false;
        foreach (var raw in pciIds.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] != '\t') { inNvidia = line.StartsWith("10de", StringComparison.OrdinalIgnoreCase) && line.Length > 4 && line[4] == ' '; continue; }
            if (!inNvidia || line.Length < 7 || line[1] == '\t') continue; // not NVIDIA, or a subsystem line
            if (!line.AsSpan(1, 4).Equals(deviceId, StringComparison.OrdinalIgnoreCase) || line[5] != ' ') continue;
            var name = line[5..].Trim();
            var open = name.LastIndexOf('[');
            return open >= 0 && name.EndsWith(']') ? name[(open + 1)..^1].Trim() : name;
        }
        return null;
    }

    internal sealed record LookupValue(string Name, int Value, int Parent);

    /// <summary>NVIDIA's lookupValueSearch XML: &lt;LookupValue ParentID="112"&gt;&lt;Name&gt;GeForce GTX 1650 SUPER&lt;/Name&gt;&lt;Value&gt;911&lt;/Value&gt;.</summary>
    internal static List<LookupValue> ParseLookup(string xml) => XDocument.Parse(xml).Descendants("LookupValue")
        .Select(v => (Name: (string?)v.Element("Name"), Value: (string?)v.Element("Value"), Parent: (string?)v.Attribute("ParentID")))
        .Where(v => v.Name is not null && int.TryParse(v.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _) && int.TryParse(v.Parent, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        .Select(v => new LookupValue(v.Name!.Trim(), int.Parse(v.Value!, CultureInfo.InvariantCulture), int.Parse(v.Parent!, CultureInfo.InvariantCulture)))
        .ToList();

    /// <summary>Products with exactly the device's name (ignoring case, extra spaces and a leading "NVIDIA "). A desktop and a notebook product can share it.</summary>
    internal static List<LookupValue> MatchProducts(IEnumerable<LookupValue> products, string deviceName)
    {
        var name = Normalize(deviceName);
        return products.Where(p => Normalize(p.Name) == name).ToList();
    }

    static string Normalize(string name)
    {
        var s = Regex.Replace(name.Trim(), @"\s+", " ");
        if (s.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        return s.ToUpperInvariant();
    }

    /// <summary>DriverManualLookup JSON: IDS[].downloadInfo with Version, ReleaseDateTime ("Wed Nov 04, 2026"), DownloadURL. Betas are left out.</summary>
    internal static List<NvidiaRelease> ParseReleases(string json, bool studio)
    {
        var list = new List<NvidiaRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in ids.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("downloadInfo", out var d) || d.ValueKind != JsonValueKind.Object) continue;
            if (Field(d, "IsBeta") == "1") continue;
            if (Release(d, Field(d, "DownloadURL"), studio) is { } r) list.Add(r);
        }
        return list;
    }

    /// <summary>getDispDrvrByDevid JSON: DriverAttributes with Version, ReleaseDateTime (ISO) and DownloadURLAdmin (the standard package).</summary>
    internal static NvidiaRelease? ParseDeviceLookup(string json, bool studio)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("DriverAttributes", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        return Release(d, Field(d, "DownloadURLAdmin") ?? Field(d, "DownloadURL"), studio);
    }

    static NvidiaRelease? Release(JsonElement d, string? url, bool studio)
    {
        var version = Field(d, "Version");
        if (url is null || version is null || !Regex.IsMatch(version, @"^\d{3}\.\d{2}$") || !IsValidUrl(url)) return null;
        var dateText = Field(d, "ReleaseDateTime");
        DateTime? date = DateTime.TryParseExact(dateText, "ddd MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ? a
            : DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b) ? b.UtcDateTime.Date : null;
        var name = Uri.UnescapeDataString(Field(d, "Name") ?? "");
        return new NvidiaRelease(version, date, url, Field(d, "DownloadURLFileSize"), studio || Field(d, "IsCRD") == "1", name.Length > 0 ? name : studio ? "NVIDIA Studio Driver" : "GeForce Game Ready Driver");
    }

    /// <summary>A string or number field as text; null when missing or empty.</summary>
    static string? Field(JsonElement e, string name) => e.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null, JsonValueKind.Number => v.GetRawText(), _ => null }
        : null;

    /// <summary>Only NVIDIA's download servers over https, and an .exe.</summary>
    internal static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Query.Length == 0 && u.UserInfo.Length == 0
        && (u.Host.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase))
        && u.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows driver version (31.0.15.3758) as NVIDIA writes it (537.58); null if it doesn't have that form.</summary>
    public static string? FriendlyVersion(string? windowsVersion)
    {
        var parts = windowsVersion?.Split('.');
        if (parts is not { Length: 4 } || parts[2].Length == 0 || parts[3].Length > 4 || !parts.All(p => p.All(char.IsAsciiDigit))) return null;
        var digits = parts[2][^1..] + parts[3].PadLeft(4, '0');
        return $"{digits[..3]}.{digits[3..]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>A system battery means a notebook: NVIDIA has separate notebook drivers. 128 = no system battery, 255 = unknown.</summary>
    static bool HasBattery() => GetSystemPowerStatus(out var s) && s.BatteryFlag is not (128 or 255);

    // ---------- download and components ----------

    /// <summary>The package already in the downloads folder (signed by NVIDIA), or a new download.</summary>
    public static async Task<string> DownloadAsync(NvidiaRelease release, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (!IsValidUrl(release.Url)) throw new InvalidOperationException(Txt.NvidiaBadUrl(release.Url));
        var existing = Path.Combine(Downloader.Folder, release.FileName);
        if (File.Exists(existing) && await Task.Run(() => Installer.Signer(existing), ct).ConfigureAwait(false) == Publisher) return existing;
        var (path, _) = await Downloader.DownloadAsync(release.Url, release.FileName, Downloader.Folder, progress, ct).ConfigureAwait(false);
        if (await Task.Run(() => Installer.Signer(path), ct).ConfigureAwait(false) != Publisher)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new InvalidOperationException(Txt.NvidiaNotSigned(Path.GetFileName(path)));
        }
        return path;
    }

    /// <summary>7-Zip: the installed one, else the copy in the KOS tools folder. Elevated, only one owned by SYSTEM/Administrators/TrustedInstaller.</summary>
    internal static string? SevenZip()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(ToolDiscovery.Root, "RadeonSoftwareSlimmer", "7-Zip", "7z.exe"),
        };
        // Elevated, 7z.dll beside it is checked too: 7z.exe loads it from its own folder.
        return candidates.FirstOrDefault(p => File.Exists(p) && (!Elevation.IsAdmin
            || (ToolDiscovery.OwnerProblem(p) is null && ToolDiscovery.OwnerProblem(Path.Combine(Path.GetDirectoryName(p)!, "7z.dll")) is null)));
    }

    /// <summary>The components of a package (its top-level folders), required ones first.</summary>
    public static async Task<List<NvidiaComponent>> ComponentsAsync(string package, CancellationToken ct)
    {
        var sevenZip = SevenZip() ?? throw new FileNotFoundException(Txt.SevenZipMissing);
        var r = await ProcessRunner.RunAsync(sevenZip, $"l -slt -ba {ProcessRunner.Quote(package)}", timeout: SevenZipTimeout, ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException(Txt.SevenZipFailed(r.ExitCode, r.Output));
        var list = ParseComponents(r.Output);
        if (!Required.All(f => list.Any(c => c.Folder.Equals(f, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException(Txt.NvidiaNotPackage);
        return list;
    }

    /// <summary>Top-level folders from "7z l -slt -ba": the first part of each "Path = folder\file" line.</summary>
    internal static List<NvidiaComponent> ParseComponents(string listing) => listing.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.StartsWith("Path = ", StringComparison.Ordinal))
        .Select(l => l[7..])
        .Where(p => p.Contains('\\'))
        .Select(p => p[..p.IndexOf('\\')])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(f => new NvidiaComponent(f, KnownComponents.GetValueOrDefault(f, f), Required.Contains(f, StringComparer.OrdinalIgnoreCase)))
        .OrderBy(c => !c.Required).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// setup.cfg lists the EULA, consent and privacy files as required; they live in folders that may have been left out, and
    /// then setup.exe stops. Removes those lines.
    /// </summary>
    internal static string StripSetupCfg(string cfg) => string.Join("\n", cfg.Split('\n')
        .Where(l => !Regex.IsMatch(l, @"name=""\$\{\{(EulaHtmlFile|FunctionalConsentFile|PrivacyPolicyFile)\}\}""", RegexOptions.IgnoreCase)));

    // ---------- install ----------

    /// <summary>Installs elevated (in this process when already administrator). Returns setup.exe's exit code (elevated: 0 or -1).</summary>
    public static async Task<int> InstallAsync(NvidiaInstallJob job, CancellationToken ct = default)
    {
        Validate(job);
        if (Elevation.IsAdmin) return await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        var code = await ElevatedRunner.RunAsync(new ElevatedJob { NvidiaInstall = job }, ct).ConfigureAwait(false);
        return code == ElevatedJob.ExitOk ? 0 : -1;
    }

    static readonly Regex FolderName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");

    internal static void Validate(NvidiaInstallJob job)
    {
        if (!DriverStore.LocalPath(job.Package) || !job.Package.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(job.Package))
            throw new InvalidOperationException($"Invalid driver package: {job.Package}");
        if (job.Components.Count != job.Components.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new InvalidOperationException("Repeated component.");
        foreach (var c in job.Components)
            if (!FolderName.IsMatch(c) || c.Contains("..", StringComparison.Ordinal) || Required.Contains(c, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid component: {c}");
        if (job.Tweaks.Count != job.Tweaks.Distinct(StringComparer.Ordinal).Count()) throw new InvalidOperationException("Repeated tweak.");
        foreach (var t in job.Tweaks)
            if (!PostInstallTweaks.Any(p => p.Id == t)) throw new InvalidOperationException($"Invalid post-install tweak: {t}");
    }

    /// <summary>
    /// Elevated. The package is copied into an administrators-only folder and checked there (NVIDIA's signature), so nothing
    /// can swap it between the check and setup; then extracted without the left-out components, setup.exe (also checked)
    /// runs silently, and the tweaks are applied. The extracted files are deleted at the end.
    /// </summary>
    internal static int Execute(NvidiaInstallJob job)
    {
        Validate(job);
        var sevenZip = SevenZip() ?? throw new FileNotFoundException(Txt.SevenZipMissing);
        var work = WorkFolder;
        GpuTuner.PrepareDirectory(Path.GetDirectoryName(work)!);
        GpuTuner.PrepareDirectory(work);
        // One install at a time: a second one would empty the folder this one's setup.exe runs from.
        FileStream gate;
        try { gate = new FileStream(work + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new IOException("Another NVIDIA driver install is running."); }
        using var _ = gate;
        Clear(work);
        Report(StepCopy, null);
        // Leftovers would be installed too (setup.exe installs every component folder it finds).
        if (Directory.EnumerateFileSystemEntries(work).Any()) throw new IOException($"{work} couldn't be emptied (another install running?); see the log.");
        var package = Path.Combine(work, "package.exe");
        var extracted = Path.Combine(work, "pkg");
        try
        {
            File.Copy(job.Package, package);
            if (Installer.Signer(package) != Publisher) throw new InvalidOperationException(Txt.NvidiaNotSigned(Path.GetFileName(job.Package)));

            var listing = ProcessRunner.Run(sevenZip, $"l -slt -ba {ProcessRunner.Quote(package)}", SevenZipTimeout);
            if (listing.ExitCode != 0) throw new InvalidOperationException(Txt.SevenZipFailed(listing.ExitCode, listing.Output));
            var components = ParseComponents(listing.Output);
            if (!Required.All(f => components.Any(c => c.Folder.Equals(f, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException(Txt.NvidiaNotPackage);
            foreach (var c in job.Components)
                if (!components.Any(x => x.Folder.Equals(c, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"The package has no component {c}.");
            var excluded = components.Where(c => !c.Required && !job.Components.Contains(c.Folder, StringComparer.OrdinalIgnoreCase)).Select(c => c.Folder).ToList();

            var args = $"x -y -bd -o{ProcessRunner.Quote(extracted)} {ProcessRunner.Quote(package)} " + string.Join(" ", excluded.Select(f => $"-x!{f}"));
            Log.Info($"NVIDIA driver: extracting without {(excluded.Count == 0 ? "nothing" : string.Join(", ", excluded))}.");
            var total = ExtractSize(listing.Output, excluded);
            var extracting = ProcessRunner.RunAsync(sevenZip, args, timeout: SevenZipTimeout);
            while (!extracting.Wait(500)) Report(StepExtract, total > 0 ? Math.Min(100, 100.0 * FolderSize(extracted) / total) : null);
            var x = extracting.GetAwaiter().GetResult();
            if (x.ExitCode != 0) throw new InvalidOperationException(Txt.SevenZipFailed(x.ExitCode, x.Output));
            File.Delete(package);

            var cfg = Path.Combine(extracted, "setup.cfg");
            if (File.Exists(cfg)) File.WriteAllText(cfg, StripSetupCfg(File.ReadAllText(cfg)));
            var setup = Path.Combine(extracted, "setup.exe");
            if (!File.Exists(setup) || Installer.Signer(setup) != Publisher) throw new InvalidOperationException(Txt.NvidiaNotSigned("setup.exe"));

            var setupArgs = "-s -noreboot -noeula" + (job.Clean ? " -clean" : "");
            Log.Info($"NVIDIA driver: {setup} {setupArgs}");
            Report(StepSetup, null);
            var r = ProcessRunner.RunAsync(setup, setupArgs, extracted, timeout: SetupTimeout).GetAwaiter().GetResult();
            Log.Info($"NVIDIA driver: setup.exe exited with {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")}.");
            if (r.TimedOut) return -1;
            Report(StepTweaks, null);
            ApplyTweaks(job.Tweaks);
            return r.ExitCode;
        }
        finally
        {
            Clear(work);
            try { File.Delete(ProgressFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Progress is only shown: a failed write never stops the install.</summary>
    static void Report(string step, double? percent)
    {
        try
        {
            var tmp = ProgressFile + ".tmp";
            File.WriteAllText(tmp, $"{step}|{percent?.ToString("F0", CultureInfo.InvariantCulture)}");
            File.Move(tmp, ProgressFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The running install's step, or null when none is running (or the file can't be read).</summary>
    public static NvidiaInstallProgress? ReadProgress()
    {
        try { return File.Exists(ProgressFile) ? ParseProgress(File.ReadAllText(ProgressFile)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static NvidiaInstallProgress? ParseProgress(string text)
    {
        var parts = text.Trim().Split('|');
        if (parts.Length != 2 || parts[0] is not (StepCopy or StepExtract or StepSetup or StepTweaks)) return null;
        return new(parts[0], double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 0, 100) : null);
    }

    /// <summary>Bytes 7-Zip will write: the files of "7z l -slt" outside the left-out top-level folders.</summary>
    internal static long ExtractSize(string listing, IReadOnlyCollection<string> excluded)
    {
        long total = 0;
        string? path = null;
        foreach (var line in listing.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("Path = ", StringComparison.Ordinal)) path = line[7..];
            else if (path is not null && line.StartsWith("Size = ", StringComparison.Ordinal) && long.TryParse(line[7..], out var size))
            {
                var top = path.Contains('\\') ? path[..path.IndexOf('\\')] : path;
                if (!excluded.Contains(top, StringComparer.OrdinalIgnoreCase)) total += size;
                path = null;
            }
        }
        return total;
    }

    static long FolderSize(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The tweaks' machine values, from a fresh read of the NVIDIA cards (setup may have given the card a new class key).</summary>
    static void ApplyTweaks(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var (_, option, _, _) = PostInstallTweaks.First(p => p.Id == id);
            var tweak = KosCatalog.FreshNvidiaTweak(id);
            if (tweak is null) { Log.Warn($"NVIDIA driver: tweak {id} not found."); continue; }
            foreach (var (setting, value) in ElevatedJob.MachineWrites(tweak, option))
            {
                try { setting.Write(value); Log.Info($"NVIDIA driver: {setting.Display} = {value ?? "(deleted)"}"); }
                catch (Exception ex) { Log.Warn($"NVIDIA driver: writing {setting.Display} failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>Empties the work folder (it is administrators-only; Directory.Delete removes links without following them).</summary>
    static void Clear(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir))
            try { Directory.Delete(d, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"NVIDIA driver: couldn't delete {d}: {ex.Message}"); }
        foreach (var f in Directory.EnumerateFiles(dir))
            try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"NVIDIA driver: couldn't delete {f}: {ex.Message}"); }
    }
}
