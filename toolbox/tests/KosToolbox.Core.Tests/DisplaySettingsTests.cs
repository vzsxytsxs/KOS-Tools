using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

public class DisplaySettingsTests
{
    [Fact]
    public void NeutralRampIsIdentity()
    {
        var ramp = DisplaySettings.BuildRamp(ChannelAdjust.Neutral);
        for (var i = 0; i < 256; i++) Assert.InRange(ramp[i], i * 257 - 1, i * 257 + 1);
    }

    [Theory]
    [InlineData(80, 50, 1.0)]
    [InlineData(50, 90, 1.0)]
    [InlineData(20, 20, 2.2)]
    public void RampStaysInRangeAndNeverDecreases(int brightness, int contrast, double gamma)
    {
        var ramp = DisplaySettings.BuildRamp(new ChannelAdjust(brightness, contrast, gamma));
        for (var i = 1; i < 256; i++) Assert.True(ramp[i] >= ramp[i - 1]);
    }

    [Fact]
    public void BrightnessRaisesTheRamp()
    {
        var dark = DisplaySettings.BuildRamp(new ChannelAdjust(40, 50, 1.0));
        var bright = DisplaySettings.BuildRamp(new ChannelAdjust(60, 50, 1.0));
        Assert.True(bright[128] > dark[128]);
    }
}
