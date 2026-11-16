using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosToolbox.Core.Tests;

public class StabilityTests
{
    static string Event(string provider, int id, string data) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/><EventID>{id}</EventID>" +
        $"<TimeCreated SystemTime='2026-11-14T03:00:00.0000000Z'/></System><EventData>{data}</EventData></Event>";

    [Fact]
    public void KernelPower41ShowsTheBugCheckInHex()
    {
        var e = Stability.Parse(Event("Microsoft-Windows-Kernel-Power", 41, "<Data Name='BugcheckCode'>292</Data><Data Name='PowerButtonTimestamp'>0</Data>"))!;
        Assert.Equal(StabilityKind.UnexpectedRestart, e.Kind);
        Assert.Contains("0x00000124 WHEA_UNCORRECTABLE_ERROR", e.Detail);
        Assert.True(e.Severe);
        Assert.Contains("power cut", Stability.Parse(Event("Microsoft-Windows-Kernel-Power", 41, "<Data Name='BugcheckCode'>0</Data>"))!.Detail);
    }

    [Fact]
    public void BlueScreenTdrAndWhea()
    {
        var bsod = Stability.Parse(Event("Microsoft-Windows-WER-SystemErrorReporting", 1001,
            "<Data Name='param1'>0x0000009f (0x0000000000000003, 0xffffc00000000000)</Data><Data Name='param2'>C:\\Windows\\MEMORY.DMP</Data>"))!;
        Assert.Equal("0x0000009F DRIVER_POWER_STATE_FAILURE · dump C:\\Windows\\MEMORY.DMP", bsod.Detail);
        var tdr = Stability.Parse(Event("Display", 4101, "<Data>nvlddmkm</Data><Data></Data>"))!;
        Assert.Equal(StabilityKind.DriverReset, tdr.Kind);
        Assert.StartsWith("nvlddmkm stopped responding", tdr.Detail);
        var whea = Stability.Parse(Event("Microsoft-Windows-WHEA-Logger", 19, "<Data Name='ErrorSource'>1</Data>"))!;
        Assert.Equal("Corrected hardware error", whea.Title);
        Assert.StartsWith("Corrected machine check (CPU)", whea.Detail);
        Assert.False(whea.Severe);
        Assert.Null(Stability.Parse(Event("Service Control Manager", 7036, "")));
    }

    [Fact]
    public void ReadsThisPcsSystemLog()
    {
        // Read-only; may be empty on a clean PC. Newest logged first (not sorted by time: a wrong clock once wrote this PC's log in 2116).
        var events = Stability.Events(50);
        Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Title)));
    }

    [Fact]
    public void TheTimeFilterIsAValidQuery()
    {
        var since = DateTime.Now.AddDays(-30);
        Assert.All(Stability.Events(50, since), e => Assert.True(e.Time >= since.AddSeconds(-1)));
    }

    [Fact]
    public void PresentDeviceIdsMatchSetupApi()
    {
        // What the gamemode session report diffs every second to notice unplugged devices.
        var ids = KosToolbox.Core.Devices.PnpDevices.PresentIds();
        var present = KosToolbox.Core.Devices.PnpDevices.Enumerate(presentOnly: true).Select(d => d.InstanceId).ToList();
        Assert.NotEmpty(ids);
        Assert.True(present.Count(ids.Contains) >= present.Count - 2, "cfgmgr32 and SetupAPI disagree on the present devices");
    }
}
