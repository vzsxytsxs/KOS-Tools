using System.Globalization;
using KosToolbox.Core.Latency;
using Xunit;

namespace KosToolbox.Core.Tests;

public class FrameCaptureTests
{
    static IEnumerable<string> Csv(string header, IEnumerable<string> rows) => new[] { header }.Concat(rows);

    [Fact]
    public void ComputesFpsLowsAndPresentMode()
    {
        // 1000 frames: 990 at 5 ms (200 FPS), 10 spikes at 20 ms, on the game's swap chain; 50 frames of an overlay on another chain.
        var rows = Enumerable.Range(0, 1000).Select(i => $"game.exe,100,0x1,DXGI,0,0,1,Hardware: Independent Flip,{(i % 100 == 0 ? 20 : 5).ToString(CultureInfo.InvariantCulture)}")
            .Concat(Enumerable.Range(0, 50).Select(_ => "game.exe,100,0x2,DXGI,1,0,0,Composed: Flip,16.6"));
        var r = FrameCapture.Parse("game.exe", Csv("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,MsBetweenPresents", rows))!;
        Assert.Equal(1000, r.Frames);
        Assert.Equal(1000 * 1000 / (990 * 5.0 + 10 * 20), r.AverageFps, 3);
        Assert.Equal(200, r.Low1Fps, 3);    // the 99th percentile frame is still 5 ms (only 1 % are spikes)
        Assert.Equal(50, r.Low01Fps, 3);    // the 99.9th is a spike
        Assert.Equal(10, r.Spikes);
        Assert.True(r.IndependentFlip);
        Assert.Equal(1, r.TearingShare, 3);
    }

    [Fact]
    public void ReadsV2ColumnsAndSkipsMissingValues()
    {
        var rows = Enumerable.Range(0, 20).Select(i => i == 0 ? "a.exe,1,0x1,NA,Composed: Flip" : "a.exe,1,0x1,10,Composed: Flip");
        var r = FrameCapture.Parse("a.exe", Csv("Application,ProcessID,SwapChainAddress,FrameTime,PresentMode", rows))!;
        Assert.Equal(19, r.Frames);
        Assert.Equal(100, r.AverageFps, 3);
        Assert.False(r.IndependentFlip);
        Assert.Null(FrameCapture.Parse("a.exe", Csv("Application,PresentMode", rows)));
        Assert.Null(FrameCapture.Parse("a.exe", new[] { "Application,MsBetweenPresents" }));
    }

    [Fact]
    public void AcceptsOnlyProgramNames()
    {
        Assert.True(FrameCapture.ValidProgram("cs2.exe"));
        Assert.False(FrameCapture.ValidProgram(@"C:\x\cs2.exe"));
        Assert.False(FrameCapture.ValidProgram("a.exe\" --output_file x"));
        Assert.False(FrameCapture.ValidProgram("cs2"));
    }

    /// <summary>A real capture of a running program (KOS_TEST_FRAMES=its file name, administrator, PresentMon in bin\3\PresentMon).</summary>
    [Fact]
    public async Task CapturesLive()
    {
        if (Environment.GetEnvironmentVariable("KOS_TEST_FRAMES") is not { Length: > 0 } program) return;
        var r = await FrameCapture.CaptureAsync(program, 5);
        Assert.True(r.Frames >= 10);
        Assert.InRange(r.AverageFps, 1, 2000);
        Assert.NotEmpty(r.PresentModes);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kos-frames-live.txt"), $"{r.Frames} frames, {r.AverageFps:0.0} FPS, 1% {r.Low1Fps:0.0}, {string.Join(", ", r.PresentModes.Select(m => $"{m.Mode} {m.Share:P0}"))}");
    }
}
