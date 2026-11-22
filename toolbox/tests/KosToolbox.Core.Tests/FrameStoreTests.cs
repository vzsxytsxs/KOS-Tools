using System.Globalization;
using KosToolbox.Core.Latency;
using Xunit;

namespace KosToolbox.Core.Tests;

public class FrameStatsTests
{
    static List<double> Frames(params (int Count, double Ms)[] runs) => runs.SelectMany(r => Enumerable.Repeat(r.Ms, r.Count)).ToList();

    [Fact]
    public void ComputesLowsBothWays()
    {
        // 980 × 5 ms, 10 × 10 ms, 10 × 30 ms: the 99th percentile frame is 10 ms (100 FPS), the worst 1 % (10 frames) average 30 ms (33.3 FPS).
        var s = FrameStats.Of(Frames((980, 5), (10, 10), (10, 30)))!;
        Assert.Equal(1000, s.Frames);
        Assert.Equal(5.3, s.Seconds, 6);
        Assert.Equal(1000 * 1000 / 5300.0, s.AverageFps, 6);
        Assert.Equal(5, s.MedianMs);
        Assert.Equal(10, s.P99Ms);
        Assert.Equal(30, s.P999Ms);
        Assert.Equal(100, s.P1LowFps, 6);
        Assert.Equal(1000 / 30.0, s.Worst1Fps, 6);
        Assert.Equal(1000 / 30.0, s.P01LowFps, 6);
        Assert.Equal(1000 / 30.0, s.Worst01Fps, 6);
        Assert.Equal(30, s.MaxMs);
        Assert.Equal(10, s.Stutters); // over 2 × 5 ms: only the 30 ms frames (10 ms isn't over 10)
        Assert.Equal(0.01, s.Over16Share, 9);
        Assert.Equal(0.02, s.Over8Share, 9);
    }

    [Fact]
    public void MatchesTheCaptureParserAndComputesDeviation()
    {
        // FrameCaptureTests' data: 990 × 5 ms and 10 × 20 ms; mean 5.15, variance (990 × 0.15² + 10 × 14.85²) / 1000 = 2.2275.
        var s = FrameStats.Of(Frames((990, 5), (10, 20)))!;
        Assert.Equal(200, s.P1LowFps, 6);
        Assert.Equal(50, s.P01LowFps, 6);
        Assert.Equal(50, s.Worst1Fps, 6);
        Assert.Equal(Math.Sqrt(2.2275), s.StdDevMs, 9);
        Assert.Null(FrameStats.Of(new[] { 5.0 }));
    }

    [Fact]
    public void FpsOverTimeUsesOneSecondWindows()
    {
        // 1 s at 10 ms (100 FPS), then 1 s at 20 ms (50 FPS), then 0.5 s at 10 ms.
        var fps = FrameStats.FpsOverTime(Frames((100, 10), (50, 20), (50, 10)));
        Assert.Equal(new[] { 0.5, 1.5, 2.5 }, fps.Select(p => p.Seconds));
        Assert.Equal(100, fps[0].Fps, 6);
        Assert.Equal(50, fps[1].Fps, 6);
        Assert.Equal(100, fps[2].Fps, 6);
    }

    [Fact]
    public void HistogramBinsAndWidths()
    {
        var h = FrameStats.Histogram(new[] { 1.0, 1.2, 2.5, 3.9 }, 1);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, h.Select(b => b.Ms));
        Assert.Equal(new[] { 0.5, 0.25, 0.25 }, h.Select(b => b.Share));
        // A spike past the 99.9th percentile counts in the last bin.
        var spiky = Frames((1000, 5), (1, 100));
        var one = FrameStats.Histogram(spiky, 1);
        Assert.Single(one);
        Assert.Equal(1, one[0].Share, 9);
        // Two captures share the same bins.
        var a = Frames((10, 4));
        var b = Frames((10, 8));
        Assert.Equal(5, FrameStats.Histogram(a, 1, new[] { a, b }).Count);
        Assert.Equal(0.1, FrameStats.BinWidth(new[] { (IReadOnlyList<double>)Frames((5, 4), (5, 8)) }), 9);   // 4 ms / 40
        Assert.Equal(0.25, FrameStats.BinWidth(new[] { (IReadOnlyList<double>)Frames((5, 2), (5, 12)) }), 9); // 10 ms / 40
    }

    [Fact]
    public void ThinningKeepsSpikesAndOrder()
    {
        var y = Enumerable.Range(0, 10_000).Select(i => i == 4321 ? 50.0 : i == 777 ? 1.0 : 5.0).ToList();
        var x = Enumerable.Range(0, 10_000).Select(i => (double)i).ToList();
        var thin = FrameStats.Thin(x, y, 100);
        Assert.InRange(thin.Count, 100, 200);
        Assert.Contains((4321.0, 50.0), thin);
        Assert.Contains((777.0, 1.0), thin);
        Assert.Equal(thin.OrderBy(p => p.X), thin);
        Assert.Equal(20, FrameStats.Thin(x.Take(20).ToList(), y, 100).Count); // short lines unchanged
    }

    [Fact]
    public void ComparisonMarksWhatIsWorse()
    {
        var reference = FrameStats.Of(Frames((100, 10)))!;   // 100 FPS
        var slower = FrameStats.Of(Frames((100, 11)))!;      // 90.9 FPS: 9.1 % fewer
        var close = FrameStats.Of(Frames((100, 10.2)))!;     // 2 % fewer: noise
        var table = FrameCompare.Table(new[] { reference, slower, close });
        var avg = table.First(r => r.Label == "Average");
        Assert.Null(avg.Cells[0].DiffPercent);
        Assert.Equal(100 * (1000 / 11.0 - 100) / 100, avg.Cells[1].DiffPercent!.Value, 6);
        Assert.True(avg.Cells[1].Worse);
        Assert.False(avg.Cells[2].Worse);
        var median = table.First(r => r.Label == "Median frame time");
        Assert.Equal(10, median.Cells[1].DiffPercent!.Value, 6);
        Assert.True(median.Cells[1].Worse); // higher frame time is worse
        var faster = FrameCompare.Table(new[] { slower, reference });
        Assert.False(faster.First(r => r.Label == "Average").Cells[1].Worse);
        Assert.False(faster.First(r => r.Label == "Median frame time").Cells[1].Worse);
    }
}

public sealed class FrameStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "kos-frames-" + Guid.NewGuid().ToString("N"));
    readonly FrameStore _store;

    public FrameStoreTests() => _store = new FrameStore(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static FrameReport Report(double ms = 5, int count = 100) =>
        FrameCapture.Parse("game.exe", new[] { "Application,ProcessID,SwapChainAddress,PresentMode,AllowsTearing,MsBetweenPresents" }
            .Concat(Enumerable.Range(0, count).Select(i => $"game.exe,1,0x1,Hardware: Independent Flip,1,{(i == 50 ? ms * 4 : ms).ToString(CultureInfo.InvariantCulture)}")))!;

    [Fact]
    public void SavesAndLoadsACapture()
    {
        var date = new DateTime(2026, 11, 22, 18, 5, 0);
        var c = FrameStore.FromReport(Report(), 30, date);
        Assert.Equal("game · 2026-11-22 18:05", c.Name);
        Assert.Equal(c.FrameTimesMs.Sum() / 1000, c.TimesS[^1], 9);
        _store.Save(c);
        Assert.False(File.Exists(Path.Combine(_dir, c.Id + ".json.tmp")));
        var (list, bad) = _store.LoadAll();
        Assert.Empty(bad);
        var back = Assert.Single(list);
        Assert.Equal(c.Id, back.Id);
        Assert.Equal(c.FrameTimesMs, back.FrameTimesMs);
        Assert.Equal(30, back.RequestedSeconds);
        Assert.Equal("Hardware: Independent Flip", back.PresentModes[0].Mode);
        Assert.Equal(1, back.TearingShare);
        Assert.Equal(c.Stats!.AverageFps, back.Stats!.AverageFps, 9);
        Assert.Equal(c.Stats.AverageFps, back.ToReport().AverageFps, 9);
    }

    [Fact]
    public void SkipsDamagedFilesAndIgnoresTheStoredId()
    {
        _store.Save(FrameStore.FromReport(Report(), 10, DateTime.Now));
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "few.json"), "{\"Version\":1,\"FrameTimesMs\":[5,5,5]}");
        File.WriteAllText(Path.Combine(_dir, "future.json"), "{\"Version\":9,\"FrameTimesMs\":[" + string.Join(",", Enumerable.Repeat("5", 20)) + "]}");
        File.WriteAllText(Path.Combine(_dir, "crafted.json"), "{\"Version\":1,\"Id\":\"..\\\\..\\\\x\",\"FrameTimesMs\":[" + string.Join(",", Enumerable.Repeat("5", 20)) + "]}");
        File.WriteAllText(Path.Combine(_dir, "modes.json"), "{\"Version\":1,\"PresentModes\":[null,{\"Share\":1}],\"FrameTimesMs\":[" + string.Join(",", Enumerable.Repeat("5", 20)) + "]}");
        var (list, bad) = _store.LoadAll();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, c => c.Id == "crafted");
        Assert.Empty(list.First(c => c.Id == "modes").PresentModes); // entries without a mode are dropped
        Assert.False(list.First(c => c.Id == "modes").ToReport().IndependentFlip);
        Assert.Equal(new[] { "broken.json", "few.json", "future.json" }, bad.Order());
    }

    [Fact]
    public void RenamesDeletesAndCapsTheFolder()
    {
        var c = FrameStore.FromReport(Report(), 10, DateTime.Now);
        _store.Save(c);
        _store.Rename(c, "  Before driver update  ");
        Assert.Equal("Before driver update", Assert.Single(_store.LoadAll().Captures).Name);
        Assert.Throws<ArgumentException>(() => _store.Rename(c, " "));
        _store.Delete(c);
        Assert.Empty(_store.LoadAll().Captures);

        for (var i = 0; i < FrameStore.MaxFiles; i++) File.WriteAllText(Path.Combine(_dir, $"f{i}.json"), "{}");
        Assert.Throws<InvalidOperationException>(() => _store.Save(FrameStore.FromReport(Report(), 10, DateTime.Now)));
        var existing = new SavedCapture { Id = "f0", FrameTimesMs = Enumerable.Repeat(5.0, 20).ToList() };
        _store.Save(existing); // an existing file is still rewritten
    }

    [Fact]
    public void ExportsCsvThatImportsAgainWhateverTheCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var c = FrameStore.FromReport(Report(4.25), 10, DateTime.Now);
            var csv = FrameStore.ToCsv(c);
            Assert.StartsWith("Frame,TimeInSeconds,MsBetweenPresents\r\n1,0.00425,4.25\r\n", csv);
            var file = Path.Combine(Directory.CreateDirectory(_dir).FullName, "export.csv");
            File.WriteAllText(file, csv);
            var back = _store.Import(file);
            Assert.Equal(c.FrameTimesMs, back.FrameTimesMs);
            Assert.Equal("export", back.Program); // no Application column: the file name
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void ImportsPresentMonCsvAndCaptureFiles()
    {
        Directory.CreateDirectory(_dir);
        var pm = Path.Combine(_dir, "pm.csv");
        File.WriteAllLines(pm, new[] { "Application,ProcessID,SwapChainAddress,MsBetweenPresents" }.Concat(Enumerable.Range(0, 20).Select(_ => "cs2.exe,4,0x9,6.5")));
        var a = _store.Import(pm);
        Assert.Equal("cs2.exe", a.Program);
        Assert.Equal(20, a.FrameTimesMs.Count);

        var json = Path.Combine(_dir, a.Id + ".json");
        var b = _store.Import(json);
        Assert.NotEqual(a.Id, b.Id); // a copy, not the same file
        Assert.Equal(2, _store.LoadAll().Captures.Count);

        var junk = Path.Combine(_dir, "junk.csv");
        File.WriteAllText(junk, "a,b\n1,2");
        Assert.Throws<FormatException>(() => _store.Import(junk));
    }
}
