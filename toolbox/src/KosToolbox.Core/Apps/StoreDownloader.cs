using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Setup;
using Windows.Management.Deployment;

namespace KosToolbox.Core.Apps;

/// <summary>A package (app or framework) of a Store product, as the Store's delivery service lists it.</summary>
public sealed record StorePackage(string FullName, string Name, string Publisher, Version Version, string Arch, string Extension, long Size,
    string UpdateId, string Revision, string Sha1, string? Sha256, ulong MinOs)
{
    public bool Encrypted => Extension.StartsWith(".e", StringComparison.OrdinalIgnoreCase);
    public bool Bundle => Extension.EndsWith("bundle", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Store product and the packages to install, frameworks first and the app last.</summary>
public sealed record StoreProduct(string ProductId, string Title, string FamilyName, IReadOnlyList<StorePackage> Packages)
{
    public StorePackage Main => Packages[^1];
}

/// <summary>
/// Installs a Microsoft Store app without the Store app, its account or Windows Update: the product's packages come from the same
/// delivery service the Store uses (the catalog gives its Windows Update category, then GetCookie, SyncUpdates and
/// GetExtendedUpdateInfo2 give the packages and their links), are checked against the SHA-256 the service lists, and are installed
/// with the Windows PackageManager, which also checks their signature. Needs AppXSvc and ClipSVC; free apps only (paid and
/// protected apps come encrypted and need the Store's license).
/// </summary>
/// <summary>A free app found by name in the Store's search.</summary>
public sealed record StoreSearchResult(string ProductId, string Title, string Publisher);

public static class StoreDownloader
{
    const string Service = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
    const string Ws = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

    /// <summary>
    /// Windows' own "installed" markers (detectoids) the service needs to see before it offers app packages: without them the
    /// answer is empty. The ids every Windows 10/11 desktop reports.
    /// </summary>
    static readonly int[] InstalledNonLeaf =
    {
        1, 2, 3, 11, 19, 544, 549, 2359974, 2359977, 5169044, 8788830, 23110993, 23110994, 54341900, 54343656, 59830006, 59830007, 59830008,
        60484010, 62450018, 62450019, 62450020, 66027979, 66053150, 97657898, 98822896, 98959022, 98959023, 98959024, 98959025, 98959026,
        104433538, 104900364, 105489019, 117765322, 129905029, 130040031, 132387090, 132393049, 133399034, 138537048, 140377312, 143747671,
        158941041, 158941042, 158941043, 158941044, 159123858, 159130928, 164836897, 164847386, 164848327, 164852241, 164852246, 164852252, 164852253,
    };

    const string DeviceAttributes = "BranchReadinessLevel=CB;CurrentBranch=ge_release;FlightRing=Retail;AttrDataVer=21;InstallLanguage=en-US;OSUILocale=en-US;" +
                                    "InstallationType=Client;App=WU;AppVer=10.0.26100.1;OSArchitecture=AMD64;IsFlightingEnabled=0;TelemetryLevel=0;OSVersion=10.0.26100.1;DeviceFamily=Windows.Desktop;";

    /// <summary>The product id in a Store link or typed alone: 9 and 11 more characters (package apps) or XP and 12 more (apps with their own installer).</summary>
    public static string? ProductId(string text) =>
        Regex.Match(text ?? "", @"(?<![A-Za-z0-9])(9[A-Za-z0-9]{11}|[Xx][Pp][A-Za-z0-9]{12})(?![A-Za-z0-9])") is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    /// <summary>The AppX services are disabled (Windows › System › App installs and licenses): nothing can install.</summary>
    public static bool ServicesDisabled => new[] { "AppXSvc", "ClipSVC" }.Any(s => RegistrySetting.ServiceStart(s).Read() == "4");

    /// <summary>Searches the Store by name, as the Store app does (desktop apps, US catalog); only free apps, since paid ones can't be installed here.</summary>
    public static async Task<IReadOnlyList<StoreSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/search?market=US&locale=en-US&deviceFamily=windows.desktop&query={Uri.EscapeDataString(query.Trim())}";
        using var response = await Downloader.Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseSearch(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    internal static List<StoreSearchResult> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<StoreSearchResult>();
        if (!doc.RootElement.TryGetProperty("Payload", out var payload) || !payload.TryGetProperty("SearchResults", out var results)
            || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in results.EnumerateArray())
        {
            var id = r.TryGetProperty("ProductId", out var p) ? ProductId(p.GetString() ?? "") : null;
            var free = !r.TryGetProperty("Price", out var price) || price.ValueKind != JsonValueKind.Number || price.GetDouble() == 0;
            if (id is null || !free || list.Exists(x => x.ProductId == id)) continue;
            list.Add(new StoreSearchResult(id, r.TryGetProperty("Title", out var t) ? t.GetString() ?? id : id,
                r.TryGetProperty("PublisherName", out var pub) ? pub.GetString() ?? "" : ""));
        }
        return list;
    }

    public static async Task<StoreProduct> FindAsync(string productId, CancellationToken ct)
    {
        if (ProductId(productId) != productId) throw new ArgumentException($"Not a Store product id: {productId}");
        var http = Downloader.Http;
        string title, category, family;
        using (var response = await http.GetAsync($"https://displaycatalog.mp.microsoft.com/v7.0/products/{productId}?market=US&languages=en-US,neutral", ct).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"{productId} isn't in the Microsoft Store catalog.");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var product = doc.RootElement.GetProperty("Product");
            title = product.GetProperty("LocalizedProperties")[0].GetProperty("ProductTitle").GetString() ?? productId;
            var data = product.GetProperty("DisplaySkuAvailabilities")[0].GetProperty("Sku").GetProperty("Properties").TryGetProperty("FulfillmentData", out var f) ? f : default;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("WuCategoryId", out var c) || c.GetString() is not { Length: > 0 } wu
                || !data.TryGetProperty("PackageFamilyName", out var pf) || pf.GetString() is not { Length: > 0 } pfn)
                throw new InvalidOperationException($"{title} installs with its own installer, not as a package: open it in the Store or download it from its site.");
            category = wu;
            family = pfn;
        }
        var cookie = Regex.Match(await PostAsync(Service, GetCookie(), ct).ConfigureAwait(false), "<EncryptedData>([^<]+)</EncryptedData>").Groups[1].Value;
        if (cookie.Length == 0) throw new InvalidOperationException("The Store's delivery service didn't answer as expected.");
        var all = Parse(await PostAsync(Service, SyncUpdates(cookie, category), ct).ConfigureAwait(false));
        var packages = Choose(all, family, Environment.OSVersion.Version.Build, Arch, InstalledVersion);
        Log.Info($"Store: {title} ({productId}, {family}): {string.Join(", ", packages.Select(p => p.FullName))}");
        return new StoreProduct(productId, title, family, packages);
    }

    /// <summary>Downloads one package and checks it against the hash the service listed; returns the file.</summary>
    public static async Task<string> DownloadAsync(StorePackage p, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var answer = XDocument.Parse(await PostAsync(Service + "/secured", FileUrl(p.UpdateId, p.Revision), ct).ConfigureAwait(false));
        var url = answer.Descendants().Where(e => e.Name.LocalName == "FileLocation")
                      .FirstOrDefault(l => l.Elements().FirstOrDefault(e => e.Name.LocalName == "FileDigest")?.Value == p.Sha1)
                      ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Url")?.Value
                  ?? throw new InvalidOperationException($"The service gave no link for {p.FullName}.");
        var (file, _) = await Downloader.DownloadAsync(url, p.FullName + p.Extension, Downloader.Folder, progress, ct).ConfigureAwait(false);
        // The link is plain HTTP: the hash came over HTTPS, so a changed file is caught here (and the package signature at install).
        await using (var stream = File.OpenRead(file))
        {
            var ok = p.Sha256 is { } sha256
                ? Convert.ToBase64String(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) == sha256
                : Convert.ToBase64String(SHA1.HashData(stream)) == p.Sha1;
            if (!ok)
            {
                stream.Close();
                File.Delete(file);
                throw new InvalidOperationException($"{p.FullName} didn't match the hash the Store listed; it was deleted.");
            }
        }
        return file;
    }

    /// <summary>
    /// Downloads the app, reads which frameworks its manifest asks for, downloads and installs those that are missing or older, then
    /// installs the app; the downloaded files are deleted at the end. Returns the packages installed.
    /// </summary>
    public static async Task<IReadOnlyList<StorePackage>> InstallAsync(StoreProduct product, IProgress<string> step, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var files = new List<string>();
        try
        {
            step.Report($"Downloading {product.Main.Name}");
            var app = await DownloadAsync(product.Main, progress, ct).ConfigureAwait(false);
            files.Add(app);
            var offered = product.Packages.Take(product.Packages.Count - 1).ToList();
            var frameworks = Needed(offered, Dependencies(app, Arch), InstalledVersion);
            var downloaded = new List<string>();
            foreach (var f in frameworks)
            {
                step.Report($"Downloading {f.Name}");
                var file = await DownloadAsync(f, progress, ct).ConfigureAwait(false);
                files.Add(file);
                downloaded.Add(file);
            }
            for (var i = 0; i < frameworks.Count; i++)
            {
                step.Report($"Installing {frameworks[i].Name}");
                await AddAsync(frameworks[i], downloaded[i], framework: true, ct).ConfigureAwait(false);
            }
            step.Report($"Installing {product.Title}");
            await AddAsync(product.Main, app, framework: false, ct).ConfigureAwait(false);
            return frameworks.Append(product.Main).ToList();
        }
        finally
        {
            foreach (var f in files)
                try { File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Store: {f} not deleted: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The offered frameworks the app asks for (by name) and that aren't installed in at least the version it asks for. When the manifest
    /// couldn't be read (null), every offered framework is installed, as the Store's list already leaves out the installed ones.
    /// </summary>
    internal static List<StorePackage> Needed(IReadOnlyList<StorePackage> offered, IReadOnlyList<(string Name, Version Min)>? dependencies, Func<string, string, Version?> installed) =>
        dependencies is null ? offered.ToList()
            : offered.Where(f => dependencies.Any(d => d.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)
                                                       && (installed(f.Name, f.Publisher) is not { } v || v < d.Min))).ToList();

    /// <summary>
    /// The PackageDependency entries of the app's manifest: of the package itself, or, in a bundle, of its package for this architecture
    /// (or the neutral one). Null when the file isn't laid out as expected.
    /// </summary>
    internal static List<(string Name, Version Min)>? Dependencies(string file, string arch)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(file);
            if (zip.GetEntry("AppxMetadata/AppxBundleManifest.xml") is { } bundle)
            {
                XDocument manifest;
                using (var s = bundle.Open()) manifest = XDocument.Load(s);
                var packages = manifest.Descendants().Where(e => e.Name.LocalName == "Package" && (string?)e.Attribute("Type") == "application").ToList();
                var inner = packages.FirstOrDefault(e => string.Equals((string?)e.Attribute("Architecture"), arch, StringComparison.OrdinalIgnoreCase))
                            ?? packages.FirstOrDefault(e => string.Equals((string?)e.Attribute("Architecture"), "neutral", StringComparison.OrdinalIgnoreCase));
                // A large app package (games) isn't copied into memory: every offered framework is installed instead.
                if (inner is null || zip.GetEntry((string?)inner.Attribute("FileName") ?? "") is not { Length: < 256L << 20 } entry) return null;
                using var copy = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(copy);
                copy.Position = 0;
                using var package = new System.IO.Compression.ZipArchive(copy);
                return Read(package);
            }
            return Read(zip);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
        {
            Log.Warn($"Store: the manifest of {file} couldn't be read ({ex.Message}); installing every offered framework.");
            return null;
        }

        static List<(string, Version)>? Read(System.IO.Compression.ZipArchive package)
        {
            if (package.GetEntry("AppxManifest.xml") is not { } entry) return null;
            using var s = entry.Open();
            return XDocument.Load(s).Descendants().Where(e => e.Name.LocalName == "PackageDependency")
                .Select(e => ((string?)e.Attribute("Name") ?? "", Version.TryParse((string?)e.Attribute("MinVersion"), out var v) ? v : new Version(0, 0)))
                .Where(d => d.Item1.Length > 0).ToList();
        }
    }

    /// <summary>Installs a downloaded package for the current user. A framework already installed in a newer version counts as done.</summary>
    static async Task AddAsync(StorePackage p, string file, bool framework, CancellationToken ct)
    {
        var op = new PackageManager().AddPackageAsync(new Uri(file), null, DeploymentOptions.None);
        using (ct.Register(op.Cancel))
            // Polled, not awaited: once the deployment finished (AppX log) but its completion never reached the awaiting task.
            while (op.Status == Windows.Foundation.AsyncStatus.Started) await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        if (op.Status == Windows.Foundation.AsyncStatus.Canceled) throw new OperationCanceledException(ct);
        DeploymentResult? result = null;
        try { result = op.GetResults(); }
        catch (Exception) when (op.Status == Windows.Foundation.AsyncStatus.Error) { } // the error is in ErrorCode then
        var error = result?.ExtendedErrorCode ?? (op.Status == Windows.Foundation.AsyncStatus.Error ? op.ErrorCode : null);
        if (error is not null && !(framework && error.HResult == HigherVersionInstalled))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result?.ErrorText) ? error.Message : result.ErrorText.Trim());
        Log.Info($"Store: installed {p.FullName}");
    }

    const int HigherVersionInstalled = unchecked((int)0x80073D06);

    static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Highest installed version of a package for this user, in this PC's architecture or neutral; null = not installed.</summary>
    static Version? InstalledVersion(string name, string publisher)
    {
        try
        {
            return new PackageManager().FindPackagesForUser("", $"{name}_{publisher}")
                .Where(p => p.Id.Architecture is Windows.System.ProcessorArchitecture.X64 or Windows.System.ProcessorArchitecture.Arm64 or Windows.System.ProcessorArchitecture.Neutral)
                .Select(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision)).DefaultIfEmpty().Max();
        }
        catch (COMException) { return null; }
    }

    /// <summary>
    /// The newest app package for this PC (its architecture or neutral, not encrypted, for this Windows build) and the newest of each
    /// framework the service offers with it that isn't installed in that version yet.
    /// </summary>
    internal static List<StorePackage> Choose(IReadOnlyList<StorePackage> all, string family, int build, string arch, Func<string, string, Version?> installed)
    {
        var i = family.LastIndexOf('_');
        var (name, publisher) = (family[..i], family[(i + 1)..]);
        var os = (10UL << 48) | ((ulong)build << 16) | 0xFFFF;
        bool IsApp(StorePackage p) => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Publisher.Equals(publisher, StringComparison.OrdinalIgnoreCase);
        var usable = all.Where(p => !p.Encrypted && (p.Arch == arch || p.Arch == "neutral") && p.MinOs <= os).ToList();
        var app = usable.Where(IsApp).OrderByDescending(p => p.Version).ThenByDescending(p => p.Bundle).FirstOrDefault()
                  ?? throw new InvalidOperationException(all.Any(p => IsApp(p) && p.Encrypted)
                      ? "This app only comes encrypted (paid or protected): install it from the Store app."
                      : "The Store has no package of this app for this PC.");
        var frameworks = usable.Where(p => !IsApp(p)).GroupBy(p => (p.Name.ToLowerInvariant(), p.Publisher.ToLowerInvariant()))
            .Select(g => g.MaxBy(p => p.Version)!)
            .Where(p => installed(p.Name, p.Publisher) is not { } v || v < p.Version)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return frameworks.Append(app).ToList();
    }

    /// <summary>Joins the updates that have a download (SecuredFragment) with their files, by the update's number.</summary>
    internal static List<StorePackage> Parse(string response)
    {
        var doc = XDocument.Parse(response);
        IEnumerable<XElement> Named(XContainer c, string local) => c.Descendants().Where(e => e.Name.LocalName == local);
        XElement Inner(XElement update) => XElement.Parse("<x>" + update.Elements().First(e => e.Name.LocalName == "Xml").Value + "</x>");
        var files = new Dictionary<string, XElement>();
        foreach (var u in Named(doc, "ExtendedUpdateInfo").SelectMany(e => Named(e, "Update")))
            if (Inner(u).Descendants("File").FirstOrDefault(f => f.Attribute("InstallerSpecificIdentifier") is not null) is { } file)
                files[u.Elements().First(e => e.Name.LocalName == "ID").Value] = file;
        var list = new List<StorePackage>();
        foreach (var info in Named(doc, "NewUpdates").SelectMany(e => Named(e, "UpdateInfo")))
        {
            var id = info.Elements().First(e => e.Name.LocalName == "ID").Value;
            if (!files.TryGetValue(id, out var file)) continue;
            var xml = Inner(info);
            if (xml.Descendants("SecuredFragment").FirstOrDefault() is null || xml.Element("UpdateIdentity") is not { } identity) continue;
            var fullName = (string)file.Attribute("InstallerSpecificIdentifier")!;
            if (!Regex.IsMatch(fullName, @"^[A-Za-z0-9.\-~_]{1,200}$")) continue; // a package full name; it also names the downloaded file
            var parts = fullName.Split('_');
            if (parts.Length != 5 || !Version.TryParse(parts[1], out var version)) continue;
            list.Add(new StorePackage(fullName, parts[0], parts[4], version, parts[2].ToLowerInvariant(), Path.GetExtension((string?)file.Attribute("FileName") ?? ""),
                (long?)file.Attribute("Size") ?? 0, (string)identity.Attribute("UpdateID")!, (string?)identity.Attribute("RevisionNumber") ?? "1",
                (string?)file.Attribute("Digest") ?? "", file.Elements("AdditionalDigest").FirstOrDefault(d => (string?)d.Attribute("Algorithm") == "SHA256")?.Value,
                MinOs(xml.Descendants("ApplicabilityBlob").FirstOrDefault()?.Value)));
        }
        return list;
    }

    /// <summary>The lowest Windows version the package runs on (major.minor.build.revision packed in 16-bit fields); 0 = any.</summary>
    static ulong MinOs(string? blob)
    {
        if (blob is null) return 0;
        try
        {
            using var doc = JsonDocument.Parse(blob);
            return doc.RootElement.TryGetProperty("content.targetPlatforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array
                ? platforms.EnumerateArray().Select(t => t.TryGetProperty("platform.minVersion", out var v) && v.TryGetUInt64(out var n) ? n : 0).DefaultIfEmpty(0UL).Min()
                : 0;
        }
        catch (JsonException) { return 0; }
    }

    static async Task<string> PostAsync(string url, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/soap+xml");
        using var response = await Downloader.Http.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // ---------- requests (Windows Update's client protocol, MS-WUSP, as the Store client sends it) ----------

    static string Header(string action, string to)
    {
        var now = DateTime.UtcNow;
        return $"""
            <s:Header>
              <a:Action s:mustUnderstand="1">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/{action}</a:Action>
              <a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID>
              <a:To s:mustUnderstand="1">{to}</a:To>
              <o:Security s:mustUnderstand="1" xmlns:o="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
                <Timestamp xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"><Created>{now:yyyy-MM-ddTHH:mm:ss.fffZ}</Created><Expires>{now.AddMinutes(5):yyyy-MM-ddTHH:mm:ss.fffZ}</Expires></Timestamp>
                <wuws:WindowsUpdateTicketsToken wsu:id="ClientMSA" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd" xmlns:wuws="http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization">
                  <TicketType Name="MSA" Version="1.0" Policy="MBI_SSL"><User /></TicketType>
                </wuws:WindowsUpdateTicketsToken>
              </o:Security>
            </s:Header>
            """;
    }

    static string Envelope(string header, string body) =>
        $"""<s:Envelope xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:s="http://www.w3.org/2003/05/soap-envelope">{header}<s:Body>{body}</s:Body></s:Envelope>""";

    static string GetCookie() => Envelope(Header("GetCookie", Service),
        $"""<GetCookie xmlns="{Ws}"><oldCookie></oldCookie><lastChange>2015-10-21T17:01:07.1472913Z</lastChange><currentTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}</currentTime><protocolVersion>1.40</protocolVersion></GetCookie>""");

    internal static string SyncUpdates(string cookie, string category) => Envelope(Header("SyncUpdates", Service), $"""
        <SyncUpdates xmlns="{Ws}">
          <cookie><Expiration>2045-03-11T02:02:48Z</Expiration><EncryptedData>{SecurityElement.Escape(cookie)}</EncryptedData></cookie>
          <parameters>
            <ExpressQuery>false</ExpressQuery>
            <InstalledNonLeafUpdateIDs>{string.Concat(InstalledNonLeaf.Select(i => $"<int>{i}</int>"))}</InstalledNonLeafUpdateIDs>
            <OtherCachedUpdateIDs />
            <SkipSoftwareSync>false</SkipSoftwareSync>
            <NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates>
            <FilterAppCategoryIds><CategoryIdentifier><Id>{SecurityElement.Escape(category)}</Id></CategoryIdentifier></FilterAppCategoryIds>
            <TreatAppCategoryIdsAsInstalled>true</TreatAppCategoryIdsAsInstalled>
            <AlsoPerformRegularSync>false</AlsoPerformRegularSync>
            <ComputerSpec />
            <ExtendedUpdateInfoParameters>
              <XmlUpdateFragmentTypes><XmlUpdateFragmentType>Extended</XmlUpdateFragmentType></XmlUpdateFragmentTypes>
              <Locales><string>en-US</string><string>en</string></Locales>
            </ExtendedUpdateInfoParameters>
            <ClientPreferredLanguages><string>en-US</string></ClientPreferredLanguages>
            <ProductsParameters>
              <SyncCurrentVersionOnly>false</SyncCurrentVersionOnly>
              <DeviceAttributes>{DeviceAttributes}</DeviceAttributes>
              <CallerAttributes>Interactive=1;IsSeeker=0;</CallerAttributes>
              <Products />
            </ProductsParameters>
          </parameters>
        </SyncUpdates>
        """);

    static string FileUrl(string updateId, string revision) => Envelope(Header("GetExtendedUpdateInfo2", Service + "/secured"), $"""
        <GetExtendedUpdateInfo2 xmlns="{Ws}">
          <updateIDs><UpdateIdentity><UpdateID>{SecurityElement.Escape(updateId)}</UpdateID><RevisionNumber>{SecurityElement.Escape(revision)}</RevisionNumber></UpdateIdentity></updateIDs>
          <infoTypes><XmlUpdateFragmentType>FileUrl</XmlUpdateFragmentType><XmlUpdateFragmentType>FileDecryption</XmlUpdateFragmentType></infoTypes>
          <deviceAttributes>{DeviceAttributes}</deviceAttributes>
        </GetExtendedUpdateInfo2>
        """);
}
