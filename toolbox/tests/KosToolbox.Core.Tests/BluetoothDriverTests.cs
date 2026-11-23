using KosToolbox.Core.Drivers;
using Xunit;

namespace KosToolbox.Core.Tests;

public class BluetoothDriverTests
{
    const string Search = """
        <a id='4714fae5-5da6-4777-b431-e6b9881f5650_link' href="x">Realtek Semiconductor Corp. - Bluetooth - 1.9.1040.1007</a>
        <a id='2555433F-42BD-4467-8D53-96F9F62F55D5_link'>Realtek Semiconductor Corp. - Bluetooth - 1.9.1051.3002</a>
        <a id='4714fae5-5da6-4777-b431-e6b9881f5650_link'>again</a>
        """;

    // The text the details page shows (scripts and markup around it left out of the sample).
    static string Details(string arch = "AMD64", string products = "Windows 11 Client, version 22H2 and later, Servicing Drivers", string ids = @"usb\vid_0bda&amp;pid_a729",
        string version = "1.9.1051.3002", string date = "3/1/2023") => $"""
        <script>var x = 'Architecture: ARM64 Hardware IDs nothing';</script>
        <div><span>Architecture:</span> {arch}</div><div>Classification: Drivers</div><div>Supported products: {products}</div>
        <div>Driver Model: Realtek Bluetooth 5.3 Adapter</div><div>Driver Provider: Realtek Semiconductor Corp.</div>
        <div>Version: {version}</div><div>Version Date: {date}</div><div>More information:</div>
        <div>Hardware IDs</div><div>{ids}</div><div>Supported Update IDs</div><div>2555433f</div>
        """;

    [Fact]
    public void ReadsTheSearchResultIds()
    {
        var ids = BluetoothDriver.ParseSearch(Search);
        Assert.Equal(new[] { "4714fae5-5da6-4777-b431-e6b9881f5650", "2555433f-42bd-4467-8d53-96f9f62f55d5" }, ids);
    }

    [Fact]
    public void ReadsADetailsPage()
    {
        var d = BluetoothDriver.ParseDetails(Details(), "2555433f-42bd-4467-8d53-96f9f62f55d5", @"USB\VID_0BDA&PID_A729");
        Assert.NotNull(d);
        Assert.Equal("1.9.1051.3002", d.Version);
        Assert.Equal(new DateTime(2023, 3, 1), d.Date);
        Assert.Equal("Realtek Bluetooth 5.3 Adapter", d.Model);
        Assert.Equal("Realtek Semiconductor Corp.", d.Provider);
        Assert.True(d.Windows11);
        Assert.False(BluetoothDriver.ParseDetails(Details(products: "Windows - Client, version 21H2 and later"), "x", @"USB\VID_0BDA&PID_A729")!.Windows11);
    }

    [Fact]
    public void RefusesAnotherArchitectureOrAnotherHardwareId()
    {
        Assert.Null(BluetoothDriver.ParseDetails(Details(arch: "ARM64"), "x", @"USB\VID_0BDA&PID_A729"));
        Assert.Null(BluetoothDriver.ParseDetails(Details(ids: @"usb\vid_0bda&amp;pid_8771"), "x", @"USB\VID_0BDA&PID_A729"));
        Assert.Null(BluetoothDriver.ParseDetails("<div>nothing useful</div>", "x", @"USB\VID_0BDA&PID_A729"));
    }

    [Fact]
    public void PicksTheNewestDriverForWindows11()
    {
        CatalogDriver D(string id, string version, int year, bool w11) => new(id, "m", "p", version, new DateTime(year, 1, 1), w11);
        var best = BluetoothDriver.Best(new[] { D("old", "1.9.1038.3008", 2022, true), D("new", "1.9.1051.3002", 2023, true), D("w10", "1.9.1060.1", 2024, false) });
        Assert.Equal("new", best!.UpdateId); // the Windows 10-only entry is newer but this PC runs Windows 11
        Assert.Equal("w10", BluetoothDriver.Best(new[] { D("w10", "1.0", 2024, false) })!.UpdateId);
        Assert.Null(BluetoothDriver.Best(Array.Empty<CatalogDriver>()));
    }

    [Fact]
    public void ReadsTheDownloadDialogAndOnlyTrustsMicrosoftsServers()
    {
        var driver = new CatalogDriver("x", "m", "p", "1", null, true);
        const string sha = "PKMnX8dAeYR5N+WXS2fHJyHg9XeexqfLfZOr7ypZH6Q=";
        string Dialog(string url) => $"downloadInformation[0].files[0].url = '{url}'; downloadInformation[0].files[0].sha256 = '{sha}';";
        var ok = BluetoothDriver.ParseDownload(Dialog("https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2023/04/a_b.cab"), driver);
        Assert.NotNull(ok);
        Assert.Equal(sha, ok.Sha256);
        Assert.Null(BluetoothDriver.ParseDownload(Dialog("https://evil.example.com/c/a_b.cab"), driver));
        Assert.Null(BluetoothDriver.ParseDownload(Dialog("http://catalog.s.download.windowsupdate.com/a.cab"), driver));
        Assert.Null(BluetoothDriver.ParseDownload(Dialog("https://catalog.s.download.windowsupdate.com/a.exe"), driver));
        Assert.Null(BluetoothDriver.ParseDownload(Dialog("https://download.windowsupdate.com.evil.com/a.cab"), driver));
        Assert.Null(BluetoothDriver.ParseDownload("nothing", driver));
    }

    [Theory]
    [InlineData(@"USB\VID_0BDA&PID_A729\00E04C239987", @"USB\VID_0BDA&PID_A729")]
    [InlineData(@"usb\vid_0bda&pid_a729\5&2c8e5f9&0&8", @"USB\VID_0BDA&PID_A729")]
    [InlineData(@"USB\VID_8087&PID_0033&MI_00\6&1&0&0000", null)] // a function of a composite device
    [InlineData(@"BTH\MS_BTHBRB\6&1D3B1BBE&0&1", null)]
    public void TheHardwareIdOfAnAdapter(string instance, string? expected) => Assert.Equal(expected, BluetoothDriver.HardwareIdOf(instance));

    [Fact]
    public void OnlyABluetoothInfWithTheAdaptersIdIsInstalled()
    {
        const string id = @"USB\VID_0BDA&PID_A729";
        const string inf = "[Version]\nClass=Bluetooth\n[Models]\n%RealTK53.DeviceDesc% = RsBthFilter, USB\\VID_0BDA&PID_A729    ;8761B BT53\n";
        Assert.True(BluetoothDriver.IsDriverFor(inf, id));
        Assert.False(BluetoothDriver.IsDriverFor(inf.Replace("Class=Bluetooth", "Class=Net"), id));
        Assert.False(BluetoothDriver.IsDriverFor(inf, @"USB\VID_0BDA&PID_A72")); // a prefix of another id
        Assert.False(BluetoothDriver.IsDriverFor(inf.Replace("= RsBthFilter, USB", "; RsBthFilter, USB"), id)); // commented out
        Assert.False(BluetoothDriver.IsDriverFor(inf.Replace("PID_A729    ;", "PID_A7299    ;"), id));
    }
}
