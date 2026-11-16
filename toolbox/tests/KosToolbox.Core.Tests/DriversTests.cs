using KosToolbox.Core.Drivers;
using KosToolbox.Core.Execution;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DriversTests
{
    [Fact]
    public void ParsesVersionSectionWithStringTokens()
    {
        var inf = new[]
        {
            "; comentário", "[Version]", "Signature=$WINDOWS NT$", "Class=Net ; placa de rede", "Provider=%Rtk%",
            "DriverVer = 07/18/2024, 10.68.815.2024", "", "[Strings]", "Rtk = \"Realtek\"",
        };
        var (cls, provider, date, version) = DriverStore.ParseInf(inf);
        Assert.Equal("Net", cls);
        Assert.Equal("Realtek", provider);
        Assert.Equal(new DateTime(2024, 7, 18), date);
        Assert.Equal(new Version(10, 68, 815, 2024), version);
    }

    [Fact]
    public void FlagsOlderPackagesOfTheSameDriver()
    {
        DriverPackage P(string oem, string inf, string provider, string ver) =>
            new(oem, inf, provider, "Display", null, Version.Parse(ver), 0, Array.Empty<string>());
        var old = DriverStore.OldVersions(new[]
        {
            P("oem3.inf", "nv_dispi.inf", "NVIDIA", "31.0.15.3758"),
            P("oem7.inf", "nv_dispi.inf", "NVIDIA", "32.0.15.6094"),
            P("oem9.inf", "NV_DISPI.INF", "nvidia", "30.0.14.7141"),
            P("oem4.inf", "other.inf", "NVIDIA", "1.0.0.0"),
        });
        Assert.Equal(new[] { "oem3.inf", "oem9.inf" }, old.OrderBy(x => x));
    }

    [Fact]
    public void WiderMatchKeepsTheNewestPerProviderAndClass()
    {
        DriverPackage P(string oem, string inf, string provider, string cls, string ver) =>
            new(oem, inf, provider, cls, null, Version.Parse(ver), 0, Array.Empty<string>());
        var older = DriverStore.OlderByProviderAndClass(new[]
        {
            P("oem3.inf", "nv_dispi.inf", "NVIDIA", "Display", "31.0.15.3758"),
            P("oem7.inf", "nv_dispig.inf", "NVIDIA", "Display", "32.0.15.6094"), // other INF name, same provider and class
            P("oem4.inf", "nvhda.inf", "NVIDIA", "MEDIA", "1.4.0.1"),             // the only one of its class
            P("oem5.inf", "x.inf", "", "Display", "1.0.0.0"),                      // no provider: never matched
        });
        Assert.Equal(new[] { "oem3.inf" }, older);
    }

    [Fact]
    public void DisplayRemovalTakesVendorVideoAndOptionallyAudio()
    {
        DriverPackage P(string oem, string provider, string cls) => new(oem, oem, provider, cls, null, null, 0, Array.Empty<string>());
        var all = new[] { P("oem1.inf", "NVIDIA", "Display"), P("oem2.inf", "NVIDIA Corporation", "MEDIA"), P("oem3.inf", "Intel Corporation", "Display"), P("oem4.inf", "NVIDIA", "System") };
        Assert.Equal(new[] { "oem1.inf" }, DisplayDrivers.RemovalSet(all, "NVIDIA", false).Select(p => p.PublishedName));
        Assert.Equal(new[] { "oem1.inf", "oem2.inf" }, DisplayDrivers.RemovalSet(all, "NVIDIA", true).Select(p => p.PublishedName));
    }

    [Fact]
    public void InstallAcceptsOnlyLocalDisplayInfs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kos-drv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Display.Driver"));
        try
        {
            var display = Path.Combine(dir, "Display.Driver", "nv_disp.inf");
            File.WriteAllLines(display, new[] { "[Version]", "Class=Display" });
            var audio = Path.Combine(dir, "hdaudio.inf");
            File.WriteAllLines(audio, new[] { "[Version]", "Class=MEDIA" });
            Assert.Equal(new[] { display }, DisplayDrivers.FindDisplayInfs(dir));
            new ElevatedJob { Drivers = new DriverJob { Install = display } }.Validate();
            Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Install = audio } }.Validate());                      // not video
            Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Install = @"\\server\share\x.inf" } }.Validate());     // network
            Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Install = display, Force = true } }.Validate());       // mixed
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ElevatedDriverRequestsAreRestricted()
    {
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Delete = { @"..\..\Windows\inf\oem1.inf" } } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Delete = { "nv_dispi.inf" } } }.Validate());   // only oemNN.inf
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Delete = { "oem99999.inf" } } }.Validate());   // doesn't exist
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob { Export = "oem1.inf", ExportFolder = @"\\server\share" } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Drivers = new DriverJob() }.Validate());                                    // neither delete nor export
    }
}
