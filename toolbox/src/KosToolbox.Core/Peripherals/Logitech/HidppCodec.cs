namespace KosToolbox.Core.Peripherals.Logitech;

public enum HidppMatch { Other, Reply, Error }

/// <summary>
/// Logitech HID++ messages. Win32 buffers include the report id: short 0x10 (7 bytes), long 0x11 (20 bytes):
/// [id][device index][feature index][function &lt;&lt; 4 | software id][params…]. Wire facts from the Logitech HID++ 2.0 drafts as
/// summarised by Wireless Battery (src/drivers/logitech.cpp) and mouse-protocol (src/drivers/logitech/hidpp.ts).
/// </summary>
public static class HidppCodec
{
    public const byte ShortId = 0x10, LongId = 0x11, Direct = 0xFF;
    public const int ShortLength = 7, LongLength = 20;
    /// <summary>Ours, non-zero (0 marks notifications); replies to other programs carry theirs.</summary>
    public const byte SoftwareId = 0x0B;

    public const ushort Root = 0x0000, FirmwareInfo = 0x0003, DeviceName = 0x0005, UnifiedBattery = 0x1004, BatteryStatus = 0x1000,
        BatteryVoltage = 0x1001, AdjustableDpi = 0x2201, ReportRate = 0x8060, OnboardProfiles = 0x8100;

    /// <summary>The payload after the report id: device index, feature index, function and software id, then the parameters.</summary>
    public static byte[] Request(byte device, byte feature, byte function, ReadOnlySpan<byte> parameters)
    {
        var p = new byte[3 + parameters.Length];
        p[0] = device;
        p[1] = feature;
        p[2] = (byte)(function << 4 | SoftwareId);
        parameters.CopyTo(p.AsSpan(3));
        return p;
    }

    /// <summary>
    /// Whether <paramref name="report"/> (report id at [0]) answers the request: a reply with the same device, feature and function byte,
    /// or an error naming them (HID++ 2.0: feature FF; HID++ 1.0: 8F). The parameters of a reply; the error code of an error.
    /// </summary>
    public static (HidppMatch Kind, byte Code, byte[] Params) Match(ReadOnlySpan<byte> report, byte device, byte feature, byte function)
    {
        if (report.Length < ShortLength || report[0] is not (ShortId or LongId) || report[1] != device) return (HidppMatch.Other, 0, Array.Empty<byte>());
        var fnSw = (byte)(function << 4 | SoftwareId);
        if (report[2] == feature && report[3] == fnSw) return (HidppMatch.Reply, 0, report[4..].ToArray());
        if (report[2] is 0xFF or 0x8F && report[3] == feature && report[4] == fnSw) return (HidppMatch.Error, report[5], Array.Empty<byte>());
        return (HidppMatch.Other, 0, Array.Empty<byte>());
    }

    // ---------- battery ----------
    /// <summary>0x1004 get status (fn 1): [0] percent (0 = level only), [1] level bits (8 full, 4 good, 2 low, else critical), [2] 1–3 charging.</summary>
    public static PeripheralBattery Unified(ReadOnlySpan<byte> p)
    {
        var charging = p[2] is >= 1 and <= 3;
        if (p[0] is > 0 and <= 100) return new PeripheralBattery(p[0], charging);
        var level = (p[1] & 8) != 0 ? "Full" : (p[1] & 4) != 0 ? "Good" : (p[1] & 2) != 0 ? "Low" : "Critical";
        return new PeripheralBattery(null, charging, Level: level);
    }

    /// <summary>0x1000 (fn 0): [0] percent, [2] 1–4 charging; 0 % with state 3 means full.</summary>
    public static PeripheralBattery Status(ReadOnlySpan<byte> p) =>
        new(p[0] == 0 && p[2] == 3 ? 100 : p[0] <= 100 ? p[0] : null, p[2] is >= 1 and <= 4);

    /// <summary>0x1001 (fn 0): millivolts big-endian in [0..1], bit 7 of [2] charging; percent from a Li-ion discharge curve.</summary>
    public static PeripheralBattery Voltage(ReadOnlySpan<byte> p)
    {
        var mv = p[0] << 8 | p[1];
        return new PeripheralBattery(mv > 0 ? LiIonPercent(mv) : null, (p[2] & 0x80) != 0);
    }

    // A typical single Li-ion cell under light load (same curve shape as Wireless Battery's LiIonPercent table).
    static readonly (int Mv, int Percent)[] Curve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    public static int LiIonPercent(int mv)
    {
        if (mv >= Curve[0].Mv) return 100;
        for (var i = 1; i < Curve.Length; i++)
            if (mv >= Curve[i].Mv)
            {
                var (hiMv, hiP) = Curve[i - 1];
                var (loMv, loP) = Curve[i];
                return loP + (mv - loMv) * (hiP - loP) / (hiMv - loMv);
            }
        return 0;
    }

    /// <summary>HID++ 1.0 register 0x0D read: [0] percent, [2] 0x21–0x26 charging.</summary>
    public static PeripheralBattery? Register0D(ReadOnlySpan<byte> p) =>
        p[0] is > 0 and <= 100 ? new PeripheralBattery(p[0], p[2] is >= 0x21 and <= 0x26) : null;

    // ---------- settings ----------
    /// <summary>
    /// 0x2201 fn 1 (getSensorDpiList, after the sensor byte): big-endian values ending with 0; an entry 0xE000 | step marks a range from the
    /// value before it to the value after it. Min, max and the step (1 for a list of single values).
    /// </summary>
    public static (int Min, int Max, int Step)? DpiRange(ReadOnlySpan<byte> list)
    {
        var values = new List<int>();
        var step = 0;
        for (var i = 0; i + 1 < list.Length; i += 2)
        {
            var v = list[i] << 8 | list[i + 1];
            if (v == 0) break;
            if (v >> 13 == 0b111) step = v & 0x1FFF;
            else values.Add(v);
        }
        if (values.Count == 0) return null;
        if (step == 0 && values.Count > 1) step = values.Zip(values.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(1).Min();
        return (values.Min(), values.Max(), Math.Max(step, 1));
    }

    /// <summary>0x8060 fn 0: bit i set = a (i + 1) ms report interval; as rates in Hz, fastest last.</summary>
    public static List<int> Rates(byte bitmap) =>
        Enumerable.Range(0, 8).Where(i => (bitmap & 1 << i) != 0).Select(i => 1000 / (i + 1)).OrderBy(hz => hz).ToList();

    /// <summary>0x0003 fn 1 for an entity: [0] type (0 = main application), [1..3] name prefix, [4] version (BCD), [5] revision (BCD), [6..7] build.</summary>
    public static string Firmware(ReadOnlySpan<byte> p)
    {
        var prefix = System.Text.Encoding.ASCII.GetString(p.Slice(1, 3)).Trim('\0', ' ');
        return $"{prefix} {p[4]:X2}.{p[5]:X2}.B{(p[6] << 8 | p[7]):X4}".Trim();
    }
}
