using KosToolbox.Core.Peripherals.Odm;

namespace KosToolbox.Core.Peripherals.Corsair;

/// <summary>Lift-off distance as the SABRE stores it (address 0A): 0 low, 3 medium, 4 high.</summary>
public enum SabreLift { Low = 0, Medium = 3, High = 4 }

/// <summary>What a button does, from the types seen in a real SABRE v2 PRO's memory (mouse button, DPI, disabled).</summary>
public enum SabreAction { Left, Right, Middle, Back, Forward, DpiCycle, DpiUp, DpiDown, Disabled }

/// <summary>
/// The settings of a SABRE v2 PRO decoded from its memory image. A field is null when its checksum doesn't validate or its value isn't
/// one the toolbox knows: such a field is neither shown nor written. <see cref="StageDpi"/> and <see cref="StageColors"/> have
/// <see cref="StageCount"/> entries; <see cref="Buttons"/> has keys 1..5 (null entry: a function the toolbox doesn't offer).
/// </summary>
public sealed record SabreSettings(int? PollingHz, int? StageCount, int? ActiveStage, IReadOnlyList<int>? StageDpi, SabreLift? LiftOff,
    bool? MotionSync, bool? AngleSnap, bool? Ripple, int? SleepSeconds, bool? SurfaceCalibration, IReadOnlyList<int>? StageColors,
    IReadOnlyList<SabreAction?>? Buttons);

/// <summary>
/// The SABRE v2 PRO's "report 8" protocol on top of <see cref="Report8Codec"/> (Win32 buffer: [0] 08, [1] command, [2] 0, [3..4] address
/// big-endian, [5] length ≤ 10, [6..15] data, [16] checksum 0x55 − Σ). Commands: 04 battery, 08 read memory, 07 write memory, 12 mouse
/// firmware, 1D receiver firmware. Every stored value is followed by a checksum byte, so a field's bytes always add up to 0x55 (mod 256):
/// [v, 0x55 − v], a DPI stage [x, y, high, chk], a button [type, p1, p2, chk], a colour [r, g, b, chk]. Memory map from Corsair's SABRE
/// Web Hub and OpenLinkHub; every field below was read from a real mouse through its receiver (the friend's, 2026-11-23) and matched
/// the iCUE screens, except the writes (command 07), which no probe has sent yet.
/// </summary>
public static class SabreCodec
{
    public const byte Battery = 0x04, ReadMemory = 0x08, WriteMemory = 0x07, MouseFirmware = 0x12, ReceiverFirmware = 0x1D;
    public const int PollingAt = 0x00, StageCountAt = 0x02, ActiveStageAt = 0x04, LiftOffAt = 0x0A, StagesAt = 0x0C, ColorsAt = 0x2C, ButtonsAt = 0x60,
        MotionSyncAt = 0xAB, SleepAt = 0xAD, AngleSnapAt = 0xAF, RippleAt = 0xB1, SurfaceAt = 0xDF;
    public const int ChunkLength = 10, ImageLength = 0xE6, MaxStages = 5, Keys = 5;
    public const int DpiMin = 100, DpiMax = 12800, DpiStep = 50;

    /// <summary>1000..8000 Hz; the codes below 1000 Hz were never seen, so they are neither read nor offered.</summary>
    public static readonly IReadOnlyList<int> Rates = new[] { 1000, 2000, 4000, 8000 };
    static readonly byte[] RateCodes = { 0x01, 0x10, 0x20, 0x40 };

    public static byte RateCode(int hz) => RateCodes[Array.IndexOf(Rates.ToArray(), hz)];

    /// <summary>The sleep times the Web Hub offers (minutes), in units of 10 s at address AD.</summary>
    public static readonly IReadOnlyList<int> SleepMinutes = new[] { 1, 3, 5, 10, 20, 30, 40 };

    public static byte[] Request(byte command, int address = 0, ReadOnlySpan<byte> data = default)
    {
        var p = new byte[Report8Codec.PayloadLength];
        p[0] = command;
        p[2] = (byte)(address >> 8);
        p[3] = (byte)address;
        p[4] = (byte)data.Length;
        data.CopyTo(p.AsSpan(5));
        p[15] = Report8Codec.Checksum(p);
        return p;
    }

    public static byte[] ReadRequest(int address, int length)
    {
        var p = Request(ReadMemory, address);
        p[4] = (byte)length;
        p[15] = Report8Codec.Checksum(p);
        return p;
    }

    /// <summary>The data of a reply (report id at [0]) to a memory read or write of <paramref name="length"/> bytes at <paramref name="address"/>; null when it is another reply or the status isn't 0.</summary>
    public static byte[]? MemoryData(ReadOnlySpan<byte> reply, byte command, int address, int length) =>
        Report8Codec.Answers(reply, command) && reply[2] == 0 && reply[3] == address >> 8 && reply[4] == (byte)address && reply[5] == length
            ? reply.Slice(6, length).ToArray() : null;

    /// <summary>Battery reply: [6] percent, [7] charging. 0–100 only; anything else is a mouse that isn't answering.</summary>
    public static PeripheralBattery? DecodeBattery(ReadOnlySpan<byte> reply) =>
        Report8Codec.Answers(reply, Battery) && reply[2] == 0 && reply[6] <= 100 ? new PeripheralBattery(reply[6], reply[7] != 0) : null;

    /// <summary>Firmware reply: [6..7] as shown by iCUE, hex digits read as decimal ones (02 24 → 2.24).</summary>
    public static string? DecodeFirmware(ReadOnlySpan<byte> reply, byte command) =>
        Report8Codec.Answers(reply, command) && reply[2] == 0 ? $"{reply[6]:X}.{reply[7]:X2}" : null;

    // ---------- the memory image ----------

    public static byte[] Pair(int value) => new[] { (byte)value, (byte)(0x55 - value) };

    /// <summary>True when the <paramref name="length"/> bytes at <paramref name="address"/> add up to 0x55 (mod 256), as every stored field does.</summary>
    public static bool BlockValid(ReadOnlySpan<byte> image, int address, int length)
    {
        if (address < 0 || address + length > image.Length) return false;
        var sum = 0;
        foreach (var b in image.Slice(address, length)) sum += b;
        return (byte)sum == 0x55;
    }

    static int? One(byte[] m, int at) => BlockValid(m, at, 2) ? m[at] : null;

    static bool? Flag(byte[] m, int at) => One(m, at) is { } v and <= 1 ? v == 1 : null;

    public static SabreSettings Decode(byte[] image)
    {
        var count = One(image, StageCountAt) is { } c and >= 1 and <= MaxStages ? c : (int?)null;
        var active = One(image, ActiveStageAt) is { } a && a < count ? a : (int?)null;
        var stages = count is { } n ? Enumerable.Range(0, n).Select(i => DecodeStage(image, StagesAt + 4 * i)).ToList() : null;
        var colors = count is { } k ? Enumerable.Range(0, k).Select(i => DecodeColor(image, ColorsAt + 4 * i)).ToList() : null;
        var buttons = Enumerable.Range(0, Keys).Select(i => DecodeButton(image, ButtonsAt + 4 * i)).ToList();
        return new SabreSettings(
            One(image, PollingAt) is { } p && Array.IndexOf(RateCodes, (byte)p) is var r and >= 0 ? Rates[r] : null,
            count, active,
            stages is not null && stages.All(s => s is not null) ? stages.Select(s => s!.Value).ToList() : null,
            One(image, LiftOffAt) is { } lift && Enum.IsDefined((SabreLift)lift) ? (SabreLift)lift : null,
            Flag(image, MotionSyncAt), Flag(image, AngleSnapAt), Flag(image, RippleAt),
            One(image, SleepAt) is { } s10 and >= 1 and <= 240 ? s10 * 10 : null,
            Flag(image, SurfaceAt),
            colors is not null && colors.All(x => x is not null) ? colors.Select(x => x!.Value).ToList() : null,
            buttons);
    }

    // ---------- DPI stage: [x low, y low, high, chk]; high 00 / 44 / 88 adds 0, 256, 512 steps of 50 ----------

    public static bool ValidDpi(int dpi) => dpi is >= DpiMin and <= DpiMax && dpi % DpiStep == 0;

    /// <summary>The 4 bytes of a stage. Only values up to 12800 DPI are written: above that the high byte was never read back from a mouse.</summary>
    public static byte[] EncodeStage(int dpi)
    {
        if (!ValidDpi(dpi)) throw new ArgumentOutOfRangeException(nameof(dpi));
        var n = dpi / DpiStep - 1;
        return new[] { (byte)n, (byte)n, (byte)0, (byte)(0x55 - 2 * n) };
    }

    public static int? DecodeStage(byte[] m, int at)
    {
        if (!BlockValid(m, at, 4) || m[at] != m[at + 1]) return null;
        var page = m[at + 2] switch { 0x00 => 0, 0x44 => 256, 0x88 => 512, _ => -1 };
        return page < 0 ? null : (m[at] + page + 1) * DpiStep;
    }

    /// <summary>A DPI stage's colour (0xRRGGBB). Read only: the Web Hub never writes it.</summary>
    public static int? DecodeColor(byte[] m, int at) => BlockValid(m, at, 4) ? m[at] << 16 | m[at + 1] << 8 | m[at + 2] : null;

    // ---------- buttons: [type, p1, p2, chk] ----------

    static readonly (SabreAction Action, byte Type, byte P1)[] ButtonCodes =
    {
        (SabreAction.Left, 0x01, 0x01), (SabreAction.Right, 0x01, 0x02), (SabreAction.Middle, 0x01, 0x04), (SabreAction.Back, 0x01, 0x08),
        (SabreAction.Forward, 0x01, 0x10), (SabreAction.DpiCycle, 0x02, 0x01), (SabreAction.DpiUp, 0x02, 0x02), (SabreAction.DpiDown, 0x02, 0x03),
        (SabreAction.Disabled, 0x00, 0x00),
    };

    public static byte[] EncodeButton(SabreAction action)
    {
        var (_, type, p1) = ButtonCodes.First(b => b.Action == action);
        return new[] { type, p1, (byte)0, (byte)(0x55 - type - p1) };
    }

    public static SabreAction? DecodeButton(byte[] m, int at)
    {
        if (!BlockValid(m, at, 4) || m[at + 2] != 0) return null;
        foreach (var (action, type, p1) in ButtonCodes)
            if (m[at] == type && m[at + 1] == p1) return action;
        return null;
    }
}
