using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

public class VfCurveEditTests
{
    const int Min = -1_000_000, Max = 1_000_000;

    // Stock curve like Afterburner's: rises up to point 4 and stays flat at 1950 MHz.
    static readonly GpuVfPoint[] Points =
    {
        new(0, 700_000, 1_365_000, 0), new(1, 800_000, 1_600_000, 0), new(2, 900_000, 1_800_000, 0),
        new(3, 1_000_000, 1_900_000, 0), new(4, 1_043_000, 1_950_000, 0), new(5, 1_100_000, 1_950_000, 0), new(6, 1_200_000, 1_950_000, 0),
    };

    static Dictionary<int, int> Zero() => Points.ToDictionary(p => p.Index, _ => 0);
    static long Freq(Dictionary<int, int> d, int i) => Points[i].FrequencyKhz + d[i];

    [Fact]
    public void RaisingAPointFlattensThePointsAfterIt()
    {
        var d = VfCurveEdit.Move(Points, Zero(), new HashSet<int> { 4 }, 256_000, Min, Max);
        Assert.Equal(2_206_000, Freq(d, 4));
        Assert.Equal(2_206_000, Freq(d, 5));
        Assert.Equal(2_206_000, Freq(d, 6));
        Assert.Equal(0, d[3]); // to the left it doesn't change
    }

    [Fact]
    public void LoweringAPointLeavesTheOthersAlone()
    {
        var d = VfCurveEdit.Move(Points, Zero(), new HashSet<int> { 4 }, -30_000, Min, Max);
        Assert.Equal(-30_000, d[4]); // 1920, still above point 3
        Assert.Equal(0, d[5]);
        Assert.Equal(0, d[3]);
    }

    [Fact]
    public void PointsLoweredBelowAnEarlierPointStayAtItsLevel()
    {
        // As in Afterburner: pulling the end of the curve below point 3 leaves everything flat at 1900 MHz.
        var d = VfCurveEdit.Move(Points, Zero(), new HashSet<int> { 4, 5, 6 }, -300_000, Min, Max);
        Assert.All(new[] { 4, 5, 6 }, i => Assert.Equal(1_900_000, Freq(d, i)));
        Assert.Equal(0, d[3]);
    }

    [Fact]
    public void FlattenRaisesOnlyWhatDropsBelowAnEarlierPoint()
    {
        var lowered = Zero();
        foreach (var i in new[] { 2, 3, 4, 5, 6 }) lowered[i] = -200_000;
        lowered[3] = 0; // Reset of point 3 only: the following ones can't stay below it
        var d = VfCurveEdit.Flatten(Points, lowered, Min, Max);
        Assert.Equal(-200_000, d[2]);
        Assert.Equal(1_900_000, Freq(d, 4));
        Assert.Equal(1_900_000, Freq(d, 6));
    }

    [Fact]
    public void TheSelectionMovesTogetherAndStopsAtTheDriverLimit()
    {
        var start = Zero();
        start[3] = 200_000;
        var d = VfCurveEdit.Move(Points, start, new HashSet<int> { 2, 3 }, 900_000, Min, 300_000);
        Assert.Equal(300_000, d[3]); // reached the maximum
        Assert.Equal(100_000, d[2]); // moved by the same amount (100 MHz)
    }

    [Fact]
    public void TheFirstPointAndAZeroMoveChangeNothing()
    {
        Assert.Equal(Zero(), VfCurveEdit.Move(Points, Zero(), new HashSet<int> { 0 }, 50_000, Min, Max));
        Assert.Equal(Zero(), VfCurveEdit.Move(Points, Zero(), new HashSet<int> { 3 }, 0, Min, Max));
    }

    [Fact]
    public void ClockLockIsValidated()
    {
        var supported = new GpuState { BusId = "00000000:04:00.0", ClockLockMhz = 0 };
        Assert.Null(GpuTuner.Problem(new GpuTuning { PciBusId = supported.BusId, ClockLockMhz = 1950 }, supported));
        Assert.Null(GpuTuner.Problem(new GpuTuning { PciBusId = supported.BusId, ClockLockMhz = 0 }, supported));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = supported.BusId, ClockLockMhz = 50 }, supported));
        Assert.NotNull(GpuTuner.Problem(new GpuTuning { PciBusId = supported.BusId, ClockLockMhz = 1950 },
            new GpuState { BusId = supported.BusId, ClockLockProblem = "not exported" }));
    }
}
