using KosGovernor.Core;
using Xunit;

namespace KosGovernor.Core.Tests;

public class TrayBatteryIconTests
{
    const int Size = 32;
    static uint At(uint[] px, int x, int y) => px[y * Size + x];
    static uint Rgb(uint p) => p & 0xFFFFFF;

    // ring points just right of the top (filled first) and just left of it (filled last); the ring spans radius 11.8..15.2 px at 32 px
    const int TopRightX = 18, TopLeftX = 13, RingY = 2;

    [Fact]
    public void TheRingEmptiesClockwiseFromTheTop()
    {
        var half = TrayBatteryIcon.Render(Size, 50, TrayGlyph.Mouse, charging: false);
        Assert.Equal(0xFFFFFFu, Rgb(At(half, TopRightX, RingY)));  // 0..50 % (the right side) is lit
        Assert.Equal(0x5A5A5Au, Rgb(At(half, TopLeftX, RingY)));   // 50..100 % (the left side) is the track
        Assert.Equal(0xFFu, At(half, TopRightX, RingY) >> 24);     // fully covered
        Assert.Equal(0u, At(half, 0, 0));                          // the corners stay transparent
    }

    [Fact]
    public void LowAndNotChargingIsAmber()
    {
        var low = TrayBatteryIcon.Render(Size, 30, TrayGlyph.Headset, charging: false);
        Assert.Equal(0xFFFFFFu, Rgb(At(low, TopRightX, RingY)));
        low = TrayBatteryIcon.Render(Size, 15, TrayGlyph.Headset, charging: false);
        Assert.Equal(0xFFB020u, Rgb(At(low, TopRightX, RingY)));
        var charging = TrayBatteryIcon.Render(Size, 15, TrayGlyph.Headset, charging: true);
        Assert.Equal(0xFFFFFFu, Rgb(At(charging, TopRightX, RingY)));
        Assert.Contains(charging, p => Rgb(p) == 0xFFD400 && p >> 24 == 0xFF); // the bolt
    }

    [Fact]
    public void EveryGlyphDrawsSomethingInside()
    {
        foreach (var glyph in Enum.GetValues<TrayGlyph>())
        {
            var px = TrayBatteryIcon.Render(Size, 0, glyph, charging: false);
            var inside = 0;
            for (var y = 8; y < 24; y++)
                for (var x = 8; x < 24; x++)
                    if (At(px, x, y) >> 24 > 0x80) inside++;
            Assert.True(inside > 10, $"{glyph}: {inside} pixels");
        }
    }
}
