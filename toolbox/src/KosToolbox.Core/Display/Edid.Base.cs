using System.Text;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Display;

/// <summary>A resolution of the established timings bitmap (bytes 35–37); <see cref="Bit"/> counts from byte 35's top bit.</summary>
public sealed record EstablishedMode(int Bit, int Width, int Height, int Hz)
{
    public override string ToString() => $"{Width}x{Height} @ {Hz} Hz";
}

/// <summary>A standard timing (2 bytes): width, aspect ratio and refresh rate; the height follows from the ratio.</summary>
public sealed record StandardTiming(int Width, int Height, int Hz)
{
    /// <summary>The four ratios of the 2-bit code: 0 = 16:10 (1:1 before EDID 1.3), 1 = 4:3, 2 = 5:4, 3 = 16:9.</summary>
    public static readonly (int H, int V)[] Ratios = { (16, 10), (4, 3), (5, 4), (16, 9) };

    /// <summary>Common sizes offered by the editor, as CRU lists them.</summary>
    public static readonly (int Width, int Height)[] Presets =
    {
        (640, 480), (720, 576), (800, 600), (1024, 768), (1152, 864), (1280, 720), (1280, 800), (1280, 960), (1280, 1024), (1440, 900),
        (1600, 900), (1600, 1200), (1680, 1050), (1792, 1344), (1800, 1440), (1856, 1392), (1920, 1080), (1920, 1200), (1920, 1440), (2048, 1536),
    };

    /// <summary>Null for an unused slot (01 01, or 00 00 in some EDIDs).</summary>
    public static StandardTiming? Decode(byte b0, byte b1, bool before13 = false)
    {
        if (b0 is 0 or 1 && b1 is 0 or 1) return null;
        var width = (b0 + 31) * 8;
        var code = b1 >> 6;
        var (h, v) = code == 0 && before13 ? (1, 1) : Ratios[code];
        return new StandardTiming(width, width * v / h, (b1 & 0x3F) + 60);
    }

    /// <summary>The ratio code, or -1 when the height isn't one of the four ratios of the width.</summary>
    public int RatioCode => Array.FindIndex(Ratios, r => Height == Width * r.V / r.H);
    public string Ratio => RatioCode is var c and >= 0 ? $"{Ratios[c].H}:{Ratios[c].V}" : "?";

    /// <summary>Null when it fits: width 256–2288 in steps of 8, a height of one of the four ratios, 60–123 Hz.</summary>
    public string? Problem() =>
        Width is < 256 or > 2288 || Width % 8 != 0 ? "Width must be 256–2288, a multiple of 8." :
        RatioCode < 0 ? "The height must give a 16:10, 4:3, 5:4 or 16:9 ratio." :
        Hz is < 60 or > 123 ? "Refresh rate must be 60–123 Hz." : null;

    public byte[] Encode()
    {
        if (Problem() is { } p) throw new InvalidOperationException(p);
        return new[] { (byte)(Width / 8 - 31), (byte)(RatioCode << 6 | Hz - 60) };
    }

    public override string ToString() => $"{Width}x{Height} ({Ratio}) @ {Hz} Hz";
}

public enum DescriptorKind { Timing, Name, Serial, Text, Range, StandardTimings, ColorPoint, Dummy, Other }

/// <summary>One of the four 18-byte descriptors of the base block.</summary>
public sealed record EdidDescriptor(int Offset, DescriptorKind Kind, DetailedTiming? Timing, string Text)
{
    public bool Preferred => Offset == 54;
}

/// <summary>An extension block in the list (CRU's "Extension blocks").</summary>
public sealed record ExtensionBlockInfo(int Index, int Tag, string Text)
{
    public bool IsCta => Tag == 0x02;
}

public sealed partial class Edid
{
    /// <summary>Established resolutions the editor shows, as CRU: the other bits (720x400, Mac modes, interlaced) stay as they are.</summary>
    public static readonly IReadOnlyList<EstablishedMode> EstablishedModes = new EstablishedMode[]
    {
        new(2, 640, 480, 60), new(4, 640, 480, 72), new(5, 640, 480, 75),
        new(6, 800, 600, 56), new(7, 800, 600, 60), new(8, 800, 600, 72), new(9, 800, 600, 75),
        new(12, 1024, 768, 60), new(13, 1024, 768, 70), new(14, 1024, 768, 75),
        new(15, 1280, 1024, 75),
    };

    public bool Established(int bit) => (Base[35 + bit / 8] & 0x80 >> bit % 8) != 0;

    public void SetEstablished(int bit, bool on)
    {
        if (bit is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(bit));
        if (on) Base[35 + bit / 8] |= (byte)(0x80 >> bit % 8);
        else Base[35 + bit / 8] &= (byte)~(0x80 >> bit % 8);
    }

    bool Before13 => Base[18] == 1 && Base[19] < 3;

    /// <summary>The used standard timing slots (bytes 38–53), in order.</summary>
    public IReadOnlyList<StandardTiming> StandardTimings =>
        Enumerable.Range(0, 8).Select(i => StandardTiming.Decode(Base[38 + i * 2], Base[39 + i * 2], Before13)).OfType<StandardTiming>().ToList();

    public const int StandardSlots = 8;

    /// <summary>Writes the list into the 8 slots, the rest marked unused (01 01).</summary>
    public void SetStandardTimings(IReadOnlyList<StandardTiming> list)
    {
        if (list.Count > StandardSlots) throw new InvalidOperationException(Txt.EdidNoSpace);
        var bytes = list.SelectMany(t => t.Encode()).ToArray();
        for (var i = 0; i < StandardSlots * 2; i++) Base[38 + i] = i < bytes.Length ? bytes[i] : (byte)1;
    }

    /// <summary>The four descriptors of the base block (detailed resolutions and monitor descriptors).</summary>
    public IReadOnlyList<EdidDescriptor> Descriptors => BaseSlots.Select(o => DescribeDescriptor(Base, o)).ToList();

    static EdidDescriptor DescribeDescriptor(byte[] b, int o)
    {
        if (DetailedTiming.Decode(b.AsSpan(o, 18)) is { } t) return new(o, DescriptorKind.Timing, t, "");
        string Ascii() => Encoding.ASCII.GetString(b, o + 5, 13).Split('\n')[0].TrimEnd();
        return b[o + 3] switch
        {
            0xFC => new(o, DescriptorKind.Name, null, Ascii()),
            0xFF => new(o, DescriptorKind.Serial, null, Ascii()),
            0xFE => new(o, DescriptorKind.Text, null, Ascii()),
            0xFD => new(o, DescriptorKind.Range, null, ""),
            0xFA => new(o, DescriptorKind.StandardTimings, null, ""),
            0xFB => new(o, DescriptorKind.ColorPoint, null, ""),
            0x10 => new(o, DescriptorKind.Dummy, null, ""),
            var tag => new(o, DescriptorKind.Other, null, $"0x{tag:X2}"),
        };
    }

    /// <summary>Empty descriptors (tag 0x10) of the base block: where detailed resolutions can be added.</summary>
    public int FreeDescriptors => Descriptors.Count(d => d.Kind == DescriptorKind.Dummy);

    /// <summary>Adds a detailed resolution in the first empty descriptor of the base block.</summary>
    public void AddBaseTiming(DetailedTiming t)
    {
        var bytes = t.Encode();
        var free = Descriptors.FirstOrDefault(d => d.Kind == DescriptorKind.Dummy) ?? throw new InvalidOperationException(Txt.EdidNoBaseSlot);
        bytes.CopyTo(Base, free.Offset);
    }

    /// <summary>Empties a descriptor of the base block (the first one, the preferred resolution, stays).</summary>
    public void ClearDescriptor(int offset)
    {
        if (!BaseSlots.Contains(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset == 54) throw new InvalidOperationException(Txt.EdidBaseTimingFixed);
        Dummy().CopyTo(Base, offset);
    }

    /// <summary>Moves a descriptor of the base block up or down (the first stays the preferred resolution).</summary>
    public void MoveDescriptor(int offset, int delta)
    {
        var i = Array.IndexOf(BaseSlots, offset);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= BaseSlots.Length) return;
        var a = Base.AsSpan(offset, 18).ToArray();
        Base.AsSpan(BaseSlots[j], 18).CopyTo(Base.AsSpan(offset, 18));
        a.CopyTo(Base, BaseSlots[j]);
    }

    // ---------- extension blocks ----------
    public const int MaxBlocks = 4;

    public IReadOnlyList<ExtensionBlockInfo> ExtensionList => Enumerable.Range(1, _blocks.Count - 1).Select(i => new ExtensionBlockInfo(i, _blocks[i][0], DescribeBlock(_blocks[i]))).ToList();

    static string DescribeBlock(byte[] b)
    {
        switch (b[0])
        {
            case 0x02:
                var cta = new CtaBlock(b);
                var parts = new List<string>();
                if (cta.Timings.Count > 0) parts.Add(Txt.Count(cta.Timings.Count, "detailed resolution"));
                if (cta.DataBlocks.Count > 0) parts.Add(Txt.Count(cta.DataBlocks.Count, "data block"));
                return $"CTA-861: {(parts.Count == 0 ? "empty" : string.Join(", ", parts))}";
            case 0x70: return $"DisplayID {b[1] >> 4}.{b[1] & 0x0F}";
            case 0xF0: return "Block map";
            case 0x10: return "Video timing block";
            case 0x40: return "Display information";
            case 0x50: return "Localized strings";
            case 0x60: return "Digital packet video link";
            default: return $"Unknown (0x{b[0]:X2})";
        }
    }

    public byte[] Block(int index) => _blocks[index].ToArray();

    /// <summary>Replaces an extension block with an edited copy (the checksum is rebuilt on save).</summary>
    public void SetBlock(int index, byte[] block)
    {
        if (index < 1 || index >= _blocks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (block.Length != 128) throw new ArgumentException(Txt.EdidInvalid, nameof(block));
        _blocks[index] = block.ToArray();
    }

    /// <summary>Adds an empty CTA-861 (revision 3) block at the end.</summary>
    public void AddCtaBlock()
    {
        if (_blocks.Count >= MaxBlocks) throw new InvalidOperationException(Txt.EdidMaxBlocks(MaxBlocks - 1));
        var b = new byte[128];
        b[0] = 0x02;
        b[1] = 0x03;
        b[2] = 0x04;
        _blocks.Add(b);
    }

    public void RemoveBlock(int index)
    {
        if (index < 1 || index >= _blocks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _blocks.RemoveAt(index);
    }

    public void MoveBlock(int index, int delta)
    {
        var j = index + delta;
        if (index < 1 || index >= _blocks.Count || j < 1 || j >= _blocks.Count) return;
        (_blocks[index], _blocks[j]) = (_blocks[j], _blocks[index]);
    }
}
