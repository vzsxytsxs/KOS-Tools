using KosToolbox.Core.Display;
using Xunit;

namespace KosToolbox.Core.Tests;

public class EdidTests
{
    /// <summary>Synthetic EDID: 1920×1080@60 (CEA) in the base block, name, 48–75 Hz limits and an empty CTA extension.</summary>
    static byte[] Sample()
    {
        var b = new byte[256];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(b, 0);
        b[8] = 0x10; b[9] = 0xAC; // "DEL"
        b[10] = 0x34; b[11] = 0x12;
        b[17] = 30; b[18] = 1; b[19] = 4; b[21] = 60; b[22] = 34;
        new DetailedTiming(148_500, 1920, 280, 88, 44, 1080, 45, 4, 5, VPositive: true).Encode().CopyTo(b, 54);
        new byte[] { 0, 0, 0, 0xFD, 0, 48, 75, 30, 160, 17, 0, 0x0A, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 }.CopyTo(b, 72);
        new byte[] { 0, 0, 0, 0xFC, 0 }.Concat("TEST MON\n    "u8.ToArray()).ToArray().CopyTo(b, 90);
        new byte[] { 0, 0, 0, 0x10 }.CopyTo(b, 108);
        b[126] = 1;
        b[128] = 0x02; b[129] = 0x03; b[130] = 4; b[131] = 0x00;
        return b;
    }

    [Fact]
    public void ReadsBaseBlock()
    {
        var e = Edid.Parse(Sample());
        Assert.Equal("DEL", e.Manufacturer);
        Assert.Equal(0x1234, e.ProductCode);
        Assert.Equal("TEST MON", e.Name);
        Assert.Equal(new RangeLimits(48, 75, 30, 160, 170), e.Range);
        var native = Assert.Single(e.Timings);
        Assert.True(native.Native);
        Assert.Equal(60.0, native.Timing.RefreshHz, 3);
        Assert.True(e.HasCta);
        Assert.False(e.BasicAudio);
    }

    [Fact]
    public void TimingRoundTripsThroughEighteenBytes()
    {
        var t = new DetailedTiming(533_250, 2560, 160, 48, 32, 1440, 41, 3, 5, false, true, false, 597, 336);
        Assert.Equal(t, DetailedTiming.Decode(t.Encode()));
    }

    [Fact]
    public void CvtReducedBlankingMatchesVesaTables()
    {
        var t = DetailedTiming.CvtReducedBlanking(1920, 1080, 60);
        Assert.Equal((138_500, 2080, 1111, 5), (t.PixelClockKhz, t.HTotal, t.VTotal, t.VSync));
        var u = DetailedTiming.CvtReducedBlanking(2560, 1440, 60);
        Assert.Equal((241_500, 2720, 1481), (u.PixelClockKhz, u.HTotal, u.VTotal));
    }

    [Fact]
    public void SameTimingNewRefreshKeepsTotals()
    {
        var native = DetailedTiming.Decode(Sample().AsSpan(54, 18))!;
        var t = native.WithRefresh(75);
        Assert.Equal((2200, 1125), (t.HTotal, t.VTotal));
        Assert.Equal(185_630, t.PixelClockKhz);
    }

    [Fact]
    public void EditsKeepChecksumsAndCanBeUndone()
    {
        var e = Edid.Parse(Sample());
        e.AddTiming(DetailedTiming.CvtReducedBlanking(1920, 1080, 75));
        e.SetVerticalRange(40, 280);
        e.EnableBasicAudio();
        var bytes = e.ToBytes();
        Assert.True(Edid.ChecksumsValid(bytes));

        var again = Edid.Parse(bytes);
        Assert.Equal(new RangeLimits(40, 280, 30, 160, 170), again.Range);
        Assert.True(again.BasicAudio && again.HasAudioBlock);
        var added = again.Timings.Single(t => t.Block == 1);
        Assert.Equal(75.0, added.Timing.RefreshHz, 0);
        again.RemoveTiming(added);
        Assert.Single(again.Timings);
        Assert.Throws<InvalidOperationException>(() => again.RemoveTiming(again.Timings[0])); // the native one stays
    }

    [Fact]
    public void CvtAndLcdStandardMatchTheStandards()
    {
        var cvt = DetailedTiming.Cvt(1920, 1080, 60);
        Assert.Equal((173_000, 2576, 1120, 128, 200), (cvt.PixelClockKhz, cvt.HTotal, cvt.VTotal, cvt.HFront, cvt.HSync));
        var std = DetailedTiming.Generate(TimingMode.LcdStandard, 1920, 1080, 60, null);
        Assert.Equal((148_500, 2200, 1125, 88, 44), (std.PixelClockKhz, std.HTotal, std.VTotal, std.HFront, std.HSync));
        var exact = DetailedTiming.Generate(TimingMode.ExactReduced, 1920, 1080, 75, null);
        Assert.Equal(75.0, exact.RefreshHz, 2);
    }

    [Fact]
    public void AudioFormatsAddAndRemoveWithoutBreakingTimings()
    {
        var e = Edid.Parse(Sample());
        var extra = DetailedTiming.CvtReducedBlanking(1920, 1080, 75);
        e.AddTiming(extra);
        e.SetAudioFormat(Edid.AudioAc3, true);
        e.SetAudioFormat(Edid.AudioTrueHd, true);
        var again = Edid.Parse(e.ToBytes());
        Assert.True(again.BasicAudio);
        Assert.Equal(new[] { Edid.AudioLpcm, Edid.AudioAc3, Edid.AudioTrueHd }, again.AudioFormats);
        Assert.Equal(extra, again.Timings.Single(t => t.Block == 1).Timing); // the timing moved down along with the data blocks

        again.SetAudioFormat(Edid.AudioAc3, false);
        Assert.Equal(new[] { Edid.AudioLpcm, Edid.AudioTrueHd }, again.AudioFormats);
        again.DisableAudio();
        Assert.False(again.BasicAudio || again.HasAudioBlock);
        Assert.Equal(extra, again.Timings.Single(t => t.Block == 1).Timing);
        Assert.True(Edid.ChecksumsValid(again.ToBytes()));
    }

    [Fact]
    public void RangeTurnsOffAndBackOn()
    {
        var e = Edid.Parse(Sample());
        e.RemoveRange();
        Assert.Null(e.Range);
        e.SetRange(48, 144, 600);
        var r = Edid.Parse(e.ToBytes()).Range!;
        Assert.Equal((48, 144, 600), (r.MinVHz, r.MaxVHz, r.MaxPixelClockMhz));
        Assert.True(r.MinHKhz <= 30 && r.MaxHKhz >= 255);
    }

    [Fact]
    public void EditedTimingCanBecomePreferred()
    {
        var e = Edid.Parse(Sample());
        var oldNative = e.NativeTiming;
        var t = DetailedTiming.CvtReducedBlanking(1920, 1080, 75);
        e.AddTiming(t);
        e.MakePreferred(e.Find(t)!);
        Assert.Equal(t, e.NativeTiming);
        Assert.Equal(oldNative, e.Timings.Single(x => x.Block == 1).Timing);
        var edited = t.WithRefresh(70);
        e.ReplaceTiming(e.Timings.Single(x => x.Block == 1), edited);
        Assert.Equal(edited, e.Timings.Single(x => x.Block == 1).Timing);
        Assert.Equal("DEL1234", e.ProductId);
        Assert.Equal(5, e.FreeSlots);
    }

    [Fact]
    public void RejectsInvalidData()
    {
        Assert.Throws<FormatException>(() => Edid.Parse(new byte[128]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Edid.Parse(Sample()).SetVerticalRange(80, 60));
        Assert.NotNull(new DetailedTiming(700_000, 1920, 160, 48, 32, 1080, 31, 3, 5).Problem());
    }
}
