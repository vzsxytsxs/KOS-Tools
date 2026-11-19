using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

public class FanCurveTests
{
    static readonly FanPoint[] Curve = { new(40, 40), new(60, 60), new(80, 100) };

    [Fact]
    public void SourceGeneratedJsonKeepsTheFileFormat()
    {
        // JsonFile's reflection options, which wrote fan-curve.json and gpu-tuning.json before KOS Engine went AOT.
        var reflection = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var curve = new GovernorFanCurve { Enabled = true, BusId = "00000000:01:00.0", Points = Curve.ToList(), Hysteresis = 4 };
        var state = new GovernorFanCurve.State { Running = true, BusId = curve.BusId, Error = null };
        var tuning = new GpuTuning
        {
            PciBusId = "00000000:01:00.0", PowerLimitMw = 300000, TemperatureLimitC = 83, CoreOffsetMhz = 150, MemOffsetMhz = 500,
            CurveChanges = new() { new GpuVfChange(10, 850000, 15000) }, ClockLockMhz = 2700, FanPercent = 60,
        };
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(curve, reflection), System.Text.Json.JsonSerializer.Serialize(curve, Persistence.KosJson.Default.GovernorFanCurve));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(state, reflection), System.Text.Json.JsonSerializer.Serialize(state, Persistence.KosJson.Default.State));
        var saved = System.Text.Json.JsonSerializer.Serialize(tuning, reflection);
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Serialize(tuning, Persistence.KosJson.Default.GpuTuning));
        Assert.Equal(saved, System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Deserialize(saved, Persistence.KosJson.Default.GpuTuning)!, reflection));
    }

    [Theory]
    [InlineData(20, 40)]   // below the first point
    [InlineData(40, 40)]
    [InlineData(50, 50)]   // halfway
    [InlineData(70, 80)]
    [InlineData(95, 100)]  // above the last point
    public void InterpolatesLinearly(int temp, int percent) => Assert.Equal(percent, FanCurve.Target(Curve, temp));

    [Fact]
    public void RisesAtOnceButDropsOnlyAfterHysteresis()
    {
        var (p, r) = FanCurve.Step(null, 0, 60, Curve, 3);
        Assert.Equal((60, 60), (p, r));
        (p, r) = FanCurve.Step(p, r, 70, Curve, 3);          // went up: applies right away
        Assert.Equal((80, 70), (p, r));
        (p, r) = FanCurve.Step(p, r, 68, Curve, 3);          // dropped only 2 °C: holds
        Assert.Equal((80, 70), (p, r));
        (p, r) = FanCurve.Step(p, r, 67, Curve, 3);          // dropped 3 °C: goes down
        Assert.Equal((74, 67), (p, r));
    }

    [Fact]
    public void ValidatesShapeRangeAndSafety()
    {
        Assert.Null(FanCurve.Problem(Curve, 3, 40, 100));
        Assert.NotNull(FanCurve.Problem(new[] { new FanPoint(50, 50) }, 3, 40, 100));                            // a single point
        Assert.NotNull(FanCurve.Problem(new[] { new FanPoint(60, 50), new FanPoint(50, 90) }, 3, 40, 100));      // temperature out of order
        Assert.NotNull(FanCurve.Problem(new[] { new FanPoint(40, 90), new FanPoint(80, 50) }, 3, 40, 100));      // speed going down
        Assert.NotNull(FanCurve.Problem(new[] { new FanPoint(40, 30), new FanPoint(80, 100) }, 3, 40, 100));     // below the driver's minimum
        Assert.NotNull(FanCurve.Problem(new[] { new FanPoint(40, 40), new FanPoint(95, 100) }, 3, 40, 100));     // doesn't reach 80 % by 90 °C
        Assert.NotNull(FanCurve.Problem(Curve, 20, 40, 100));                                                   // hysteresis too large
        Assert.Null(FanCurve.Problem(new[] { new FanPoint(40, 40), new FanPoint(85, 70) }, 3, 40, 70));          // card with a 70 % maximum
    }
}
