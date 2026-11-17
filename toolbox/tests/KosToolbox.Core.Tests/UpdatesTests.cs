using System.Security;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Updates;
using Xunit;

namespace KosToolbox.Core.Tests;

public class StoreDownloaderTests
{
    const string Ws = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

    /// <summary>An update with a download (SecuredFragment) and its package file, shaped like the service's SyncUpdates answer.</summary>
    static (string Info, string Update) Package(int id, string fullName, string ext, bool secured = true, ulong minOs = 2814750438211584 /* 10.0.10240.0 */)
    {
        var info = $"""<UpdateIdentity UpdateID="{Guid.NewGuid()}" RevisionNumber="1" /><Properties UpdateType="Software">{(secured ? "<SecuredFragment>FileUrl</SecuredFragment>" : "")}</Properties><ApplicabilityRules><Metadata><AppxPackageMetadata><AppxMetadata PackageType="UAP" PackageMoniker="{fullName}"><ApplicabilityBlob>{"{"}"content.targetPlatforms":[{"{"}"platform.minVersion":{minOs},"platform.target":0{"}"}]{"}"}</ApplicabilityBlob></AppxMetadata></AppxPackageMetadata></Metadata></ApplicabilityRules>""";
        var update = $"""<ExtendedProperties ContentType="Application" /><Files><File FileName="{Guid.NewGuid()}{ext}" Digest="c2hhMQ==" DigestAlgorithm="SHA1" Size="1000" InstallerSpecificIdentifier="{fullName}"><AdditionalDigest Algorithm="SHA256">c2hhMjU2</AdditionalDigest></File><File FileName="Abm_x.cab" Digest="YmxvY2s=" DigestAlgorithm="SHA1" Size="10" PatchingType="DynamicMetadata" /></Files>""";
        return ($"<UpdateInfo><ID>{id}</ID><Deployment><ID>9</ID></Deployment><Xml>{SecurityElement.Escape(info)}</Xml></UpdateInfo>",
                $"<Update><ID>{id}</ID><Xml>{SecurityElement.Escape(update)}</Xml></Update>");
    }

    static string Response(params (string Info, string Update)[] packages) =>
        $"""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><SyncUpdatesResponse xmlns="{Ws}"><SyncUpdatesResult><NewUpdates>{string.Concat(packages.Select(p => p.Info))}</NewUpdates><ExtendedUpdateInfo><Updates>{string.Concat(packages.Select(p => p.Update))}</Updates></ExtendedUpdateInfo></SyncUpdatesResult></SyncUpdatesResponse></s:Body></s:Envelope>""";

    [Fact]
    public void SearchKeepsFreeAppsOnce()
    {
        var json = """
            {"Payload":{"SearchResults":[
              {"ProductId":"9NKSQGP7F2NH","Title":"WhatsApp","PublisherName":"WhatsApp Inc.","Price":0.0},
              {"ProductId":"9NKSQGP7F2NH","Title":"WhatsApp","PublisherName":"WhatsApp Inc.","Price":0.0},
              {"ProductId":"XP89119P9F2PCQ","Title":"Signal Private Messenger","Price":0.0},
              {"ProductId":"9PAIDAPP0001","Title":"Paid","PublisherName":"X","Price":4.99},
              {"ProductId":"not-an-id","Title":"Bad"}]}}
            """;
        var results = StoreDownloader.ParseSearch(json);
        Assert.Equal(new[] { "9NKSQGP7F2NH", "XP89119P9F2PCQ" }, results.Select(r => r.ProductId));
        Assert.Equal(("WhatsApp", "WhatsApp Inc."), (results[0].Title, results[0].Publisher));
        Assert.Equal("", results[1].Publisher);
        Assert.Empty(StoreDownloader.ParseSearch("""{"Payload":{}}"""));
    }

    [Fact]
    public void ParsesPackagesWithTheirFilesAndHashes()
    {
        var list = StoreDownloader.Parse(Response(
            Package(1, "Microsoft.WindowsCalculator_2021.2607.0.0_neutral_~_8wekyb3d8bbwe", ".msixbundle"),
            Package(2, "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(3, "Microsoft.VCLibs.140.00_14.0.30000.0_x64__8wekyb3d8bbwe", ".appx", secured: false)));
        Assert.Equal(2, list.Count); // the update without a download is left out
        var app = list[0];
        Assert.Equal(("Microsoft.WindowsCalculator", "8wekyb3d8bbwe", "neutral", ".msixbundle"), (app.Name, app.Publisher, app.Arch, app.Extension));
        Assert.Equal(new Version(2021, 2607, 0, 0), app.Version);
        Assert.Equal(("c2hhMQ==", "c2hhMjU2", 1000L), (app.Sha1, app.Sha256, app.Size)); // the package file, not the block map
        Assert.True(app.Bundle);
        Assert.Equal(2814750438211584UL, app.MinOs);
    }

    [Fact]
    public void ChoosesTheNewestUsableAppAndMissingFrameworks()
    {
        var all = StoreDownloader.Parse(Response(
            Package(1, "Contoso.App_1.0.0.0_neutral_~_pub", ".msixbundle"),
            Package(2, "Contoso.App_2.0.0.0_neutral_~_pub", ".msixbundle"),
            Package(3, "Contoso.App_3.0.0.0_neutral_~_pub", ".emsixbundle"),                                   // encrypted
            Package(4, "Contoso.App_4.0.0.0_neutral_~_pub", ".msixbundle", minOs: (10UL << 48) | (99999UL << 16)), // needs a newer Windows
            Package(5, "Contoso.App_5.0.0.0_arm64__pub", ".msix"),                                             // other architecture
            Package(6, "Microsoft.VCLibs.140.00_14.0.1.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(7, "Microsoft.VCLibs.140.00_14.0.2.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(8, "Microsoft.UI.Xaml.2.8_8.1.0.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(9, "Microsoft.NET.Native.Runtime.2.2_2.2.1.0_x86__8wekyb3d8bbwe", ".appx")));
        Version? Installed(string name, string publisher) => name == "Microsoft.UI.Xaml.2.8" ? new Version(8, 1, 0, 0) : null;
        var chosen = StoreDownloader.Choose(all, "Contoso.App_pub", 26100, "x64", Installed);
        Assert.Equal(new[] { "Microsoft.VCLibs.140.00_14.0.2.0_x64__8wekyb3d8bbwe", "Contoso.App_2.0.0.0_neutral_~_pub" }, chosen.Select(p => p.FullName));
    }

    [Fact]
    public void EncryptedOnlyAppIsExplained()
    {
        var all = StoreDownloader.Parse(Response(Package(1, "Contoso.Game_1.0.0.0_x64__pub", ".eappx")));
        var ex = Assert.Throws<InvalidOperationException>(() => StoreDownloader.Choose(all, "Contoso.Game_pub", 26100, "x64", (_, _) => null));
        Assert.Contains("encrypted", ex.Message);
    }

    static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
                using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return ms.ToArray();
    }

    static byte[] Manifest(params string[] dependencies) => System.Text.Encoding.UTF8.GetBytes(
        $"""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Dependencies><TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.17763.0" />{string.Concat(dependencies.Select(d => $"<PackageDependency Name=\"{d.Split('@')[0]}\" MinVersion=\"{d.Split('@')[1]}\" Publisher=\"CN=Microsoft\" />"))}</Dependencies></Package>""");

    [Fact]
    public void ReadsTheDependenciesOfTheBundlePackageForThisArchitecture()
    {
        var bundle = Zip(
            ("AppxMetadata/AppxBundleManifest.xml", System.Text.Encoding.UTF8.GetBytes(
                """<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle"><Packages><Package Type="application" Architecture="arm64" FileName="App_arm64.msix" /><Package Type="application" Architecture="x64" FileName="App_x64.msix" /><Package Type="resource" FileName="App_scale-200.msix" /></Packages></Bundle>""")),
            ("App_arm64.msix", Zip(("AppxManifest.xml", Manifest("Arm.Only@1.0.0.0")))),
            ("App_x64.msix", Zip(("AppxManifest.xml", Manifest("Microsoft.UI.Xaml.2.8@8.2310.30001.0", "Microsoft.VCLibs.140.00@14.0.33519.0")))));
        var file = Path.Combine(Path.GetTempPath(), $"kos-test-{Guid.NewGuid():N}.msixbundle");
        File.WriteAllBytes(file, bundle);
        try
        {
            var deps = StoreDownloader.Dependencies(file, "x64")!;
            Assert.Equal(new[] { ("Microsoft.UI.Xaml.2.8", new Version(8, 2310, 30001, 0)), ("Microsoft.VCLibs.140.00", new Version(14, 0, 33519, 0)) }, deps);
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            Assert.Null(StoreDownloader.Dependencies(file, "x64")); // not a package: every offered framework is installed instead
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void InstallsOnlyTheFrameworksTheAppNeedsAndLacks()
    {
        var offered = StoreDownloader.Parse(Response(
            Package(1, "Microsoft.UI.Xaml.2.2_2.21909.0.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(2, "Microsoft.UI.Xaml.2.8_8.2501.31001.0_x64__8wekyb3d8bbwe", ".appx"),
            Package(3, "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", ".appx")));
        var deps = new List<(string, Version)> { ("Microsoft.UI.Xaml.2.8", new Version(8, 2310, 0, 0)), ("Microsoft.VCLibs.140.00", new Version(14, 0, 30000, 0)) };
        Version? Installed(string name, string _) => name == "Microsoft.VCLibs.140.00" ? new Version(14, 0, 30035, 0) : null; // newer than asked
        Assert.Equal(new[] { "Microsoft.UI.Xaml.2.8" }, StoreDownloader.Needed(offered, deps, Installed).Select(p => p.Name));
        Assert.Equal(3, StoreDownloader.Needed(offered, null, Installed).Count);
    }

    [Theory]
    [InlineData("https://apps.microsoft.com/detail/9wzdncrfhvn5?hl=en-US&gl=US", "9WZDNCRFHVN5")]
    [InlineData("  9P1TBXR6QDCX ", "9P1TBXR6QDCX")]
    [InlineData("ms-windows-store://pdp/?ProductId=XP89DCGQ3K6VLD", "XP89DCGQ3K6VLD")]
    [InlineData("calculator", null)]
    [InlineData("9WZDNCRFHVN5X", null)]
    public void ReadsTheProductId(string text, string? id) => Assert.Equal(id, StoreDownloader.ProductId(text));

    [Fact]
    public void SyncRequestIsWellFormed()
    {
        var xml = System.Xml.Linq.XDocument.Parse(StoreDownloader.SyncUpdates("cookie<&>", "e632fe90-a5ba-43aa-bf81-9de8413e83d8"));
        Assert.Equal("cookie<&>", xml.Descendants().First(e => e.Name.LocalName == "EncryptedData").Value);
        Assert.Equal(56, xml.Descendants().Count(e => e.Name.LocalName == "int"));
    }

    sealed class Steps : IProgress<string>, IProgress<KosToolbox.Core.Setup.DownloadProgress>
    {
        public readonly List<string> Seen = new();
        public void Report(string value) { lock (Seen) Seen.Add(value); }
        public void Report(KosToolbox.Core.Setup.DownloadProgress value) { }
    }

    /// <summary>
    /// Downloads and installs a Store product for this user (KOS_TEST_STORE_INSTALL=its id; use an app that is already installed, so
    /// at most it updates). The files must be gone afterwards and the package installed.
    /// </summary>
    [Fact]
    public async Task InstallsLive()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_STORE_INSTALL") is not { Length: > 0 } id) return;
        var p = await StoreDownloader.FindAsync(id, CancellationToken.None);
        var steps = new Steps();
        var installed = await StoreDownloader.InstallAsync(p, steps, steps, CancellationToken.None);
        Assert.Equal(p.Main, installed[^1]);
        Assert.Contains(new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", p.FamilyName),
            pkg => new Version(pkg.Id.Version.Major, pkg.Id.Version.Minor, pkg.Id.Version.Build, pkg.Id.Version.Revision) >= p.Main.Version
                   || p.Main.Bundle); // a bundle's version isn't the installed package's
        Assert.Empty(Directory.EnumerateFiles(KosToolbox.Core.Setup.Downloader.Folder, p.Main.FullName + "*"));
    }

    /// <summary>The whole lookup against the real Store service (no download). Set KOS_TEST_STORE=1 to run it.</summary>
    [Fact]
    public async Task FindsCalculatorLive()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_STORE") != "1") return;
        var p = await StoreDownloader.FindAsync("9WZDNCRFHVN5", CancellationToken.None);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", p.FamilyName);
        Assert.Equal("Microsoft.WindowsCalculator", p.Main.Name);
        Assert.False(p.Main.Encrypted);
        Assert.NotNull(p.Main.Sha256);
    }
}

public class AppUpdatesTests
{
    [Theory]
    [InlineData("v1.7.2", "1.7.1.0", true)]
    [InlineData("1.7.2", "1.7.2.0", false)]               // padded installed version
    [InlineData("131.0.6778.85-1.1", "131.0.6778.85", false)] // build suffix on the release tag
    [InlineData("M138.0.7204.303", "138.0.7204.100", true)]
    [InlineData("2.0.0", "10.0.0", false)]
    [InlineData("release_1.8.0", "1.7.9", true)]
    public void ComparesVersions(string available, string installed, bool newer) => Assert.Equal(newer, SetupCatalog.IsNewer(available, installed));

    [Fact]
    public void ReadsChocoOutdated()
    {
        var output = "Chocolatey v2.4.3\nOutdated Packages\n Output is package name | current version | available version | pinned?\n\n" +
                     "googlechrome|131.0.6778.86|131.0.6778.109|false\r\nfirefox|133.0|133.0.3|true\nnot a line\nchocolatey has determined 2 package(s) are outdated.\n";
        var list = Chocolatey.ParseOutdated(output);
        Assert.Equal(new[] { ("googlechrome", "131.0.6778.86", "131.0.6778.109", false), ("firefox", "133.0", "133.0.3", true) }, list);
    }

    [Fact]
    public void EveryCheckedDownloadHasAVersionSource()
    {
        // The programs that download from GitHub or winget can be compared; the fixed links (Discord, Riot) can't.
        Assert.NotNull(SetupCatalog.Find("vesktop")!.Latest);
        Assert.NotNull(SetupCatalog.Find("afterburner")!.Latest);
        Assert.NotNull(SetupCatalog.Find("thorium")!.Latest);
        Assert.Null(SetupCatalog.Find("riot")!.Latest);
    }
}

public class WindowsUpdatesTests
{
    [Fact]
    public void DescribesKnownErrors()
    {
        Assert.Contains("policy", WindowsUpdates.Describe(unchecked((int)0x8024002E)));
        Assert.Contains("0x80240FFF", WindowsUpdates.Describe(unchecked((int)0x80240FFF)));
    }

    /// <summary>
    /// A real search (administrator; reads only, but lets Windows Update run for it). Every service and policy value must read the
    /// same afterwards. Set KOS_TEST_WU=1 to run it.
    /// </summary>
    [Fact]
    public async Task SearchesLiveAndLeavesValuesAsTheyWere()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_WU") != "1") return;
        string?[] Values() => new[] { "wuauserv", "bits", "dosvc" }.Select(s => Settings.RegistrySetting.ServiceStart(s).Read())
            .Concat(new[] { "DisableWindowsUpdateAccess", "DoNotConnectToWindowsUpdateInternetLocations" }
                .Select(n => new Settings.RegistrySetting(Settings.Hive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", n, Microsoft.Win32.RegistryValueKind.DWord).Read()))
            .ToArray();
        var before = Values();
        var found = await WindowsUpdates.SearchAsync();
        Assert.Equal(before, Values());
        Assert.All(found, u => Assert.False(string.IsNullOrEmpty(u.Id)));
        if (Environment.GetEnvironmentVariable("KOS_TEST_WU_HIDE") != "1") return;
        // Hides one optional update and shows it again: the hidden flag must read back both times.
        var u = found.First(f => !f.Hidden && !f.Mandatory);
        Assert.Equal(1, await WindowsUpdates.SetHiddenAsync(new[] { u.Id }, true));
        try { Assert.True((await WindowsUpdates.SearchAsync()).Single(f => f.Id == u.Id).Hidden); }
        finally { Assert.Equal(1, await WindowsUpdates.SetHiddenAsync(new[] { u.Id }, false)); }
        Assert.False((await WindowsUpdates.SearchAsync()).Single(f => f.Id == u.Id).Hidden);
        Assert.Equal(before, Values());
    }
}
