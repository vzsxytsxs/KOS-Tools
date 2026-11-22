namespace KosToolbox.Core.Peripherals.AttackShark;

/// <summary>
/// The Attack Shark X11's settings reports (Beken receiver <c>1D57:FA60</c>, MI_02): feature reports on the collection of usage page
/// 0x0B, events (battery heartbeat, write acknowledgements) as 5-byte input report 3 on usage page 0x0A. Every buffer has the report
/// id at [0] and the report's own length at [1]. Layouts from HarukaYamamoto0/attack-shark-x11-driver (MIT) and MiguelSanzBr's
/// decompile of the vendor app, checked against the friend's X11 (firmware 01 07) through the read-only probe. Pure, no I/O.
/// </summary>
public static class AttackSharkCodec
{
    public const byte ReadRequest = 0xA0, Dpi = 0x04, Light = 0x05, Polling = 0x06, Buttons = 0x08, Macro = 0x09, Version = 0x0B;
    public const byte ModelX11 = 0x55, EventReport = 0x03, EventBattery = 0x40, EventBatteryAlt = 0x41, EventAck = 0x50;
    public const int RequestLength = 8;
    public const int DpiMin = 50, DpiMax = 22000, DpiStages = 8, ButtonSlots = 18;
    public const int SleepMinSeconds = 30, SleepMaxSeconds = 1800;

    /// <summary>The length of each report this codec reads (byte [1] of the report, id included).</summary>
    public static int LengthOf(byte id) => id switch
    {
        Dpi => 0x38, Light => 0x0F, Polling => 0x09, Buttons => 0x3B, Version => 0x08,
        _ => throw new ArgumentOutOfRangeException(nameof(id), $"Report {id:X2} isn't read by this driver."),
    };

    /// <summary>The read request (<c>A0 id len 00 param 00 00 00</c>; param = profile 1, 0 for the version). Payload only, no id.</summary>
    public static byte[] ReadRequestPayload(byte id) => new byte[] { id, (byte)LengthOf(id), 0x00, id == Version ? (byte)0 : (byte)1, 0, 0, 0 };

    /// <summary>The A0 reply grants one read when [1] is 01.</summary>
    public static bool Granted(ReadOnlySpan<byte> reply) => reply.Length >= 2 && reply[0] == ReadRequest && reply[1] == 0x01;

    /// <summary>Where the 16-bit big-endian sum of [3..at-1] sits; null for a report without a sum.</summary>
    static int? SumAt(byte id) => id switch { Dpi => 50, Light => 11, Buttons => 57, _ => null };

    /// <summary>The reply is the report asked for: id, length byte and checksum (06: [4] is the complement of [3]).</summary>
    public static bool Valid(ReadOnlySpan<byte> r, byte id)
    {
        var len = LengthOf(id);
        if (r.Length < len || r[0] != id || r[1] != len) return false;
        if (id == Polling) return r[4] == (byte)(0xFF - r[3]);
        return SumAt(id) is not { } at || Sum(r, at) == (r[at] << 8 | r[at + 1]);
    }

    static int Sum(ReadOnlySpan<byte> r, int at)
    {
        var s = 0;
        for (var i = 3; i < at; i++) s += r[i];
        return s & 0xFFFF;
    }

    /// <summary>A copy of <paramref name="report"/> with its checksum recomputed, ready to send.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> report)
    {
        var id = report[0];
        var r = report[..LengthOf(id)].ToArray();
        if (id == Polling) r[4] = (byte)(0xFF - r[3]);
        else if (SumAt(id) is { } at) { var s = Sum(r, at); r[at] = (byte)(s >> 8); r[at + 1] = (byte)s; }
        else throw new ArgumentException($"Report {id:X2} is never written.");
        return r;
    }

    // ---------- events ----------

    /// <summary>A battery heartbeat (<c>03 model 40|41 state percent</c>, every 2 s while linked): model byte, percent, charging (state 03).</summary>
    public static (byte Model, int Percent, bool Charging)? Heartbeat(ReadOnlySpan<byte> e) =>
        e.Length >= 5 && e[0] == EventReport && e[2] is EventBattery or EventBatteryAlt && e[4] <= 100 ? (e[1], e[4], e[3] == 0x03) : null;

    /// <summary>The acknowledgement of a write of report <paramref name="id"/> (<c>03 model 50 status id</c>): true ok, false refused, null not it.</summary>
    public static bool? Ack(ReadOnlySpan<byte> e, byte id) =>
        e.Length >= 5 && e[0] == EventReport && e[2] == EventAck && e[4] == id ? e[3] == 0 : null;

    // ---------- version, polling ----------

    public static string DecodeVersion(ReadOnlySpan<byte> r) => $"{r[2]}.{r[3]:X2}";

    public static readonly int[] Rates = { 1000, 500, 250, 125 };

    public static int? DecodePolling(ReadOnlySpan<byte> r) => r[3] switch { 1 => 1000, 2 => 500, 4 => 250, 8 => 125, _ => null };

    public static byte[] WithPolling(ReadOnlySpan<byte> r, int hz)
    {
        var c = r.ToArray();
        c[3] = hz switch { 1000 => 1, 500 => 2, 250 => 4, 125 => 8, _ => throw new ArgumentOutOfRangeException(nameof(hz)) };
        return Seal(c);
    }

    // ---------- light, sleep (report 05) ----------

    /// <summary>The light mode in [3]'s high nibble: 0 off, 1 static, 2 breathing, 3 neon (colour cycle); 4.. are DPI-colour modes.</summary>
    public static PeripheralLighting DecodeLighting(ReadOnlySpan<byte> r)
    {
        var effect = (r[3] >> 4) switch { 0 => LightingEffect.Off, 1 => LightingEffect.Static, 2 => LightingEffect.Breathing, 3 => LightingEffect.Spectrum, _ => LightingEffect.Other };
        return new PeripheralLighting(effect, effect is LightingEffect.Static or LightingEffect.Breathing ? r[6] << 16 | r[7] << 8 | r[8] : null);
    }

    public static byte[] WithLighting(ReadOnlySpan<byte> r, LightingEffect effect, int color)
    {
        var c = r.ToArray();
        var mode = effect switch { LightingEffect.Off => 0, LightingEffect.Static => 1, LightingEffect.Breathing => 2, LightingEffect.Spectrum => 3, _ => throw new ArgumentOutOfRangeException(nameof(effect)) };
        c[3] = (byte)(mode << 4 | (c[3] & 0x0F));
        if (effect is LightingEffect.Static or LightingEffect.Breathing) { c[6] = (byte)(color >> 16); c[7] = (byte)(color >> 8); c[8] = (byte)color; }
        return Seal(c);
    }

    /// <summary>Brightness level 1..8 in [5]'s low nibble, as a percentage (level × 12.5, rounded).</summary>
    public static int DecodeBrightness(ReadOnlySpan<byte> r) => Percent(r[5] & 0x0F);

    public static int Percent(int level) => (int)Math.Round(level * 100 / 8.0, MidpointRounding.AwayFromZero);

    /// <summary>The level 1..8 nearest to a percentage (0 % is level 1: the mouse has no brightness 0; Off is the effect).</summary>
    public static int Level(int percent) => Math.Clamp((int)Math.Round(percent * 8 / 100.0, MidpointRounding.AwayFromZero), 1, 8);

    public static byte[] WithBrightness(ReadOnlySpan<byte> r, int percent)
    {
        var c = r.ToArray();
        c[5] = (byte)(c[5] & 0xF0 | Level(percent)); // the high nibble is half of the deep-sleep minutes
        return Seal(c);
    }

    /// <summary>Sleep in [9]: half minutes (the friend's raw 1 is the app's 0.5 min).</summary>
    public static int DecodeSleepSeconds(ReadOnlySpan<byte> r) => r[9] * 30;

    public static byte[] WithSleep(ReadOnlySpan<byte> r, int seconds)
    {
        if (seconds % 30 != 0 || seconds is < SleepMinSeconds or > SleepMaxSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        var c = r.ToArray();
        c[9] = (byte)(seconds / 30);
        return Seal(c);
    }

    /// <summary>Deep sleep in minutes: [4]'s high nibble and [5]'s high nibble.</summary>
    public static int DecodeDeepSleepMinutes(ReadOnlySpan<byte> r) => r[4] & 0xF0 | r[5] >> 4;

    /// <summary>Key response (debounce) in [10]: 2 ms units (the friend's raw 3 is the app's 6 ms).</summary>
    public static int DecodeDebounceMs(ReadOnlySpan<byte> r) => r[10] * 2;

    public static byte[] WithDeepSleep(ReadOnlySpan<byte> r, int minutes)
    {
        if (minutes is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(minutes));
        var c = r.ToArray();
        c[4] = (byte)(minutes & 0xF0 | c[4] & 0x0F); // low nibbles: light speed and brightness, kept
        c[5] = (byte)((minutes & 0x0F) << 4 | c[5] & 0x0F);
        return Seal(c);
    }

    /// <summary>Key response 4..50 ms in steps of 2 (the app's slider 2..25).</summary>
    public static byte[] WithDebounce(ReadOnlySpan<byte> r, int ms)
    {
        if (ms is < 4 or > 50 || ms % 2 != 0) throw new ArgumentOutOfRangeException(nameof(ms));
        var c = r.ToArray();
        c[10] = (byte)(ms / 2);
        return Seal(c);
    }

    // ---------- DPI (report 04) ----------

    /// <summary>
    /// PAW3311 sensor codes (HarukaYamamoto0/attack-shark-x11-driver src/tables/dpi-map.ts): index 0..199 = 50..10000 DPI in steps of 50
    /// (flag byte 0), 200..219 = 10100..12000 in steps of 100 (flag byte 1). The two halves share codes 76..8D, so the flag decides.
    /// </summary>
    static readonly byte[] Codes =
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0e, 0x0f, 0x10, 0x11, 0x12, 0x13, 0x15, 0x16,
        0x17, 0x18, 0x19, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x29, 0x2a, 0x2b, 0x2c,
        0x2d, 0x2f, 0x30, 0x31, 0x32, 0x33, 0x34, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3d, 0x3e, 0x3f, 0x40, 0x41, 0x43,
        0x44, 0x45, 0x46, 0x47, 0x48, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f, 0x51, 0x52, 0x53, 0x54, 0x55, 0x57, 0x58, 0x59,
        0x5a, 0x5b, 0x5c, 0x5e, 0x5f, 0x60, 0x61, 0x62, 0x63, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f,
        0x70, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7f, 0x80, 0x81, 0x82, 0x83, 0x84, 0x86,
        0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8d, 0x8e, 0x8f, 0x90, 0x91, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x9a, 0x9b, 0x9c,
        0x9d, 0x9e, 0x9f, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa7, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xae, 0xaf, 0xb0, 0xb1, 0xb2,
        0xb3, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf, 0xc0, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc9,
        0xca, 0xcb, 0xcc, 0xcd, 0xcf, 0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xdb, 0xdd, 0xde, 0xdf,
        0xe0, 0xe1, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xea, 0xeb, 0x76, 0x77, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7f, 0x80,
        0x81, 0x82, 0x83, 0x84, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8d,
    };

    /// <summary>The DPI of stage <paramref name="i"/> (0-based): code [8+i], range flag [16+i], doubling bit i of [6]. Null for an unknown code.</summary>
    public static int? DecodeStage(ReadOnlySpan<byte> r, int i)
    {
        var high = r[16 + i] != 0;
        var index = high ? Array.IndexOf(Codes, r[8 + i], 200) : Array.IndexOf(Codes, r[8 + i], 0, 200);
        if (index < 0) return null;
        var dpi = high ? 10100 + (index - 200) * 100 : 50 + index * 50;
        return (r[6] >> i & 1) != 0 ? dpi * 2 : dpi;
    }

    /// <summary>The enabled stages (the low bits of [5] must be contiguous) and the active one (1-based, [24]); null when not decodable.</summary>
    public static (IReadOnlyList<int> Stages, int Active)? DecodeStages(ReadOnlySpan<byte> r)
    {
        var mask = r[5];
        var count = 0;
        while (count < DpiStages && (mask >> count & 1) != 0) count++;
        if (count == 0 || mask >> count != 0) return null;
        var stages = new List<int>();
        for (var i = 0; i < count; i++)
            if (DecodeStage(r, i) is { } dpi) stages.Add(dpi);
            else return null;
        return r[24] is >= 1 && r[24] <= count ? (stages, r[24]) : null;
    }

    /// <summary>
    /// Whether <paramref name="dpi"/> can be written (the vendor app's slider, MiguelSanzBr's decompile): 50..10000 in steps of 50,
    /// 10100..20000 in steps of 100, 20200..22000 in steps of 200.
    /// </summary>
    public static bool ValidDpi(int dpi) => dpi switch
    {
        >= DpiMin and <= 10000 => dpi % 50 == 0,
        > 10000 and <= 20000 => dpi % 100 == 0,
        > 20000 and <= DpiMax => dpi % 200 == 0,
        _ => false,
    };

    /// <summary>Stage <paramref name="i"/> (0-based) set to <paramref name="dpi"/>; every other byte as read (other stages stay byte-identical).</summary>
    public static byte[] WithStageDpi(ReadOnlySpan<byte> r, int i, int dpi)
    {
        if (!ValidDpi(dpi)) throw new ArgumentOutOfRangeException(nameof(dpi));
        var c = r.ToArray();
        var doubled = dpi > 12000;        // above 12000 the sensor runs at half and the stage's bit in [6] doubles it
        var value = doubled ? dpi / 2 : dpi;
        var high = value > 10000;
        c[8 + i] = Codes[high ? 200 + (value - 10100) / 100 : (value - 50) / 50];
        c[16 + i] = (byte)(high ? 1 : 0);
        var bit = 1 << i;
        c[6] = (byte)(doubled ? c[6] | bit : c[6] & ~bit); // [7] is a copy of [6] (the friend's X11: both 20 for its 22000 stage)
        c[7] = (byte)(doubled ? c[7] | bit : c[7] & ~bit);
        return Seal(c);
    }

    /// <summary>The colour of stage <paramref name="i"/> (0-based), shown when the stage is chosen: [25 + 3i..].</summary>
    public static int DecodeStageColor(ReadOnlySpan<byte> r, int i) => r[25 + 3 * i] << 16 | r[26 + 3 * i] << 8 | r[27 + 3 * i];

    public static byte[] WithStageColor(ReadOnlySpan<byte> r, int i, int color)
    {
        var c = r.ToArray();
        c[25 + 3 * i] = (byte)(color >> 16); c[26 + 3 * i] = (byte)(color >> 8); c[27 + 3 * i] = (byte)color;
        return Seal(c);
    }

    /// <summary>Angle snap in [3]'s low nibble, ripple control in [4]'s (the high nibbles, lift-off and motion sync, aren't used on the X11).</summary>
    public static bool DecodeAngleSnap(ReadOnlySpan<byte> r) => (r[3] & 0x0F) != 0;
    public static bool DecodeRipple(ReadOnlySpan<byte> r) => (r[4] & 0x0F) != 0;

    public static byte[] WithAngleSnap(ReadOnlySpan<byte> r, bool on) { var c = r.ToArray(); c[3] = (byte)(c[3] & 0xF0 | (on ? 1 : 0)); return Seal(c); }
    public static byte[] WithRipple(ReadOnlySpan<byte> r, bool on) { var c = r.ToArray(); c[4] = (byte)(c[4] & 0xF0 | (on ? 1 : 0)); return Seal(c); }

    // ---------- buttons (report 08), macros (report 09) ----------

    /// <summary>
    /// Button slot <paramref name="slot"/> (0..17) as its three bytes: action, modifiers, value. X11 slots: 0 left, 1 right, 2 middle,
    /// 5 DPI cycle, 6 the app's "4" (forward), 7 its "5" (back), 16/17 wheel. Actions (MiguelSanzBr's decompile, table 0x4ec3b8):
    /// 01 off, 02 left, 03 right, 04 middle, 05 back, 06 forward, 07 double click, 08 fire, 09/0A wheel, 0B/0C tilt, 0D/0E/0F DPI
    /// cycle/up/down, 11 key (mods bit0 Ctrl, 1 Shift, 2 Alt, 3 Win; HID key), 12 macro (00, slot), 15..1C media, 1D.. apps/browser.
    /// </summary>
    public static ButtonAction DecodeButton(ReadOnlySpan<byte> r, int slot) => new(r[3 + 3 * slot], r[4 + 3 * slot], r[5 + 3 * slot]);

    public static byte[] WithButton(ReadOnlySpan<byte> r, int slot, ButtonAction action)
    {
        if (slot is < 0 or >= ButtonSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        var c = r.ToArray();
        c[3 + 3 * slot] = action.Action; c[4 + 3 * slot] = action.Modifiers; c[5 + 3 * slot] = action.Value;
        // The vendor app refuses a map without a left click: the mouse would have no primary button.
        if (!Enumerable.Range(0, ButtonSlots).Any(k => c[3 + 3 * k] == ButtonAction.Left.Action))
            throw new ArgumentException("At least one button must stay a left click.");
        return Seal(c);
    }

    public const int MacroItemBytes = 100, MacroReportLength = 64;

    /// <summary>
    /// A macro as the three 64-byte feature reports the vendor app sends (MiguelSanzBr's decompile, section 3.8): a logical buffer
    /// L[0]=09, L[1]=83, L[2]=slot, L[3]=play mode, L[4..6]=0, L[7]=loops (at least 1), L[28]=item count, L[29..128] items,
    /// L[129..130] the sum of L[3..128] big-endian; report j is <c>09 len slot j</c> + L[3+60j..]. Each step is a key down or up
    /// with the delay before it, in 10 ms units up to 1270 ms: <c>[delay/10 | (down ? 01 : 81), key]</c> (the 01 copied from the
    /// app; with 10 ms delays it matches HarukaYamamoto0's encoding too). <paramref name="lastLength"/> is the length byte of the
    /// third report: 0C as the app sends it over the receiver, 40 as the Linux driver sends it.
    /// </summary>
    public static byte[][] EncodeMacro(int slot, IReadOnlyList<MacroStep> steps, int playMode = 0, int loops = 1, byte lastLength = 0x0C)
    {
        if (slot is < 1 or > ButtonSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        if (steps.Count == 0 || steps.Count * 2 > MacroItemBytes) throw new ArgumentOutOfRangeException(nameof(steps), "A macro takes 1 to 50 steps.");
        if (loops is < 1 or > 255 || playMode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(loops));
        var l = new byte[131];
        l[0] = Macro; l[1] = 0x83; l[2] = (byte)slot; l[3] = (byte)playMode; l[7] = (byte)loops; l[28] = (byte)steps.Count;
        for (var i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            if (st.DelayMs is < 0 or > 1270) throw new ArgumentOutOfRangeException(nameof(steps), "A step's delay goes up to 1270 ms.");
            var units = (int)Math.Round(st.DelayMs / 10.0, MidpointRounding.AwayFromZero);
            l[29 + 2 * i] = (byte)(units | (st.Down ? 0x01 : 0x81));
            l[30 + 2 * i] = st.Key;
        }
        var sum = 0;
        for (var i = 3; i <= 128; i++) sum += l[i];
        l[129] = (byte)(sum >> 8); l[130] = (byte)sum;
        var reports = new byte[3][];
        for (var j = 0; j < 3; j++)
        {
            var r = new byte[MacroReportLength];
            r[0] = Macro; r[1] = j < 2 ? (byte)0x40 : lastLength; r[2] = (byte)slot; r[3] = (byte)j;
            l.AsSpan(3 + 60 * j, Math.Min(60, l.Length - (3 + 60 * j))).CopyTo(r.AsSpan(4));
            reports[j] = r;
        }
        return reports;
    }

    public static byte[] WithActiveStage(ReadOnlySpan<byte> r, int stage)
    {
        var c = r.ToArray();
        c[24] = (byte)stage;
        return Seal(c);
    }
}

/// <summary>A button's function: action code, modifiers and value (a HID key, or a macro slot).</summary>
public readonly record struct ButtonAction(byte Action, byte Modifiers, byte Value)
{
    public static readonly ButtonAction Off = new(0x01, 0, 0), Left = new(0x02, 0, 0), Right = new(0x03, 0, 0), Middle = new(0x04, 0, 0),
        Back = new(0x05, 0, 0), Forward = new(0x06, 0, 0), DpiCycle = new(0x0D, 0, 0);
    /// <summary>A key with modifiers (bit 0 Ctrl, 1 Shift, 2 Alt, 3 Win) by its HID usage (0x04 = A).</summary>
    public static ButtonAction Key(byte usage, byte modifiers = 0) => new(0x11, modifiers, usage);
    public static ButtonAction RunMacro(int slot) => new(0x12, 0, (byte)slot);
    public override string ToString() => $"{Action:X2} {Modifiers:X2} {Value:X2}";
}

/// <summary>One macro step: a key (HID usage; F1..F5 are mouse buttons) going down or up after <paramref name="DelayMs"/>.</summary>
public readonly record struct MacroStep(byte Key, bool Down, int DelayMs = 10);
