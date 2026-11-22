using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosToolbox.Core.Tests;

public class CpuPowerControlTests
{
    static string Event(int impl, int max, int min) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-Processor-Power'/>" +
        "<EventID>55</EventID><TimeCreated SystemTime='2026-11-21T14:25:00.000Z'/></System><EventData>" +
        "<Data Name='Group'>0</Data><Data Name='Number'>0</Data><Data Name='IdleStateCount'>1</Data><Data Name='IdleImplementation'>1</Data>" +
        $"<Data Name='NominalFrequency'>2904</Data><Data Name='MaximumPerformancePercent'>{max}</Data><Data Name='MinimumPerformancePercent'>{min}</Data>" +
        $"<Data Name='MinimumThrottlePercent'>100</Data><Data Name='PerformanceImplementation'>{impl}</Data></EventData></Event>";

    [Fact]
    public void NoPerformanceStatesMeansTheBiosFixesTheFrequency()
    {
        var c = CpuPowerControl.Parse(Event(0, 100, 100))!;
        Assert.True(c.FixedByBios);
        Assert.Equal(2904, c.NominalMhz);
        Assert.Contains("BIOS", c.Describe());
    }

    [Fact]
    public void CppcIsWindowsControlled()
    {
        var c = CpuPowerControl.Parse(Event(3, 879, 31))!;
        Assert.False(c.FixedByBios);
        Assert.Equal(879, c.MaxPercent);
    }
}
