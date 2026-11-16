using KosToolbox.Core.Latency;
using Xunit;

namespace KosToolbox.Core.Tests;

public class MouseTestTests
{
    // A 1000 Hz mouse with one late report (2 ms) and one early one (0.5 ms).
    static readonly MouseSample[] Run =
    {
        new(0, 2, 0), new(1, 2, 1), new(2, 3, 0), new(4, 2, -1), new(4.5, 1, 0), new(5.5, 2, 0),
    };

    [Fact]
    public void StatisticsComeFromTheIntervals()
    {
        var s = MouseTest.Stats(Run)!;
        Assert.Equal(6, s.Events);
        Assert.Equal(5.5, s.DurationMs);
        Assert.Equal(12, s.SumX);
        Assert.Equal(0, s.SumY);
        Assert.Equal(1, s.MedianMs); // intervals 1, 1, 2, 0.5, 1
        Assert.Equal(1000, s.RateHz);
        Assert.Equal(0.5, s.MinMs);
        Assert.Equal(2, s.MaxMs);
        Assert.Equal(60, s.StablePercent, 3);
        Assert.Null(MouseTest.Stats(Run[..1]));
    }

    [Fact]
    public void GraphsAndCpi()
    {
        var interval = MouseTest.Series(Run, MouseGraph.Interval, null).Single();
        Assert.Equal(5, interval.Count);
        Assert.Equal((4.0, 2.0), interval[2]);
        Assert.Equal(2, MouseTest.Series(Run, MouseGraph.XYCounts, null).Count);
        var path = MouseTest.Series(Run, MouseGraph.XvsY, null).Single();
        Assert.Equal((12.0, -0.0), path[^1]);
        // 800 counts over 2.54 cm = 800 CPI; with it, 1 count/ms is 0.03175 m/s.
        Assert.Equal(800, MouseTest.Cpi(new[] { new MouseSample(0, 800, 3) }, 2.54));
        Assert.Equal(0.03175, MouseTest.Series(new[] { new MouseSample(0, 1, 0), new MouseSample(1, 1, 0) }, MouseGraph.XVelocity, 800).Single()[0].Y, 6);
    }

    [Fact]
    public void CsvRoundTripsAndReadsMouseTesterColumns()
    {
        var (back, cpi) = MouseTest.FromCsv(MouseTest.ToCsv(Run, 1600));
        Assert.Equal(Run, back);
        Assert.Equal(1600, cpi);
        var (other, none) = MouseTest.FromCsv("MouseTester\r\nTime (ms),xCount,yCount,buttonflags\r\n0.25,3,-2,0\r\n");
        Assert.Equal(new MouseSample(0.25, 3, -2), other.Single());
        Assert.Null(none);
        Assert.Throws<FormatException>(() => MouseTest.FromCsv("a,b,c\n1,2,3"));
    }
}
