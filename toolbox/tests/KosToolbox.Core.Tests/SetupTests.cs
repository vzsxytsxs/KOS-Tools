using System.Text.RegularExpressions;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Setup;
using Xunit;

namespace KosToolbox.Core.Tests;

public class SetupTests
{
    [Fact]
    public void CatalogIsConsistent()
    {
        var all = SetupCatalog.All.ToList();
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct().Count());
        foreach (var a in all)
        {
            if (a.Variants.Length > 0)
            {
                Assert.True(a.Resolve is null && a.StoreUrl is null, a.Id); // the card only opens the version picker
                Assert.All(a.Variants, v => Assert.Empty(v.Variants));
                continue;
            }
            // Exactly one way: Chocolatey, a download or the Store.
            Assert.Equal(1, new[] { a.Choco is not null, a.Resolve is not null, a.StoreUrl is not null }.Count(b => b));
            foreach (var d in a.Detect) _ = new Regex(d);
            if (a.Choco is { } package) { Assert.Matches(Chocolatey.PackageId(), package); Assert.Null(a.ZipEntry); Assert.NotEmpty(a.Detect); continue; }
            Assert.Contains(Path.GetExtension(a.FileName).ToLowerInvariant(), new[] { ".exe", ".msi", ".zip", ".ps1" });
            Assert.Equal(a.ZipEntry is not null, a.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("Google Chrome", "chrome")]
    [InlineData("Voicemeeter, The Virtual Mixing Console", "voicemeeter-potato")]
    [InlineData("MSI Afterburner 4.6.7 Beta 2", "afterburner")]
    [InlineData("Git", "git")]
    [InlineData("Corsair iCUE5 Software", "icue")]
    [InlineData("Notepad++ (64-bit x64)", "notepadpp")]
    [InlineData("League of Legends", "riot")]
    [InlineData("VALORANT", "riot")]
    [InlineData("Thorium", "thorium")]
    [InlineData("Discord", "discord-stable")]
    [InlineData("Discord Canary", "discord-canary")]
    [InlineData("Vesktop", "vesktop")]
    [InlineData("Rare", "rare")]
    public void DetectsInstalledNames(string displayName, string id)
    {
        var program = new InstalledProgram("x", displayName, null, "1.0", null, null, null, null, null, "HKLM", null);
        Assert.Same(program, SetupCatalog.Installed(SetupCatalog.Find(id)!, new[] { program }));
        Assert.Null(SetupCatalog.Installed(SetupCatalog.Find("steam")!, new[] { program }));
    }

    [Fact]
    public void DiscordChannelsDoNotMatchEachOther()
    {
        var canary = new InstalledProgram("x", "Discord Canary", null, "1.0", null, null, null, null, null, "HKCU", null);
        Assert.Null(SetupCatalog.Installed(SetupCatalog.Find("discord-stable")!, new[] { canary }));
    }

    [Fact]
    public void SpotXNeedsItsBackupFile()
    {
        var spotify = new InstalledProgram("x", "Spotify", null, "1.2.13.661", null, null, null, null, null, "HKCU", null);
        var spotx = SetupCatalog.Find("spotx-new")! with { DetectFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
        Assert.Null(SetupCatalog.Installed(spotx, new[] { spotify }));
        File.WriteAllText(spotx.DetectFile!, "");
        try
        {
            Assert.Null(SetupCatalog.Installed(spotx, new[] { spotify })); // 1.2.13 is the old theme
            Assert.Same(spotify, SetupCatalog.Installed(spotx with { DetectVersion = SetupCatalog.Find("spotx-old")!.DetectVersion }, new[] { spotify }));
            var latest = spotify with { Version = "1.3.6.1" };
            Assert.Same(latest, SetupCatalog.Installed(spotx, new[] { latest }));
        }
        finally { File.Delete(spotx.DetectFile!); }
        Assert.Contains("-podcasts_off", SetupCatalog.Find("spotx-old")!.Arguments);
    }

    [Theory]
    [InlineData("\"Firefox Setup 144.0.exe\"", null, "Firefox Setup.exe", "Firefox Setup 144.0.exe")]
    [InlineData(null, "https://cdn.example.com/a/Firefox%20Setup%20144.0.exe", "Firefox Setup.exe", "Firefox Setup 144.0.exe")]
    [InlineData(null, "https://example.com/latest/winx64", "BraveBrowserSetup.exe", "BraveBrowserSetup.exe")]
    [InlineData("..\\..\\evil.exe", null, "setup.exe", "evil.exe")]
    [InlineData("setup.bat", null, "setup.exe", "setup.exe")]
    [InlineData("..", null, "setup.exe", "setup.exe")]
    public void PicksASafeFileName(string? disposition, string? url, string fallback, string expected) =>
        Assert.Equal(expected, Downloader.FileNameFor(disposition, url is null ? null : new Uri(url), fallback));

    [Fact]
    public void PicksTheNewestVersionFolder()
    {
        Assert.Equal("158.0-1", SetupCatalog.NewestVersion(new[] { "99.0.1.2", "158.0-1", "157.0.4-2", ".validation", "M138.0.7204.300", "Beta" }));
        Assert.Equal("4.6.6", SetupCatalog.NewestVersion(new[] { "4.6.5", "4.6.6", "4.6" }));
        Assert.Null(SetupCatalog.NewestVersion(new[] { ".validation" }));
    }

    [Fact]
    public void DeletesOnlyInsideTheDownloadsFolder()
    {
        Assert.True(Installer.IsInsideDownloads(Path.Combine(Downloader.Folder, "a.exe")));
        Assert.True(Installer.IsInsideDownloads(Path.Combine(Downloader.Folder, "Voicemeeter8Setup", "x.exe")));
        Assert.False(Installer.IsInsideDownloads(Downloader.Folder));
        Assert.False(Installer.IsInsideDownloads(Path.Combine(Downloader.Folder, "..", "a.exe")));
        Assert.False(Installer.IsInsideDownloads(Downloader.Folder + "2\\a.exe"));
    }

    [Fact]
    public void ParsesAria2Readout()
    {
        var p = Downloader.ParseAria2("[#2089b0 400.0KiB/33.2MiB(1%) CN:1 DL:115.7KiB ETA:4m51s]");
        Assert.NotNull(p);
        Assert.Equal(409600, p!.Value.Received);
        Assert.Equal((long)(33.2 * 1024 * 1024), p.Value.Total);
        Assert.Equal(115.7 * 1024, p.Value.BytesPerSecond, 1);
        Assert.True(p.Value.Aria2);
        Assert.Null(Downloader.ParseAria2("*** Download Progress Summary as of Mon Oct 20 10:00:00 2026 ***"));
    }

    [Fact]
    public void PrefersTheX64InstallerOfAManifest()
    {
        const string yaml = """
            PackageIdentifier: Some.App
            Installers:
            - Architecture: x86
              InstallerUrl: https://example.com/app-x86.exe
            - Architecture: x64
              InstallerUrl: https://example.com/app-x64.exe
            ManifestType: installer
            """;
        Assert.Equal("https://example.com/app-x64.exe", SetupCatalog.InstallerUrl(yaml));
        Assert.Equal("https://example.com/only.zip", SetupCatalog.InstallerUrl("Installers:\n- Architecture: x86\n  InstallerUrl: https://example.com/only.zip\n"));
    }
}
