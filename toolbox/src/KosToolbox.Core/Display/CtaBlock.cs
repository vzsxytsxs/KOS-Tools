using KosToolbox.Core.Text;

namespace KosToolbox.Core.Display;

/// <summary>What a CTA-861 data block holds, from its tag, extended tag or IEEE OUI.</summary>
public enum CtaKind
{
    Audio, Video, Hdmi, HdmiForum, FreeSync, Vendor, Speaker, VideoCapability, Colorimetry, HdrStatic, HdrDynamic, Ycbcr420Video, Ycbcr420Map, Other,
}

/// <summary>A CTA-861 data block, header byte included (tag in the top 3 bits, payload length in the low 5).</summary>
public sealed record CtaDataBlock(byte[] Raw)
{
    public const int HdmiOui = 0x000C03, HdmiForumOui = 0xC45DD8, AmdOui = 0x00001A;

    public int Tag => Raw[0] >> 5;
    public int Length => Raw[0] & 0x1F;
    public int? ExtendedTag => Tag == 7 && Raw.Length > 1 ? Raw[1] : null;
    public int? Oui => Tag == 3 && Raw.Length >= 4 ? Raw[1] | Raw[2] << 8 | Raw[3] << 16 : null;

    public CtaKind Kind => (Tag, ExtendedTag, Oui) switch
    {
        (1, _, _) => CtaKind.Audio,
        (2, _, _) => CtaKind.Video,
        (3, _, HdmiOui) => CtaKind.Hdmi,
        (3, _, HdmiForumOui) => CtaKind.HdmiForum,
        (3, _, AmdOui) => CtaKind.FreeSync,
        (3, _, _) => CtaKind.Vendor,
        (4, _, _) => CtaKind.Speaker,
        (7, 0, _) => CtaKind.VideoCapability,
        (7, 5, _) => CtaKind.Colorimetry,
        (7, 6, _) => CtaKind.HdrStatic,
        (7, 7, _) => CtaKind.HdrDynamic,
        (7, 14, _) => CtaKind.Ycbcr420Video,
        (7, 15, _) => CtaKind.Ycbcr420Map,
        _ => CtaKind.Other,
    };

    /// <summary>Builds a block from its tag and payload (the header holds the length: at most 31 bytes).</summary>
    public static CtaDataBlock Create(int tag, ReadOnlySpan<byte> payload)
    {
        if (tag is < 1 or > 7 || payload.Length > 31) throw new InvalidOperationException(Txt.EdidCtaFull);
        var raw = new byte[payload.Length + 1];
        raw[0] = (byte)(tag << 5 | payload.Length);
        payload.CopyTo(raw.AsSpan(1));
        return new CtaDataBlock(raw);
    }

    public string Name => Kind switch
    {
        CtaKind.Audio => "Audio formats",
        CtaKind.Video => "TV resolutions",
        CtaKind.Hdmi => "HDMI support",
        CtaKind.HdmiForum => "HDMI 2.x support",
        CtaKind.FreeSync => "FreeSync range",
        CtaKind.Vendor => $"Vendor-specific ({Oui:X6})",
        CtaKind.Speaker => "Speaker setup",
        CtaKind.VideoCapability => "Video capability",
        CtaKind.Colorimetry => "Colorimetry",
        CtaKind.HdrStatic => "HDR static metadata",
        CtaKind.HdrDynamic => "HDR dynamic metadata",
        CtaKind.Ycbcr420Video => "YCbCr 4:2:0 resolutions",
        CtaKind.Ycbcr420Map => "YCbCr 4:2:0 capability map",
        _ => ExtendedTag is { } e ? ExtendedName(e) : Tag == 5 ? "VESA display transfer" : $"Unknown (tag {Tag})",
    };

    static string ExtendedName(int e) => e switch
    {
        1 => "Vendor-specific video", 2 => "VESA display device", 3 => "VESA video timing", 8 => "Native video resolution",
        13 => "Video format preference", 17 => "Vendor-specific audio", 18 => "HDMI audio", 19 => "Room configuration",
        20 => "Speaker location", 32 => "InfoFrame", 34 => "DisplayID type VII timing", 35 => "DisplayID type VIII timing",
        42 => "DisplayID type X timing", 120 => "HDMI EDID extension override", 121 => "HDMI sink capability",
        _ => $"Extended (tag {e})",
    };

    /// <summary>Short contents for the list (CRU's third column).</summary>
    public string Summary => Kind switch
    {
        CtaKind.Video => Txt.Count(CtaFormats.ReadVideo(this).Count, "resolution"),
        CtaKind.Audio => string.Join(", ", CtaFormats.ReadAudio(this).Select(s => CtaFormats.AudioName(s[0] >> 3 & 0x0F))),
        CtaKind.Speaker => CtaFormats.SpeakerSummary(this),
        CtaKind.FreeSync => CtaFormats.ReadFreeSync(this) is var (min, max) ? $"{min}-{max} Hz" : "",
        CtaKind.Hdmi => Length >= 7 && Raw[7] > 0 ? $"{Raw[7] * 5} MHz" : "",
        CtaKind.HdmiForum => Length >= 5 && Raw[5] > 0 ? $"{Raw[5] * 5} MHz" : "",
        _ => "",
    };
}

/// <summary>
/// A CTA-861 extension block, parsed into its flags, data blocks and detailed resolutions and rebuilt on <see cref="ToBytes"/>
/// (data blocks first, then the 18-byte timings, then zero padding). An edit that doesn't fit is undone and refused.
/// </summary>
public sealed class CtaBlock
{
    readonly byte[] _header = new byte[4];
    readonly byte[] _opaque;            // revision 1/2: bytes 4..d kept as they are (no data block collection there)
    readonly List<CtaDataBlock> _data = new();
    readonly List<byte[]> _timings = new();

    public CtaBlock(byte[] block)
    {
        if (block.Length != 128 || block[0] != 0x02) throw new FormatException(Txt.EdidInvalid);
        block.AsSpan(0, 4).CopyTo(_header);
        int d = block[2];
        var end = d is >= 4 and <= 127 ? d : 4;
        if (Revision >= 3)
        {
            _opaque = Array.Empty<byte>();
            for (var o = 4; o < end;)
            {
                var len = block[o] & 0x1F;
                if (o + 1 + len > end) break; // malformed: what fits is kept
                _data.Add(new CtaDataBlock(block.AsSpan(o, 1 + len).ToArray()));
                o += 1 + len;
            }
        }
        else _opaque = block.AsSpan(4, end - 4).ToArray();
        if (d == 0) return;
        for (var o = end; o + 18 <= 127; o += 18)
        {
            if (block[o] == 0 && block[o + 1] == 0) break; // padding
            _timings.Add(block.AsSpan(o, 18).ToArray());
        }
    }

    public int Revision => _header[1];
    public bool SupportsDataBlocks => Revision >= 3;

    bool Flag(int bit) => (_header[3] & 1 << bit) != 0;
    void SetFlag(int bit, bool on) => _header[3] = (byte)(on ? _header[3] | 1 << bit : _header[3] & ~(1 << bit));
    public bool Underscan { get => Flag(7); set => SetFlag(7, value); }
    public bool BasicAudio { get => Flag(6); set => SetFlag(6, value); }
    public bool YCbCr444 { get => Flag(5); set => SetFlag(5, value); }
    public bool YCbCr422 { get => Flag(4); set => SetFlag(4, value); }
    /// <summary>How many of the detailed resolutions are native (low 4 bits of byte 3).</summary>
    public int NativeCount
    {
        get => _header[3] & 0x0F;
        set => _header[3] = (byte)(_header[3] & 0xF0 | Math.Clamp(value, 0, 15));
    }

    public IReadOnlyList<CtaDataBlock> DataBlocks => _data;
    public IReadOnlyList<DetailedTiming> Timings => _timings.Select(t => DetailedTiming.Decode(t)!).ToList();

    int Used => 4 + _opaque.Length + _data.Sum(b => b.Raw.Length) + _timings.Count * 18;
    /// <summary>Bytes left for data blocks or timings (a timing takes 18).</summary>
    public int FreeBytes => 127 - Used;

    public byte[] ToBytes()
    {
        var b = new byte[128];
        _header.CopyTo(b, 0);
        var o = 4;
        _opaque.CopyTo(b, o);
        o += _opaque.Length;
        foreach (var d in _data) { d.Raw.CopyTo(b, o); o += d.Raw.Length; }
        b[2] = (byte)o;
        foreach (var t in _timings) { t.CopyTo(b, o); o += 18; }
        return b;
    }

    /// <summary>Runs an edit and undoes it when the block would overflow.</summary>
    void Edit<T>(List<T> list, Action change)
    {
        var before = list.ToList();
        change();
        if (FreeBytes >= 0) return;
        list.Clear();
        list.AddRange(before);
        throw new InvalidOperationException(Txt.EdidCtaFull);
    }

    static void Move<T>(List<T> list, int index, int delta)
    {
        var j = index + delta;
        if (index < 0 || index >= list.Count || j < 0 || j >= list.Count) return;
        (list[index], list[j]) = (list[j], list[index]);
    }

    public void AddDataBlock(CtaDataBlock block)
    {
        if (!SupportsDataBlocks) throw new InvalidOperationException("This extension block is revision 1 or 2, without data blocks.");
        Edit(_data, () => _data.Add(block));
    }
    public void ReplaceDataBlock(int index, CtaDataBlock block) => Edit(_data, () => _data[index] = block);
    public void RemoveDataBlock(int index) => _data.RemoveAt(index);
    public void MoveDataBlock(int index, int delta) => Move(_data, index, delta);
    public void ClearDataBlocks() => _data.Clear();

    public void AddTiming(DetailedTiming t) { var bytes = t.Encode(); Edit(_timings, () => _timings.Add(bytes)); }
    public void ReplaceTiming(int index, DetailedTiming t) { var bytes = t.Encode(); Edit(_timings, () => _timings[index] = bytes); }
    public void RemoveTiming(int index) => _timings.RemoveAt(index);
    public void MoveTiming(int index, int delta) => Move(_timings, index, delta);
    public void ClearTimings() => _timings.Clear();
}
