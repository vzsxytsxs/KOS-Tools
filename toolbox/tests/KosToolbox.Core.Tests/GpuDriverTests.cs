using System.Text.Json;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Drivers;
using KosToolbox.Core.Execution;
using Xunit;

namespace KosToolbox.Core.Tests;

/// <summary>GPU › Driver: NVIDIA lookup parsers, package components, install job validation and DDU's command line.</summary>
public class GpuDriverTests
{
    const string Products = """
        <?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues>
        <LookupValue ParentID="112"><Name>GeForce GTX 1660 SUPER</Name><Value>910</Value></LookupValue>
        <LookupValue ParentID="112"><Name>GeForce GTX 1650 SUPER</Name><Value>911</Value></LookupValue>
        <LookupValue ParentID="112"><Name>GeForce GTX 1650</Name><Value>895</Value></LookupValue>
        <LookupValue ParentID="115"><Name>GeForce GTX 1650</Name><Value>897</Value></LookupValue>
        <LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070</Name><Value>1015</Value></LookupValue>
        <LookupValue ParentID="x"><Name>Broken</Name><Value>1</Value></LookupValue>
        </LookupValues></LookupValueSearch>
        """;

    [Fact]
    public void MatchesTheDeviceNameExactly()
    {
        var products = NvidiaDriver.ParseLookup(Products);
        Assert.Equal(5, products.Count); // the entry with a non-numeric parent is left out

        var super = Assert.Single(NvidiaDriver.MatchProducts(products, "NVIDIA GeForce GTX 1650 SUPER"));
        Assert.Equal((911, 112), (super.Value, super.Parent));
        // Desktop and notebook share the name; the caller picks by battery.
        Assert.Equal(new[] { 112, 115 }, NvidiaDriver.MatchProducts(products, "  nvidia  GeForce GTX 1650 ").Select(p => p.Parent));
        Assert.Single(NvidiaDriver.MatchProducts(products, "NVIDIA GeForce RTX 4070"));
        Assert.Empty(NvidiaDriver.MatchProducts(products, "Microsoft Basic Display Adapter"));
        Assert.Empty(NvidiaDriver.MatchProducts(products, "NVIDIA GeForce RTX 4070 Ti"));
    }

    [Fact]
    public void NamesTheCardFromThePciIdDatabase()
    {
        const string pciIds = "# comment\n1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t2187  Not an NVIDIA card\n10de  NVIDIA Corporation\n"
            + "\t1c82  GP107 [GeForce GTX 1050 Ti]\n\t\t1043 8613  subsystem line\n\t2187  TU116 [GeForce GTX 1650 SUPER]\n\t2204  GA102 [GeForce RTX 3090]\n\t10c3  GT218 NoBrackets\n10df  Emulex Corporation\n\t2187  not ours\n";
        Assert.Equal("GeForce GTX 1650 SUPER", NvidiaDriver.PciName(pciIds, "2187")); // not the AMD or Emulex line with the same id
        Assert.Equal("GeForce RTX 3090", NvidiaDriver.PciName(pciIds.Replace("\n", "\r\n"), "2204"));
        Assert.Equal("GT218 NoBrackets", NvidiaDriver.PciName(pciIds, "10C3"));
        Assert.Null(NvidiaDriver.PciName(pciIds, "ffff"));
        Assert.Null(NvidiaDriver.PciName(pciIds, "1043")); // a subsystem id isn't a device id
    }

    [Fact]
    public void ParsesTheVersionList()
    {
        const string json = """
            {"Success":"2","IDS":[
             {"downloadInfo":{"Version":"621.14","ReleaseDateTime":"Wed Nov 04, 2026","Name":"GeForce%20Game%20Ready%20Driver","IsBeta":"0","IsCRD":"0",
              "DownloadURL":"https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe","DownloadURLFileSize":"1.04 GB"}},
             {"downloadInfo":{"Version":"621.02","ReleaseDateTime":"Tue Oct 20, 2026","Name":"Beta","IsBeta":"1",
              "DownloadURL":"https://us.download.nvidia.com/Windows/621.02/621.02-beta.exe"}},
             {"downloadInfo":{"Version":"620.88","ReleaseDateTime":"Thu Oct 08, 2026","Name":"Evil","IsBeta":"0",
              "DownloadURL":"https://example.com/620.88.exe"}}]}
            """;
        var r = Assert.Single(NvidiaDriver.ParseReleases(json, studio: false));
        Assert.Equal("621.14", r.Version);
        Assert.Equal(new DateTime(2026, 11, 4), r.Date);
        Assert.Equal("1.04 GB", r.Size);
        Assert.Equal("GeForce Game Ready Driver", r.Name);
        Assert.False(r.Studio);
        Assert.Equal("621.14-desktop-win10-win11-64bit-international-dch-whql.exe", r.FileName);
        Assert.Empty(NvidiaDriver.ParseReleases("""{"Success":"0"}""", false));
    }

    [Fact]
    public void ParsesTheLookupByDeviceId()
    {
        const string json = """
            {"DriverAttributes":{"Name":"GeForce Game Ready Driver","ReleaseDateTime":"2026-11-04T00:00:00+0000","Version":"621.14","IsCRD":0,
             "DownloadURL":"https:\/\/international-gfe.download.nvidia.com\/Windows\/621.14\/621.14-desktop-win10-win11-64bit-international-dch-whql-g.exe",
             "DownloadURLAdmin":"https:\/\/us.download.nvidia.com\/Windows\/621.14\/621.14-desktop-win10-win11-64bit-international-dch-whql.exe"}}
            """;
        var r = NvidiaDriver.ParseDeviceLookup(json, studio: false)!;
        Assert.Equal("621.14", r.Version);
        Assert.Equal(new DateTime(2026, 11, 4), r.Date);
        Assert.Equal("https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe", r.Url);
        Assert.Null(NvidiaDriver.ParseDeviceLookup("""{"DriverAttributes":null}""", false));

        using var query = JsonDocument.Parse(NvidiaDriver.DeviceQuery("2187", laptop: true, studio: true));
        Assert.Equal("2187_10DE", query.RootElement.GetProperty("dIDa")[0].GetString());
        Assert.Equal("1", query.RootElement.GetProperty("iLp").GetString());
        Assert.Equal("1", query.RootElement.GetProperty("upCRD").GetString());
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.14/621.14-desktop.exe", true)]
    [InlineData("https://international-gfe.download.nvidia.com/Windows/621.14/x.exe", true)]
    [InlineData("http://us.download.nvidia.com/Windows/621.14/x.exe", false)]
    [InlineData("https://download.nvidia.com.example.com/x.exe", false)]
    [InlineData("https://evil.com/download.nvidia.com/x.exe", false)]
    [InlineData("https://us.download.nvidia.com/Windows/x.zip", false)]
    [InlineData("https://us.download.nvidia.com/Windows/x.exe?y=1", false)]
    [InlineData("https://user@us.download.nvidia.com/Windows/x.exe", false)]
    public void OnlyNvidiaDownloadLinks(string url, bool valid) => Assert.Equal(valid, NvidiaDriver.IsValidUrl(url));

    [Theory]
    [InlineData("31.0.15.3758", "537.58")]
    [InlineData("32.0.16.2114", "621.14")]
    [InlineData("32.0.15.94", "500.94")]
    [InlineData("1.2.3", null)]
    [InlineData("32.0.16.21145", null)]
    [InlineData(null, null)]
    public void NvidiaVersionFromTheWindowsVersion(string? windows, string? nvidia) => Assert.Equal(nvidia, NvidiaDriver.FriendlyVersion(windows));

    [Fact]
    public void ListsTopLevelFoldersAsComponents()
    {
        // "7z l -slt -ba" of a 621.14 package (shortened).
        var listing = string.Join("\r\n", "Path = Display.Driver", "Attributes = D", "", "Path = Display.Driver\\nvlddmkm.sys", "Size = 1",
            "Path = Display.Driver\\NVCPL\\AUMIDs.txt", "Path = NVI2\\NVI2.dll", "Path = PhysX\\PhysX.nvi", "Path = NvApp\\NvApp.nvi",
            "Path = NewThing\\a.nvi", "Path = setup.exe", "Path = setup.cfg");
        var components = NvidiaDriver.ParseComponents(listing);
        Assert.Equal(new[] { "Display.Driver", "NVI2" }, components.Where(c => c.Required).Select(c => c.Folder));
        Assert.Equal(new[] { "Display.Driver", "NewThing", "NvApp", "NVI2", "PhysX" }, components.Select(c => c.Folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("NewThing", components.Single(c => c.Folder == "NewThing").Name); // unknown folders keep their name
        Assert.Equal("NVIDIA app", components.Single(c => c.Folder == "NvApp").Name);
    }

    [Fact]
    public void StripsTheFilesOfLeftOutComponentsFromSetupCfg()
    {
        var cfg = string.Join("\r\n",
            "\t\t<string name=\"EulaHtmlFileFullPath\" value=\"${{InstallerDirectory}}/${{EulaHtmlFile}}\"/>",
            "\t<manifest>", "\t\t<file name=\"setup.exe\" core=\"true\"/>", "\t\t<file name=\"eula.txt\"/>",
            "\t\t<file name=\"${{EulaHtmlFile}}\"/>", "\t\t<file name=\"${{FunctionalConsentFile}}\"/>", "\t\t<file name=\"${{PrivacyPolicyFile}}\"/>",
            "\t\t<file name=\"${{InstallerDllDirectory}}/NVI2.dll\" core=\"true\"/>");
        var stripped = NvidiaDriver.StripSetupCfg(cfg);
        Assert.DoesNotContain("name=\"${{EulaHtmlFile}}\"", stripped);
        Assert.DoesNotContain("FunctionalConsentFile", stripped);
        Assert.DoesNotContain("PrivacyPolicyFile", stripped);
        Assert.Contains("EulaHtmlFileFullPath", stripped);
        Assert.Contains("NVI2.dll", stripped);
        Assert.Equal(5, stripped.Split('\n').Length);
        Assert.Contains("\r\n", stripped); // line endings kept
    }

    [Fact]
    public void InstallJobAcceptsOnlyComponentFoldersAndKnownTweaks()
    {
        var package = Path.Combine(Path.GetTempPath(), $"kos-test-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(package, new byte[] { 0x4D, 0x5A });
        try
        {
            NvidiaDriver.Validate(new NvidiaInstallJob { Package = package, Components = { "PhysX", "HDAudio" }, Tweaks = { "nv-msi", "nv-telemetry" } });
            new ElevatedJob { NvidiaInstall = new NvidiaInstallJob { Package = package } }.Validate();

            foreach (var bad in new[] { "..", "a\\b", "a/b", "C:x", "Display.Driver", "nvi2", "", ".hidden" })
                Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = package, Components = { bad } }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = package, Components = { "PhysX", "physx" } }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = package, Tweaks = { "uac" } }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = package + ".missing.exe" }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = "relative.exe" }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = @"\\server\share\driver.exe" }));
            Assert.ThrowsAny<Exception>(() => NvidiaDriver.Validate(new NvidiaInstallJob { Package = @"\\?\GLOBALROOT\Device\HarddiskVolume3\driver.exe" }));
            Assert.ThrowsAny<Exception>(() => new ElevatedJob { NvidiaInstall = new NvidiaInstallJob { Package = package }, Ddu = new DduJob { Vendor = "NVIDIA" } }.Validate());
        }
        finally { File.Delete(package); }
    }

    [Fact]
    public void PostInstallTweaksExistWithTheirOption()
    {
        foreach (var (id, option, _, _) in NvidiaDriver.PostInstallTweaks)
        {
            var tweak = KosCatalog.FreshNvidiaTweak(id);
            Assert.NotNull(tweak);
            Assert.Contains(tweak!.Options, o => o.Key == option);
        }
        Assert.Null(KosCatalog.FreshNvidiaTweak("uac"));
        Assert.NotNull(KosCatalog.Find("wu-drivers"));
    }

    [Theory]
    [InlineData("The entry was successfully copied to {3b0b8f6e-0c2f-11f1-9a4e-a8a1591d0c7e}.\r\n", "{3b0b8f6e-0c2f-11f1-9a4e-a8a1591d0c7e}")]
    [InlineData("A entrada foi copiada com êxito para {3B0B8F6E-0C2F-11F1-9A4E-A8A1591D0C7E}.", "{3B0B8F6E-0C2F-11F1-9A4E-A8A1591D0C7E}")]
    [InlineData("The specified entry type is invalid.", null)]
    [InlineData("{3b0b8f6e-0c2f-11f1-9a4e-a8a1591d0c7e} {9dea862c-5cdd-4e70-acc1-f32b344d4795}", null)]
    public void BootEntryIdFromBcdeditCopy(string output, string? id) => Assert.Equal(id, Ddu.BootEntryId(output));

    [Fact]
    public void DduCommandLine()
    {
        var now = Ddu.Arguments(new DduJob { Vendor = "NVIDIA", Options = { "-RemovePhysx", Ddu.RestorePoint, "-RemoveVulkan" } });
        Assert.Equal(new[] { "-Silent", "-NoSafeModeMsg", "-CleanNvidia", "-RemovePhysx", "-RemoveVulkan", "-Restart" }, now);

        // No restore point in Safe Mode (System Restore doesn't run there), nor when not asked.
        var safe = Ddu.Arguments(new DduJob { Vendor = "AMD", Options = { "-RemoveAMDDirs", Ddu.RestorePoint }, SafeMode = true });
        Assert.Equal(new[] { "-Silent", "-NoSafeModeMsg", "-CleanAmd", "-RemoveAMDDirs", "-NoRestorePoint", "-Restart" }, safe);
        Assert.Contains("-NoRestorePoint", Ddu.Arguments(new DduJob { Vendor = "INTEL" }));

        new ElevatedJob { Ddu = new DduJob { Vendor = "INTEL", Options = { "-RemoveINTELCP" } } }.Validate();
        Assert.ThrowsAny<Exception>(() => Ddu.Validate(new DduJob { Vendor = "NVIDIA", Options = { "-RemoveAMDCP" } }));   // another vendor's option
        Assert.ThrowsAny<Exception>(() => Ddu.Validate(new DduJob { Vendor = "NVIDIA", Options = { "-Shutdown" } }));      // not in the list
        Assert.ThrowsAny<Exception>(() => Ddu.Validate(new DduJob { Vendor = "NVIDIA", Options = { "-RemovePhysx", "-RemovePhysx" } }));
        Assert.ThrowsAny<Exception>(() => Ddu.Validate(new DduJob { Vendor = "nvidia" }));
        Assert.ThrowsAny<Exception>(() => Ddu.Validate(new DduJob { Vendor = "-cleanallgpus" }));
    }

    [Fact]
    public void ExtractSizeCountsOnlyTheFilesOfIncludedFolders()
    {
        const string listing = "Path = Display.Driver\\nvlddmkm.sys\r\nSize = 100\r\n\r\nPath = PhysX\\physx.msi\r\nSize = 50\r\n\r\nPath = setup.exe\r\nSize = 7\r\n\r\nPath = NVI2\r\nSize = 0\r\n";
        Assert.Equal(107, NvidiaDriver.ExtractSize(listing, new[] { "physx" }));
        Assert.Equal(157, NvidiaDriver.ExtractSize(listing, Array.Empty<string>()));
    }

    [Fact]
    public void ProgressFileIsParsedOrIgnored()
    {
        Assert.Equal(new NvidiaInstallProgress("extract", 42), NvidiaDriver.ParseProgress("extract|42"));
        Assert.Equal(new NvidiaInstallProgress("setup", null), NvidiaDriver.ParseProgress("setup|"));
        Assert.Equal(new NvidiaInstallProgress("extract", 100), NvidiaDriver.ParseProgress("extract|250"));
        Assert.Null(NvidiaDriver.ParseProgress("other|1"));
        Assert.Null(NvidiaDriver.ParseProgress(""));
    }
}
