namespace KosToolbox.Core.Peripherals.Razer;

/// <summary>A Razer command: class, id (bit 7 set = getter) and the data size the device expects.</summary>
public readonly record struct RazerCommand(byte Class, byte Id, byte Size)
{
    public bool IsGetter => (Id & 0x80) != 0;
}

public enum RazerStatus : byte { Request = 0, Busy = 1, Ok = 2, Failure = 3, NoAnswer = 4, Unsupported = 5 }

/// <summary>A decoded reply: its status, whether it answers the command it was read for, and the 80 argument bytes.</summary>
public sealed record RazerReply(RazerStatus Status, bool Matches, byte[] Args);

/// <summary>
/// The Razer 90-byte control report (feature report 0; the Win32 buffer is the id then these 90 bytes). Wire facts from OpenRazer's
/// driver sources as summarised by mouse-protocol (src/razer/codec.ts) and Wireless Battery (src/drivers/razer.cpp):
/// [0] status, [1] transaction id, [5] data size, [6] class, [7] id, [8..87] arguments, [88] XOR of [2..87].
/// </summary>
public static class RazerCodec
{
    public const int Length = 90, ArgsOffset = 8, ArgsLength = 80;
    const int ChecksumAt = 88;

    public static readonly RazerCommand Firmware = new(0x00, 0x81, 0x02), Serial = new(0x00, 0x82, 0x16);
    public static readonly RazerCommand PollingGet = new(0x00, 0x85, 0x01), PollingSet = new(0x00, 0x05, 0x01);
    // Extended (divisor of 8000): mouse-protocol RAZER_READ.pollingRateExtended / RAZER_WRITE.pollingRateExtended.
    public static readonly RazerCommand PollingExtGet = new(0x00, 0xC0, 0x02), PollingExtSet = new(0x00, 0x40, 0x02);
    public static readonly RazerCommand DpiGet = new(0x04, 0x85, 0x07), DpiSet = new(0x04, 0x05, 0x07);
    public static readonly RazerCommand StagesGet = new(0x04, 0x86, 0x26), StagesSet = new(0x04, 0x06, 0x26);
    public static readonly RazerCommand Battery = new(0x07, 0x80, 0x02), Charging = new(0x07, 0x84, 0x02);
    public static readonly RazerCommand SleepGet = new(0x07, 0x83, 0x02), SleepSet = new(0x07, 0x03, 0x02);
    public static readonly RazerCommand LowBatteryGet = new(0x07, 0x81, 0x02), LowBatterySet = new(0x07, 0x01, 0x02);
    // Extended-matrix brightness for one led (OpenRazer's 0x0F/0x84 and 0x0F/0x04): [storage, led, level 0-255].
    public static readonly RazerCommand BrightnessGet = new(0x0F, 0x84, 0x03), BrightnessSet = new(0x0F, 0x04, 0x03);
    // Extended-matrix effect for one led (OpenRazer razer_chroma_extended_matrix_effect_*): [storage, led, effect, ...]. The getter
    // answers in the same layout; checked on the Viper Ultimate (led 04) and the Mouse Dock (led 05).
    public static readonly RazerCommand EffectGet = new(0x0F, 0x82, 0x0C);
    static readonly RazerCommand EffectSetShort = new(0x0F, 0x02, 0x06), EffectSetColor = new(0x0F, 0x02, 0x09);

    /// <summary>Storage selector of the DPI and brightness commands: stored (survives a power cycle) or the live value only.</summary>
    public const byte Stored = 0x01, NoStore = 0x00;
    const int StageOffset = 3, StageLength = 7, MaxStages = 5;

    public static byte Checksum(ReadOnlySpan<byte> report)
    {
        byte x = 0;
        for (var i = 2; i < ChecksumAt; i++) x ^= report[i];
        return x;
    }

    /// <summary>The 90-byte request (without the report id).</summary>
    public static byte[] Request(byte transactionId, RazerCommand command, ReadOnlySpan<byte> args = default)
    {
        if (args.Length > ArgsLength) throw new ArgumentException("Razer arguments are at most 80 bytes.", nameof(args));
        var r = new byte[Length];
        r[1] = transactionId;
        r[5] = command.Size;
        r[6] = command.Class;
        r[7] = command.Id;
        args.CopyTo(r.AsSpan(ArgsOffset));
        r[ChecksumAt] = Checksum(r);
        return r;
    }

    /// <summary>
    /// A reply as GetFeature returns it (report id at [0], then 90 bytes). A busy, no-answer or unsupported status says nothing about the
    /// rest. A reply whose class/id or checksum don't fit isn't this command's (a stale one, or another program's).
    /// </summary>
    public static RazerReply Parse(ReadOnlySpan<byte> buffer, RazerCommand command)
    {
        if (buffer.Length < Length + 1) throw new PeripheralException($"The Razer reply has {buffer.Length} bytes; expected {Length + 1}.");
        var r = buffer.Slice(1, Length);
        var status = (RazerStatus)r[0];
        var matches = r[6] == command.Class && r[7] == command.Id && r[ChecksumAt] == Checksum(r);
        return new RazerReply(status, matches, r.Slice(ArgsOffset, ArgsLength).ToArray());
    }

    // ---------- values ----------
    public static string DecodeFirmware(ReadOnlySpan<byte> a) => $"{a[0]}.{a[1]}";

    public static string? DecodeSerial(ReadOnlySpan<byte> a)
    {
        var end = a[..0x16].IndexOf((byte)0);
        var text = System.Text.Encoding.ASCII.GetString(end < 0 ? a[..0x16] : a[..end]).Trim();
        return text.Length > 0 && text.All(c => c is >= ' ' and <= '~') ? text : null;
    }

    /// <summary>0–255 battery scale to percent, rounded: the battery reply and the low-power threshold share it.</summary>
    public static int Percent(byte level) => (level * 100 + 127) / 255;
    public static byte Level(int percent) => (byte)((Math.Clamp(percent, 0, 100) * 255 + 50) / 100);

    public static int DecodeBattery(ReadOnlySpan<byte> a) => Percent(a[1]);
    public static bool DecodeCharging(ReadOnlySpan<byte> a) => a[1] == 1;
    // The threshold answers in the first byte (mouse-protocol decodeLowPowerThreshold, checked there against Synapse).
    public static int DecodeLowBattery(ReadOnlySpan<byte> a) => Percent(a[0]);
    public static byte[] EncodeLowBattery(int percent) => new[] { Level(percent), (byte)0 };
    public static int DecodeSleep(ReadOnlySpan<byte> a) => a[0] << 8 | a[1];
    public static byte[] EncodeSleep(int seconds) => new[] { (byte)(seconds >> 8), (byte)seconds };

    public static DpiStage DecodeDpi(ReadOnlySpan<byte> a) => new(a[1] << 8 | a[2], a[3] << 8 | a[4]);
    public static byte[] EncodeDpi(byte storage, int x, int y) => new[] { storage, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)0, (byte)0 };

    /// <summary>[storage, active (1-based), count, then per stage: index, xHi, xLo, yHi, yLo, 0, 0].</summary>
    public static (int Active, List<DpiStage> Stages) DecodeStages(ReadOnlySpan<byte> a)
    {
        var stages = new List<DpiStage>();
        for (var i = 0; i < Math.Min((int)a[2], MaxStages); i++)
        {
            var o = StageOffset + i * StageLength;
            stages.Add(new DpiStage(a[o + 1] << 8 | a[o + 2], a[o + 3] << 8 | a[o + 4]));
        }
        return (a[1], stages);
    }

    /// <summary>
    /// The stage table with another active stage. Read-modify-write: every byte of the table as the device returned it except the active
    /// stage, always the full 0x26 bytes.
    /// </summary>
    public static byte[] WithActiveStage(ReadOnlySpan<byte> table, int active)
    {
        if (active < 1 || active > Math.Min((int)table[2], MaxStages)) throw new ArgumentOutOfRangeException(nameof(active));
        var copy = table[..0x26].ToArray();
        copy[1] = (byte)active;
        return copy;
    }

    /// <summary>Legacy polling: 1000 / Hz in one byte. Extended: 8000 / Hz in the second byte (the first echoes the request's 0).</summary>
    public static int DecodePolling(ReadOnlySpan<byte> a) => a[0] == 0 ? throw new PeripheralException("The device reported an unknown polling rate.") : 1000 / a[0];
    public static int DecodePollingExtended(ReadOnlySpan<byte> a) => a[1] == 0 ? throw new PeripheralException("The device reported an unknown polling rate.") : 8000 / a[1];

    public static byte Divisor(int ceiling, int hz) =>
        hz > 0 && ceiling % hz == 0 && ceiling / hz is >= 1 and <= 255 ? (byte)(ceiling / hz) : throw new ArgumentOutOfRangeException(nameof(hz), $"{hz} Hz can't be encoded.");

    public static int DecodeBrightness(ReadOnlySpan<byte> a) => Percent(a[2]);

    /// <summary>
    /// Off and spectrum: [storage, led, 00 or 03]. Static: [.., 01, 0, 0, 1, r, g, b]. Breathing in one color: [.., 02, 1, 0, 1, r, g, b].
    /// </summary>
    public static (RazerCommand Command, byte[] Args) EncodeEffect(byte storage, byte led, LightingEffect effect, int color)
    {
        byte r = (byte)(color >> 16), g = (byte)(color >> 8), b = (byte)color;
        return effect switch
        {
            LightingEffect.Off => (EffectSetShort, new byte[] { storage, led, 0x00 }),
            LightingEffect.Spectrum => (EffectSetShort, new byte[] { storage, led, 0x03 }),
            LightingEffect.Static => (EffectSetColor, new byte[] { storage, led, 0x01, 0, 0, 1, r, g, b }),
            LightingEffect.Breathing => (EffectSetColor, new byte[] { storage, led, 0x02, 1, 0, 1, r, g, b }),
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };
    }

    /// <summary>The getter's reply; bytes after the effect's own are leftovers (spectrum answers with old ones) and are ignored.</summary>
    public static PeripheralLighting DecodeEffect(ReadOnlySpan<byte> a)
    {
        var color = a.Length > 8 ? a[6] << 16 | a[7] << 8 | a[8] : 0;
        return a[2] switch
        {
            0x00 => new(LightingEffect.Off, null),
            0x01 => new(LightingEffect.Static, color),
            0x02 when a[3] == 1 => new(LightingEffect.Breathing, color),
            0x03 => new(LightingEffect.Spectrum, null),
            _ => new(LightingEffect.Other, null), // random or two-color breathing, wave, reactive...
        };
    }
}
