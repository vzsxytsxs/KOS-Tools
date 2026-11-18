using System.Buffers.Binary;

namespace KosToolbox.Core.Peripherals.Royuan;

/// <summary>
/// One macro step: a key (HID usage 0x04..0xEF) or mouse button (0..4: left, right, middle, back, forward) going down or up, then
/// <paramref name="DelayMs"/> before the next step.
/// </summary>
public sealed record RoyuanMacroStep(byte Code, bool MouseButton, bool Pressed, int DelayMs);

/// <summary>
/// An onboard macro: 50 slots of 256 bytes. Blob: u16 repeat count, then the steps, each [code, pressed &lt;&lt; 7 | delay] when the
/// delay is 1..127 ms, else [code, pressed &lt;&lt; 7, delay u16 LE]; mouse buttons are codes F0..F4; zero ends it (mouse moves, F9,
/// are read past and not offered). Written as 56-byte pages, <c>last</c> on the final one; read as four raw 64-byte pages.
/// Facts from sharkfin's docs/PROTOCOL.md ("Macros").
/// </summary>
public sealed record RoyuanMacro(int Repeat, IReadOnlyList<RoyuanMacroStep> Steps)
{
    public const int Slots = 50, Bytes = 256, PageData = 56;
    const byte MouseBase = 0xF0, MouseMove = 0xF9;

    /// <summary>The 256-byte blob; throws when a step is out of range or the steps don't fit (250 bytes, the end marker kept).</summary>
    public byte[] Encode()
    {
        if (Repeat is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(null, "The repeat count goes from 0 to 65535.");
        var blob = new byte[Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(blob, (ushort)Repeat);
        var at = 2;
        foreach (var s in Steps)
        {
            if (s.MouseButton ? s.Code > 4 : s.Code is < 0x04 or > 0xEF) throw new ArgumentOutOfRangeException(null, $"Step code {s.Code} is out of range.");
            if (s.DelayMs is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(null, "A delay goes from 0 to 65535 ms.");
            var size = s.DelayMs is >= 1 and <= 127 ? 2 : 4;
            if (at + size > Bytes - 4) throw new ArgumentOutOfRangeException(null, "The macro doesn't fit in 256 bytes.");
            blob[at] = (byte)(s.MouseButton ? MouseBase + s.Code : s.Code);
            blob[at + 1] = (byte)((s.Pressed ? 0x80 : 0) | (size == 2 ? s.DelayMs : 0));
            if (size == 4) BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(at + 2), (ushort)s.DelayMs);
            at += size;
        }
        return blob;
    }

    public static RoyuanMacro Decode(ReadOnlySpan<byte> blob)
    {
        var steps = new List<RoyuanMacroStep>();
        var i = 2;
        while (i + 1 < blob.Length && blob[i] != 0)
        {
            var code = blob[i];
            if (code == MouseMove) { i += 4; continue; }
            var pressed = (blob[i + 1] & 0x80) != 0;
            int delay;
            if ((blob[i + 1] & 0x7F) != 0) { delay = blob[i + 1] & 0x7F; i += 2; }
            else if (i + 3 < blob.Length) { delay = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(i + 2, 2)); i += 4; }
            else break;
            if (code is >= 0x04 and <= 0xEF) steps.Add(new RoyuanMacroStep(code, false, pressed, delay));
            else if (code is >= MouseBase and <= MouseBase + 4) steps.Add(new RoyuanMacroStep((byte)(code - MouseBase), true, pressed, delay));
            else break;
        }
        return new RoyuanMacro(BinaryPrimitives.ReadUInt16LittleEndian(blob), steps);
    }

    /// <summary>The write pages of a blob: the pages up to the last one with data (at least one), <c>last</c> on the final page.</summary>
    public static List<byte[]> Pages(int slot, byte[] blob)
    {
        var count = Math.Max(1, Enumerable.Range(0, (Bytes + PageData - 1) / PageData).LastOrDefault(p => blob.Skip(p * PageData).Take(PageData).Any(b => b != 0), -1) + 1);
        return Enumerable.Range(0, count).Select(p => RoyuanCodec.Request(
            new byte[] { RoyuanCodec.SetMacro, (byte)slot, (byte)p, PageData, (byte)(p == count - 1 ? 1 : 0) },
            blob.Skip(p * PageData).Take(PageData).ToArray())).ToList();
    }
}
