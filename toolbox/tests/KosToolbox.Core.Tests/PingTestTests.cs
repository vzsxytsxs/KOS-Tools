using KosToolbox.Core.Latency;
using Xunit;

namespace KosToolbox.Core.Tests;

public class PingTestTests
{
    [Fact]
    public void LossAndJitterSkipTheLostReplies()
    {
        // 10, lost, 14, 12, lost, lost, 20: jitter compares each reply with the last one that came back (4, 2, 8).
        double?[] rtts = { 10, null, 14, 12, null, null, 20 };
        var s = PingTest.Compute(rtts);
        Assert.Equal(7, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(3, s.Lost);
        Assert.Equal(300.0 / 7, s.LossPercent, 6);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(14, s.AvgMs);
        Assert.Equal(20, s.MaxMs);
        Assert.Equal(14.0 / 3, s.JitterMs!.Value, 6);
        Assert.Equal(8, s.MaxJitterMs);
        Assert.Equal(new double?[] { null, null, 4, 2, null, null, 8 }, PingTest.Jitter(rtts).ToArray());
    }

    [Fact]
    public void NoReplyMeansAllLostAndNoFigures()
    {
        var s = PingTest.Compute(new double?[] { null, null });
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AvgMs);
        Assert.Null(s.JitterMs);
        Assert.Equal(0, PingTest.Compute(Array.Empty<double?>()).LossPercent);
        Assert.Null(PingTest.Compute(new double?[] { 5 }).JitterMs); // one reply: nothing to compare with
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("example.com", true)]
    [InlineData("router", true)]
    [InlineData("", false)]
    [InlineData("example.com:27015", false)]
    [InlineData("http://example.com", false)]
    [InlineData("two words", false)]
    public void HostIsANameOrAnAddress(string host, bool valid) => Assert.Equal(valid, PingTest.ValidHost(host));
}
