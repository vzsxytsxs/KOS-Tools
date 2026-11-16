using KosToolbox.Core.Display;
using KosToolbox.Core.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KosToolbox.Core.Tests;

/// <summary>CRU-style sections: established, standard, base descriptors, extension blocks and CTA-861 data blocks.</summary>
public class EdidEditorTests(ITestOutputHelper output)
{
    /// <summary>1920x1080@60 preferred, a name, a free descriptor, 640x480@60 + 1024x768@60 established, two standard timings and a CTA block
    /// with a video block (VIC 16 native, VIC 4), speakers (stereo) and one detailed resolution.</summary>
    static byte[] Sample()
    {
        var b = new byte[256];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(b, 0);
        b[8] = 0x10; b[9] = 0xAC; b[18] = 1; b[19] = 4;
        b[35] = 0x20; b[36] = 0x08;
        for (var i = 38; i < 54; i++) b[i] = 1;
        b[38] = 129; b[39] = 0x40;           // 1280x1024 (5:4) would be 0x80: this is 1280x960 (4:3) @ 60
        b[40] = 0xD1; b[41] = 0xC0;          // 1920x1080 (16:9) @ 60
        new DetailedTiming(148_500, 1920, 280, 88, 44, 1080, 45, 4, 5, VPositive: true).Encode().CopyTo(b, 54);
        new byte[] { 0, 0, 0, 0xFC, 0 }.Concat("TEST MON\n    "u8.ToArray()).ToArray().CopyTo(b, 72);
        new byte[] { 0, 0, 0, 0x10 }.CopyTo(b, 90);
        new byte[] { 0, 0, 0, 0x10 }.CopyTo(b, 108);
        b[126] = 1;
        byte[] cta = { 0x02, 0x03, 0, 0x41, 0x42, 0x90, 0x04, 0x83, 0x01, 0x00, 0x00 };
        cta.CopyTo(b, 128);
        b[130] = (byte)(4 + 3 + 4);
        new DetailedTiming(74_250, 1280, 370, 110, 40, 720, 30, 5, 5).Encode().CopyTo(b, 128 + b[130]);
        return Edid.Parse(b).ToBytes();
    }

    [Fact]
    public void EstablishedAndStandardResolutionsReadAndWrite()
    {
        var e = Edid.Parse(Sample());
        Assert.True(e.Established(2));   // 640x480 @ 60
        Assert.True(e.Established(12));  // 1024x768 @ 60
        Assert.False(e.Established(15));
        e.SetEstablished(15, true);
        e.SetEstablished(2, false);
        Assert.True(e.Established(15));
        Assert.False(e.Established(2));

        Assert.Equal(new[] { new StandardTiming(1280, 960, 60), new StandardTiming(1920, 1080, 60) }, e.StandardTimings);
        var list = e.StandardTimings.Append(new StandardTiming(1440, 900, 75)).ToList();
        e.SetStandardTimings(list);
        var again = Edid.Parse(e.ToBytes());
        Assert.Equal(list, again.StandardTimings);
        Assert.Equal("16:10", again.StandardTimings[2].Ratio);
        Assert.NotNull(new StandardTiming(1366, 768, 60).Problem());   // not a multiple of 8, not one of the ratios
        Assert.NotNull(new StandardTiming(1920, 1080, 144).Problem()); // above 123 Hz
        Assert.Throws<InvalidOperationException>(() => e.SetStandardTimings(Enumerable.Repeat(new StandardTiming(800, 600, 60), 9).ToList()));
    }

    [Fact]
    public void BaseDescriptorsAddClearAndKeepThePreferred()
    {
        var e = Edid.Parse(Sample());
        Assert.Equal(new[] { DescriptorKind.Timing, DescriptorKind.Name, DescriptorKind.Dummy, DescriptorKind.Dummy }, e.Descriptors.Select(d => d.Kind));
        Assert.Equal("TEST MON", e.Descriptors[1].Text);
        Assert.Equal(2, e.FreeDescriptors);
        var t = DetailedTiming.CvtReducedBlanking(2560, 1440, 144);
        e.AddBaseTiming(t);
        Assert.Equal(t, e.Descriptors[2].Timing);
        Assert.Throws<InvalidOperationException>(() => e.ClearDescriptor(54));
        e.ClearDescriptor(72);
        Assert.Equal(2, e.FreeDescriptors);
        e.AddBaseTiming(t);
        e.AddBaseTiming(t);
        Assert.Throws<InvalidOperationException>(() => e.AddBaseTiming(t));
        Assert.True(Edid.ChecksumsValid(e.ToBytes()));
    }

    [Fact]
    public void CtaBlockParsesDataBlocksAndTimingsAndRebuildsTheSameBytes()
    {
        var bytes = Sample();
        var cta = new CtaBlock(bytes.AsSpan(128, 128).ToArray());
        Assert.Equal(new[] { CtaKind.Video, CtaKind.Speaker }, cta.DataBlocks.Select(d => d.Kind));
        Assert.Equal(new[] { new VideoEntry(16, true), new VideoEntry(4, false) }, CtaFormats.ReadVideo(cta.DataBlocks[0]));
        Assert.Equal("Stereo", cta.DataBlocks[1].Summary);
        Assert.Single(cta.Timings);
        Assert.True(cta.BasicAudio);
        Assert.Equal(1, cta.NativeCount);
        Assert.Equal(bytes.AsSpan(128, 127).ToArray(), cta.ToBytes().AsSpan(0, 127).ToArray());

        cta.AddDataBlock(CtaFormats.BuildColorimetry(0x80 | 0x8000)); // BT.2020 RGB + DCI-P3
        cta.AddDataBlock(CtaFormats.BuildHdr(new CtaFormats.HdrStatic(0b0101, true, 115, 90, 30)));
        cta.MoveDataBlock(3, -1);
        Assert.Equal(new[] { CtaKind.Video, CtaKind.Speaker, CtaKind.HdrStatic, CtaKind.Colorimetry }, cta.DataBlocks.Select(d => d.Kind));
        var rebuilt = new CtaBlock(cta.ToBytes());
        Assert.Equal(0x8080, CtaFormats.ReadColorimetry(rebuilt.DataBlocks[3]));
        Assert.Equal(new CtaFormats.HdrStatic(0b0101, true, 115, 90, 30), CtaFormats.ReadHdr(rebuilt.DataBlocks[2]));
        Assert.Equal(rebuilt.Timings, cta.Timings);

        // A timing that doesn't fit is refused and the block stays as it was.
        var full = new CtaBlock(cta.ToBytes());
        for (var i = 0; full.FreeBytes >= 18; i++) full.AddTiming(DetailedTiming.CvtReducedBlanking(1920, 1080, 60 + i));
        var before = full.ToBytes();
        Assert.Throws<InvalidOperationException>(() => full.AddTiming(DetailedTiming.CvtReducedBlanking(1920, 1080, 75)));
        Assert.Equal(before, full.ToBytes());
    }

    [Fact]
    public void ExtensionBlocksAddRemoveAndReplace()
    {
        var e = Edid.Parse(Sample());
        Assert.Equal("CTA-861: 1 detailed resolution, 2 data blocks", Assert.Single(e.ExtensionList).Text);
        e.AddCtaBlock();
        e.AddCtaBlock();
        Assert.Throws<InvalidOperationException>(() => e.AddCtaBlock());
        Assert.Equal("CTA-861: empty", e.ExtensionList[2].Text);
        var cta = new CtaBlock(e.Block(3));
        cta.AddDataBlock(CtaFormats.BuildSpeakers(CtaFormats.Surround71));
        e.SetBlock(3, cta.ToBytes());
        var bytes = e.ToBytes();
        Assert.Equal(512, bytes.Length);
        Assert.True(Edid.ChecksumsValid(bytes));
        Assert.Equal(3, bytes[126]);
        e.RemoveBlock(2);
        Assert.Equal("7.1 surround", new CtaBlock(Edid.Parse(e.ToBytes()).Block(2)).DataBlocks[0].Summary);
    }

    [Fact]
    public void VendorBlocksKeepWhatTheEditorDoesNotChange()
    {
        // HDMI 1.4b block with latency bytes after the TMDS clock: they stay.
        var hdmi = new CtaDataBlock(new byte[] { 0x6A, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x38, 0x3C, 0x80, 0x0B, 0x0B });
        var h = CtaFormats.ReadHdmi(hdmi);
        Assert.Equal(new CtaFormats.HdmiSupport("1.0.0.0", false, false, true, true, true, 300), h);
        var edited = CtaFormats.BuildHdmi(hdmi, h with { MaxTmdsMhz = 340, Dc36 = false });
        Assert.Equal(new byte[] { 0x6A, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x18, 0x44, 0x80, 0x0B, 0x0B }, edited.Raw);
        Assert.Throws<ArgumentException>(() => CtaFormats.BuildHdmi(hdmi, h with { PhysicalAddress = "1.0.0" }));

        var forum = CtaFormats.BuildHdmiForum(null, new CtaFormats.HdmiForum(600, true, true, false, false, false, 48, 240));
        Assert.Equal(CtaKind.HdmiForum, forum.Kind);
        Assert.Equal(new CtaFormats.HdmiForum(600, true, true, false, false, false, 48, 240), CtaFormats.ReadHdmiForum(forum));

        var free = CtaFormats.BuildFreeSync(null, 48, 144);
        Assert.Equal(CtaKind.FreeSync, free.Kind);
        Assert.Equal((48, 144), CtaFormats.ReadFreeSync(free));
        Assert.Throws<ArgumentOutOfRangeException>(() => CtaFormats.BuildFreeSync(free, 48, 300)); // short block: 8-bit max

        var cap = new CtaFormats.VideoCapability(true, false, 0, 2, 3);
        Assert.Equal(cap, CtaFormats.ReadVideoCapability(CtaFormats.BuildVideoCapability(cap)));
        Assert.Equal(new byte[] { 0x01, 0x02, 0xFF }, CtaFormats.ParseHex("01 02-ff"));
        Assert.Null(CtaFormats.ParseHex("1 2"));
        Assert.Equal("0A FF", CtaFormats.Hex(new byte[] { 0x0A, 0xFF }));
    }

    [Theory]
    [InlineData(false, DisplaySettings.ScalingAspect, 6)]
    [InlineData(false, DisplaySettings.ScalingStretched, 1)]
    [InlineData(false, DisplaySettings.ScalingCentered, 7)]
    [InlineData(true, DisplaySettings.ScalingAspect, 5)]
    [InlineData(true, DisplaySettings.ScalingStretched, 2)]
    [InlineData(true, DisplaySettings.ScalingCentered, 3)]
    [InlineData(true, DisplaySettings.ScalingInteger, 8)]
    public void NvidiaScalingMatchesTheControlPanel(bool gpu, uint mode, int nv)
    {
        Assert.Equal(nv, DisplaySettings.ToNvScaling(gpu, mode));
        Assert.Equal((gpu, mode), DisplaySettings.FromNvScaling(nv));
    }

    [Fact]
    public void ScalingOverrideIsBitFourWithTheChecksumRebuilt()
    {
        // Read on the maintainer's PC with the panel's box off, then on.
        var off = Convert.FromHexString("DB0200001000000000010000EE000000");
        var on = Convert.FromHexString("DB0200001000000010010000FE000000");
        Assert.Equal(on, DisplaySettings.WithScalingOverride(off, true));
        Assert.Equal(off, DisplaySettings.WithScalingOverride(on, false));
        Assert.Null(DisplaySettings.WithScalingOverride(off.Take(12).ToArray(), true));
        var bad = off.ToArray(); bad[12] = 0;
        Assert.Null(DisplaySettings.WithScalingOverride(bad, true)); // wrong checksum: unknown layout, nothing written
        Monitors.Validate(new DisplayJob { ScalingOverrideProduct = "SAM727E", ScalingOverride = true });
        Assert.Throws<InvalidOperationException>(() => Monitors.Validate(new DisplayJob { ScalingOverrideProduct = @"..\x" }));
        Assert.Throws<InvalidOperationException>(() => Monitors.Validate(new DisplayJob { ScalingOverrideProduct = "SAM727E", RestartAdapters = true }));
    }

    /// <summary>Every CTA block of this PC's monitors parses and rebuilds to the same bytes (nothing is written).</summary>
    [Fact]
    public void ThisPcsExtensionBlocksRoundTrip()
    {
        foreach (var m in Monitors.List())
        {
            Edid e;
            try { e = Edid.Parse(m.Effective); } catch (FormatException) { continue; }
            foreach (var x in e.ExtensionList.Where(x => x.IsCta))
            {
                var block = e.Block(x.Index);
                var cta = new CtaBlock(block);
                output.WriteLine($"{m.InstanceId} block {x.Index}: {x.Text}; {string.Join(" | ", cta.DataBlocks.Select(d => $"{d.Name} {d.Summary}"))}");
                Assert.Equal(block.AsSpan(0, 127).ToArray(), cta.ToBytes().AsSpan(0, 127).ToArray());
            }
            output.WriteLine($"  standard: {string.Join(", ", e.StandardTimings)}; descriptors: {string.Join(", ", e.Descriptors.Select(d => d.Kind))}");
        }
        foreach (var t in DisplaySettings.Targets())
            output.WriteLine($"{t.Name} ({t.Product}): NV scaling {DisplaySettings.ReadNvScaling(t.GdiName)?.ToString() ?? "n/a"}, override {DisplaySettings.ReadScalingOverride(t.Product)?.ToString() ?? "n/a"}");
    }
}
