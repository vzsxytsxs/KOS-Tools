using System.Text;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Display;

/// <summary>A timing from the EDID and where it is (block 0 = base; other = CTA-861 extension).</summary>
public sealed record EdidTiming(int Block, int Offset, DetailedTiming Timing, bool Native);

public sealed record RangeLimits(int MinVHz, int MaxVHz, int MinHKhz, int MaxHKhz, int MaxPixelClockMhz);

/// <summary>
/// EDID of a monitor (VESA E-EDID 1.4 + CTA-861 extension) with the edits CRU makes: add/remove timings,
/// vertical frequency range (VRR) and basic HDMI audio. Checksums and the extension count are rebuilt on save.
/// </summary>
public sealed partial class Edid
{
    static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
    static readonly int[] BaseSlots = { 54, 72, 90, 108 };
    readonly List<byte[]> _blocks;

    Edid(List<byte[]> blocks) => _blocks = blocks;

    public static Edid Parse(byte[] data)
    {
        if (data.Length < 128 || data.Length % 128 != 0 || !data.AsSpan(0, 8).SequenceEqual(Header)) throw new FormatException(Txt.EdidInvalid);
        var blocks = data.Chunk(128).Select(b => b.ToArray()).ToList();
        var extensions = blocks[0][126];
        return new Edid(blocks.Take(Math.Min(blocks.Count, 1 + extensions)).ToList());
    }

    public Edid Clone() => new(_blocks.Select(b => b.ToArray()).ToList());

    /// <summary>Bytes with the extension count and each block's checksum rebuilt.</summary>
    public byte[] ToBytes()
    {
        _blocks[0][126] = (byte)(_blocks.Count - 1);
        foreach (var b in _blocks) b[127] = (byte)(256 - b.Take(127).Sum(x => x) % 256);
        return _blocks.SelectMany(b => b).ToArray();
    }

    public static bool ChecksumsValid(byte[] data) => data.Length % 128 == 0 && data.Chunk(128).All(b => b.Sum(x => x) % 256 == 0);

    byte[] Base => _blocks[0];
    int? CtaIndex => _blocks.FindIndex(b => b[0] == 0x02) is var i and >= 0 ? i : null;

    // ---------- reading ----------
    public string Manufacturer
    {
        get
        {
            var v = Base[8] << 8 | Base[9];
            return new string(new[] { (char)('A' - 1 + (v >> 10 & 0x1F)), (char)('A' - 1 + (v >> 5 & 0x1F)), (char)('A' - 1 + (v & 0x1F)) });
        }
    }

    public int ProductCode => Base[10] | Base[11] << 8;
    public string Version => $"{Base[18]}.{Base[19]}";
    public int Year => Base[17] + 1990;
    public int WidthCm => Base[21];
    public int HeightCm => Base[22];
    public int Extensions => _blocks.Count - 1;
    public bool HasCta => CtaIndex is not null;
    public string? Name => Descriptor(0xFC) is { } d ? Encoding.ASCII.GetString(Base, d + 5, 13).Split('\n')[0].Trim() : null;

    /// <summary>Offset of the monitor descriptor with the requested tag in the base block.</summary>
    int? Descriptor(byte tag) => BaseSlots.Cast<int?>().FirstOrDefault(o => Base[o!.Value] == 0 && Base[o.Value + 1] == 0 && Base[o.Value + 3] == tag);

    public RangeLimits? Range
    {
        get
        {
            if (Descriptor(0xFD) is not { } o) return null;
            var flags = Base[o + 4];
            return new RangeLimits(Base[o + 5] + ((flags & 0x03) == 0x03 ? 255 : 0), Base[o + 6] + ((flags & 0x02) != 0 ? 255 : 0),
                Base[o + 7] + ((flags & 0x0C) == 0x0C ? 255 : 0), Base[o + 8] + ((flags & 0x08) != 0 ? 255 : 0), Base[o + 9] * 10);
        }
    }

    public IReadOnlyList<EdidTiming> Timings
    {
        get
        {
            var list = new List<EdidTiming>();
            foreach (var o in BaseSlots)
                if (DetailedTiming.Decode(Base.AsSpan(o, 18)) is { } t) list.Add(new EdidTiming(0, o, t, o == 54));
            if (CtaIndex is { } c)
                foreach (var o in CtaDtdOffsets(_blocks[c]))
                    if (DetailedTiming.Decode(_blocks[c].AsSpan(o, 18)) is { } t) list.Add(new EdidTiming(c, o, t, false));
            return list;
        }
    }

    static IEnumerable<int> CtaDtdOffsets(byte[] cta)
    {
        var d = cta[2];
        if (d < 4) yield break;
        for (var o = (int)d; o + 18 <= 127; o += 18) yield return o;
    }

    public bool BasicAudio => CtaIndex is { } c && (_blocks[c][3] & 0x40) != 0;
    public bool HasAudioBlock => CtaIndex is { } c && DataBlocks(_blocks[c]).Any(b => b.Tag == 1);

    /// <summary>Product code as Windows shows it (manufacturer + code in hex, e.g. SAM727E).</summary>
    public string ProductId => $"{Manufacturer}{ProductCode:X4}";

    /// <summary>Preferred timing (first descriptor of the base block).</summary>
    public DetailedTiming? NativeTiming => DetailedTiming.Decode(Base.AsSpan(54, 18));

    /// <summary>Free timing slots in the CTA-861 extension (18 bytes each).</summary>
    public int FreeSlots => CtaIndex is { } c ? CtaDtdOffsets(_blocks[c]).Count(o => _blocks[c].AsSpan(o, 18).IndexOfAnyExcept((byte)0) < 0) : 0;

    // Format codes of the Short Audio Descriptors (CTA-861, table 37).
    public const int AudioLpcm = 1, AudioAc3 = 2, AudioDts = 7, AudioEac3 = 10, AudioDtsHd = 11, AudioTrueHd = 12;

    /// <summary>Default SAD of each format: channels, sample rates and the third byte (max rate or profile).</summary>
    static readonly Dictionary<int, byte[]> DefaultSads = new()
    {
        [AudioLpcm] = new byte[] { 0x09, 0x07, 0x07 },    // 2 channels, 32/44.1/48 kHz, 16/20/24 bits
        [AudioAc3] = new byte[] { 0x15, 0x07, 0x50 },     // 6 channels, 640 kbps
        [AudioDts] = new byte[] { 0x3D, 0x06, 0xC0 },     // 6 channels, 1536 kbps
        [AudioEac3] = new byte[] { 0x57, 0x06, 0x00 },    // 8 channels
        [AudioDtsHd] = new byte[] { 0x5F, 0x7E, 0x01 },   // 8 channels, up to 192 kHz
        [AudioTrueHd] = new byte[] { 0x67, 0x7E, 0x00 },  // 8 channels, up to 192 kHz
    };

    /// <summary>Formats (codes) listed in the extension's audio blocks.</summary>
    public IReadOnlyList<int> AudioFormats
    {
        get
        {
            if (CtaIndex is not { } c) return Array.Empty<int>();
            var cta = _blocks[c];
            return DataBlocks(cta).Where(b => b.Tag == 1)
                .SelectMany(b => Enumerable.Range(0, b.Length / 3).Select(i => cta[b.Offset + 1 + i * 3] >> 3 & 0x0F)).Distinct().ToList();
        }
    }

    static IEnumerable<(int Offset, int Tag, int Length)> DataBlocks(byte[] cta)
    {
        for (var o = 4; o < cta[2] && o < 127;)
        {
            int tag = cta[o] >> 5, len = cta[o] & 0x1F;
            yield return (o, tag, len);
            o += 1 + len;
        }
    }

    // ---------- editing ----------

    /// <summary>Adds a timing in the first free slot of the CTA-861 extension (where CRU puts the extra resolutions).</summary>
    public void AddTiming(DetailedTiming t)
    {
        var bytes = t.Encode();
        if (CtaIndex is not { } c) throw new InvalidOperationException(Txt.EdidNoCta);
        var cta = _blocks[c];
        if (cta[2] == 0) cta[2] = 4; // 0 = extension with no data blocks or timings: they start at byte 4
        foreach (var o in CtaDtdOffsets(cta))
            if (cta[o] == 0 && cta[o + 1] == 0 && cta.AsSpan(o, 18).IndexOfAnyExcept((byte)0) < 0)
            {
                bytes.CopyTo(cta, o);
                return;
            }
        throw new InvalidOperationException(Txt.EdidNoSpace);
    }

    /// <summary>
    /// Removes a timing. In the extension, the following ones move up and the end becomes padding; in the base block, the descriptor becomes
    /// "empty" (tag 0x10). The preferred timing (first of the base block) stays.
    /// </summary>
    public void RemoveTiming(EdidTiming t)
    {
        if (t.Block == 0)
        {
            if (t.Offset == 54) throw new InvalidOperationException(Txt.EdidBaseTimingFixed);
            Dummy().CopyTo(Base, t.Offset);
            return;
        }
        var cta = _blocks[t.Block];
        var end = CtaDtdOffsets(cta).LastOrDefault() + 18;
        Array.Copy(cta, t.Offset + 18, cta, t.Offset, end - t.Offset - 18);
        Array.Clear(cta, end - 18, 18);
    }

    /// <summary>Replaces a timing in place (edit resolution).</summary>
    public void ReplaceTiming(EdidTiming at, DetailedTiming t) => t.Encode().CopyTo(_blocks[at.Block], at.Offset);

    /// <summary>Makes the timing the preferred one: swaps places with the first descriptor of the base block.</summary>
    public void MakePreferred(EdidTiming t)
    {
        if (t.Block == 0 && t.Offset == 54) return;
        var block = _blocks[t.Block];
        var preferred = Base.AsSpan(54, 18).ToArray();
        block.AsSpan(t.Offset, 18).CopyTo(Base.AsSpan(54, 18));
        preferred.CopyTo(block, t.Offset);
    }

    /// <summary>Where a timing equal to this one is (after adding, to make it preferred).</summary>
    public EdidTiming? Find(DetailedTiming t) => Timings.LastOrDefault(x => x.Timing == t);

    static byte[] Dummy() => new byte[] { 0, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Vertical frequency range of the range-limits descriptor (used by VRR/FreeSync/G-Sync Compatible).</summary>
    public void SetVerticalRange(int minHz, int maxHz)
    {
        if (minHz is < 1 or > 510 || maxHz is < 1 or > 510 || minHz > maxHz) throw new ArgumentOutOfRangeException(nameof(minHz), Txt.EdidRangeInvalid);
        if (Descriptor(0xFD) is not { } o) throw new InvalidOperationException(Txt.EdidNoRange);
        var flags = (byte)(Base[o + 4] & 0xFC);
        if (maxHz > 255) flags |= 0x02;
        if (minHz > 255) flags |= 0x03;
        Base[o + 4] = flags;
        Base[o + 5] = (byte)(minHz > 255 ? minHz - 255 : minHz);
        Base[o + 6] = (byte)(maxHz > 255 ? maxHz - 255 : maxHz);
    }

    /// <summary>
    /// Turns on (or adjusts) the range-limits descriptor: requested vertical and pixel clock; horizontal automatic, covering all
    /// timings (at least 30–255 kHz). Without a descriptor, uses an empty descriptor of the base block.
    /// </summary>
    public void SetRange(int minHz, int maxHz, int maxPixelClockMhz)
    {
        if (maxPixelClockMhz is < 10 or > 2550) throw new ArgumentOutOfRangeException(nameof(maxPixelClockMhz), "Max pixel clock must be 10–2550 MHz.");
        if (Descriptor(0xFD) is null)
        {
            var free = Descriptor(0x10) ?? throw new InvalidOperationException("The base block has no free descriptor for a range.");
            byte[] range = { 0, 0, 0, 0xFD, 0, 0, 0, 0, 0, 0, 0x01, 0x0A, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 };
            range.CopyTo(Base, free);
        }
        SetVerticalRange(minHz, maxHz);
        var o = Descriptor(0xFD)!.Value;
        var hz = Timings.Select(t => t.Timing.HTotal == 0 ? 0 : t.Timing.PixelClockKhz / (double)t.Timing.HTotal).Where(k => k > 0).ToList();
        var minH = Math.Clamp((int)Math.Floor(hz.DefaultIfEmpty(30).Min()), 1, 30);
        var maxH = Math.Clamp((int)Math.Ceiling(hz.DefaultIfEmpty(255).Max()), 255, 510);
        var flags = (byte)(Base[o + 4] & 0xF3);
        if (maxH > 255) flags |= 0x08;
        Base[o + 4] = flags;
        Base[o + 7] = (byte)minH;
        Base[o + 8] = (byte)(maxH > 255 ? maxH - 255 : maxH);
        Base[o + 9] = (byte)Math.Ceiling(maxPixelClockMhz / 10.0);
    }

    /// <summary>Turns the range off (VRR): the range-limits descriptor becomes an empty descriptor.</summary>
    public void RemoveRange()
    {
        if (Descriptor(0xFD) is { } o) Dummy().CopyTo(Base, o);
    }

    /// <summary>Basic HDMI audio: the CTA audio bit and a 2-channel LPCM audio block (32/44.1/48 kHz, 16/20/24 bits).</summary>
    public void EnableBasicAudio()
    {
        if (CtaIndex is not { } c) throw new InvalidOperationException(Txt.EdidNoCta);
        var cta = _blocks[c];
        cta[3] |= 0x40;
        if (HasAudioBlock) return;
        InsertCta(cta, cta[2] == 0 ? 4 : cta[2], new byte[] { 0x23 }.Concat(DefaultSads[AudioLpcm]).ToArray(), dataBlock: true);
    }

    /// <summary>Turns HDMI audio off: clears the audio bit and the audio blocks.</summary>
    public void DisableAudio()
    {
        if (CtaIndex is not { } c) return;
        var cta = _blocks[c];
        cta[3] &= 0xBF;
        for (var b = AudioBlock(cta); b is { } x; b = AudioBlock(cta)) RemoveCta(cta, x.Offset, 1 + x.Length, dataBlock: true);
    }

    static (int Offset, int Tag, int Length)? AudioBlock(byte[] cta) =>
        DataBlocks(cta).Where(b => b.Tag == 1).Cast<(int, int, int)?>().FirstOrDefault();

    /// <summary>Turns an audio format (bitstream) on/off in the audio block; turning a format on turns audio on.</summary>
    public void SetAudioFormat(int code, bool on)
    {
        if (!DefaultSads.ContainsKey(code)) throw new ArgumentOutOfRangeException(nameof(code));
        if (on) EnableBasicAudio();
        if (CtaIndex is not { } c) throw new InvalidOperationException(Txt.EdidNoCta);
        var cta = _blocks[c];
        if (AudioBlock(cta) is not { } block) return; // turning off with no audio block: nothing to do
        var sads = Enumerable.Range(0, block.Length / 3).Select(i => block.Offset + 1 + i * 3).ToList();
        var existing = sads.FirstOrDefault(at => (cta[at] >> 3 & 0x0F) == code, -1);
        if (on && existing < 0)
        {
            if (block.Length + 3 > 31) throw new InvalidOperationException(Txt.EdidNoSpace);
            InsertCta(cta, block.Offset + 1 + block.Length, DefaultSads[code], dataBlock: true);
            cta[block.Offset] = (byte)(1 << 5 | block.Length + 3);
        }
        else if (!on && existing >= 0)
        {
            RemoveCta(cta, existing, 3, dataBlock: true);
            cta[block.Offset] = (byte)(1 << 5 | block.Length - 3);
        }
    }

    /// <summary>End of the last used timing of the extension (or the start of the timings).</summary>
    static int UsedEnd(byte[] cta)
    {
        var d = cta[2] == 0 ? 4 : cta[2];
        return CtaDtdOffsets(cta).Where(o => cta.AsSpan(o, 18).IndexOfAnyExcept((byte)0) >= 0).Select(o => o + 18).DefaultIfEmpty((int)d).Max();
    }

    /// <summary>Inserts bytes in the data-block area; the following timings move down and their start (byte 2) follows.</summary>
    static void InsertCta(byte[] cta, int at, byte[] bytes, bool dataBlock)
    {
        if (cta[2] == 0) cta[2] = 4;
        var used = UsedEnd(cta);
        if (used + bytes.Length > 127) throw new InvalidOperationException(Txt.EdidNoSpace);
        Array.Copy(cta, at, cta, at + bytes.Length, used - at);
        bytes.CopyTo(cta, at);
        if (dataBlock) cta[2] = (byte)(cta[2] + bytes.Length);
    }

    static void RemoveCta(byte[] cta, int at, int count, bool dataBlock)
    {
        var used = UsedEnd(cta);
        Array.Copy(cta, at + count, cta, at, used - at - count);
        Array.Clear(cta, used - count, count);
        if (dataBlock) cta[2] = (byte)(cta[2] - count);
    }
}
