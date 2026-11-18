using System.Buffers.Binary;

namespace KosToolbox.Core.Peripherals.Royuan;

/// <summary>
/// The switch columns of the active profile, one value per key slot (128): actuation, release (lift) point, rapid-trigger press and
/// release steps, dynamic-keystroke first point, bottom dead zone (u16, in the board's travel unit); mod-tap hold time (10 ms units),
/// the key mode (bit 7 rapid trigger, bits 0..6 the kind: 0 plain, 2 dynamic keystroke, 3 mod-tap, 4/5 toggle, 7 snap) and the snap
/// partner (255 none); <paramref name="DksActions"/> is four blocks of 128 (one byte per slot for sub-layers 0..3, four 2-bit cells).
/// A slot whose actuation is 0 has no key.
/// </summary>
public sealed record RoyuanColumns(ushort[] Actuation, ushort[] Release, ushort[] RtPress, ushort[] RtRelease, ushort[] DeadZone, byte[] Mode,
    ushort[] DksPoint, byte[] HoldTime, byte[] Partner, byte[] DksActions)
{
    public IEnumerable<int> Keys => Enumerable.Range(0, RoyuanCodec.Slots).Where(i => Actuation[i] != 0);

    /// <summary>The u16 columns with their sub-op.</summary>
    public (byte SubOp, ushort[] Values)[] Travel => new[]
    {
        (RoyuanCodec.ActuationColumn, Actuation), (RoyuanCodec.ReleaseColumn, Release), (RoyuanCodec.RtPressColumn, RtPress),
        (RoyuanCodec.RtReleaseColumn, RtRelease), (RoyuanCodec.DksPointColumn, DksPoint), (RoyuanCodec.DeadZoneColumn, DeadZone),
    };

    /// <summary>
    /// The byte columns with their sub-op, in the order they're written: hold time (and the DKS actions) before the mode, so a key never
    /// takes a kind before its data; the snap partner after it (on an R82HE, partners sent while the keys weren't snap yet read back 255).
    /// </summary>
    public (byte SubOp, byte[] Values)[] Bytes => new[]
    {
        (RoyuanCodec.HoldTimeColumn, HoldTime), (RoyuanCodec.ModeColumn, Mode), (RoyuanCodec.PartnerColumn, Partner),
    };

    /// <summary>A slot's four DKS action bytes (sub-layers 0..3).</summary>
    public byte[] Actions(int slot) => Enumerable.Range(0, 4).Select(l => DksActions[l * RoyuanCodec.Slots + slot]).ToArray();

    public RoyuanColumns Copy() => new((ushort[])Actuation.Clone(), (ushort[])Release.Clone(), (ushort[])RtPress.Clone(),
        (ushort[])RtRelease.Clone(), (ushort[])DeadZone.Clone(), (byte[])Mode.Clone(), (ushort[])DksPoint.Clone(), (byte[])HoldTime.Clone(),
        (byte[])Partner.Clone(), (byte[])DksActions.Clone());
}

/// <summary>The stored lighting as the board keeps it: mode, wire speed (0 = fastest), brightness 0..4, option/flags byte, color.</summary>
public sealed record RoyuanLight(byte Mode, byte Speed, byte Brightness, byte Flags, int Color);

/// <summary>
/// ROYUAN "gen2" keyboards (ry5088 firmware, e.g. Attack Shark R82HE): 64-byte feature report 0 on collection FFFF:0002. Facts from
/// the sharkfin project's docs/PROTOCOL.md (GPL-3.0, github.com/dniminenn/sharkfin; only the protocol facts, no code) and the vendor's
/// WebHID driver, checked on an R82HE (firmware 5.05). A request is SET then GET; the reply is written into the request buffer, so it
/// echoes the opcode at payload [0] and keeps the request's checksum. Column reads (0xE5) answer raw pages with no echo.
/// All offsets below are Win32 buffer offsets (report id at [0], payload + 1).
/// </summary>
public static class RoyuanCodec
{
    public const ushort UsagePage = 0xFFFF, Usage = 0x0002;
    public const int PayloadLength = 64;
    public const byte Identify = 0x8F, GetPolling = 0x83, SetPolling = 0x03, GetLight = 0x87, SetLight = 0x07,
                      GetProfile = 0x84, SetProfile = 0x04, GetColumn = 0xE5, SetColumn = 0x65, TravelStream = 0x1B, GetKeymap = 0x8A, SetKeymap = 0x0A,
                      GetFn = 0x90, SetFn = 0x10, GetMacro = 0x8B, SetMacro = 0x0B, SetPicture = 0x0C;

    /// <summary>
    /// The only opcodes ever built. Never: 0xAC (erases flash), 0x7F (bootloader), 0x1C/0x1E (sensor calibration), 0x8C (the picture
    /// read: stale data), 0x01 (factory reset), and any yc500-family opcode (they collide with live gen2 registers). 0x0C (the per-key
    /// picture) only through <see cref="PicturePages"/>, paced by the driver: fast uploads stall the endpoint until USB is reconnected.
    /// </summary>
    static readonly HashSet<byte> Allowed = new() { Identify, GetPolling, SetPolling, GetLight, SetLight, GetProfile, SetProfile, GetColumn, SetColumn, TravelStream, GetKeymap, SetKeymap, GetFn, SetFn, GetMacro, SetMacro, SetPicture };

    /// <summary>Wire index → Hz.</summary>
    public static readonly int[] PollingRates = { 8000, 4000, 2000, 1000, 500, 250, 125 };

    /// <summary>Lighting flags 0..6: a preset color instead of the packet's RGB. 7 = the packet's color, 8 = rainbow.</summary>
    static readonly int[] PresetColors = { 0xFF0000, 0xFF8000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF };
    public const byte FixedColor = 7, Rainbow = 8;

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)(0xFF - (sum & 0xFF));
    }

    /// <summary>
    /// A 64-byte payload: <paramref name="head"/> from [0], the checksum of the bytes before it at [7] (Bit7) or [8] (Bit8, lighting
    /// only), <paramref name="data"/> after the checksum. Refuses any opcode outside the allow-list.
    /// </summary>
    public static byte[] Request(ReadOnlySpan<byte> head, ReadOnlySpan<byte> data = default, bool bit8 = false)
    {
        if (head.IsEmpty || !Allowed.Contains(head[0])) throw new ArgumentException($"Opcode {(head.IsEmpty ? "(none)" : $"0x{head[0]:X2}")} is not on the allow-list.");
        var ck = bit8 ? 8 : 7;
        if (head.Length > ck || data.Length > PayloadLength - ck - 1) throw new ArgumentException("The request doesn't fit the report.");
        var p = new byte[PayloadLength];
        head.CopyTo(p);
        data.CopyTo(p.AsSpan(ck + 1));
        p[ck] = Checksum(p.AsSpan(0, ck));
        return p;
    }

    /// <summary>A reply (report id at [0]) echoing <paramref name="command"/>.</summary>
    public static bool Answers(ReadOnlySpan<byte> r, byte command) => r.Length == PayloadLength + 1 && r[1] == command;

    public static uint DeviceId(ReadOnlySpan<byte> r) => BinaryPrimitives.ReadUInt32LittleEndian(r[2..6]);
    /// <summary>Firmware as BCD nibbles: 0x0505 is "5.05".</summary>
    public static int Version(ReadOnlySpan<byte> r) => r[9] << 8 | r[8];
    public static string VersionText(int v) => $"{v >> 8:X}.{v & 0xFF:X2}";

    public static int? DecodePolling(ReadOnlySpan<byte> r) => r[3] < PollingRates.Length ? PollingRates[r[3]] : null;
    public static byte[] EncodePolling(int hz)
    {
        var i = Array.IndexOf(PollingRates, hz);
        if (i < 0) throw new ArgumentOutOfRangeException(nameof(hz));
        return Request(new byte[] { SetPolling, 0, (byte)i });
    }

    public static int DecodeProfile(ReadOnlySpan<byte> r) => r[2];
    public static byte[] EncodeProfile(int zeroBased) => Request(new byte[] { SetProfile, (byte)zeroBased });

    public static RoyuanLight DecodeLight(ReadOnlySpan<byte> r) => new(r[2], r[3], r[4], r[5], r[6] << 16 | r[7] << 8 | r[8]);

    /// <summary>The lighting write (Bit8 checksum). White is sent as FAFAFA, as the vendor does.</summary>
    public static byte[] EncodeLight(RoyuanLight l)
    {
        var c = l.Color == 0xFFFFFF ? 0xFAFAFA : l.Color;
        return Request(new[] { SetLight, l.Mode, l.Speed, l.Brightness, l.Flags, (byte)(c >> 16), (byte)(c >> 8), (byte)c }, bit8: true);
    }

    /// <summary>What the toolbox shows: static/breathing with their color, spectrum; any other mode is <see cref="LightingEffect.Other"/>.</summary>
    public static PeripheralLighting ToLighting(RoyuanLight l)
    {
        var flags = l.Flags & 0x0F;
        int? color = flags < PresetColors.Length ? PresetColors[flags] : flags == FixedColor ? (l.Color == 0xFAFAFA ? 0xFFFFFF : l.Color) : null;
        return l.Mode switch
        {
            1 when color is not null => new(LightingEffect.Static, color),
            2 when color is not null => new(LightingEffect.Breathing, color),
            3 => new(LightingEffect.Spectrum, null),
            _ => new(LightingEffect.Other, null),
        };
    }

    // ---------- magnetic columns (one value per key slot, 128 slots) ----------
    public const int Slots = 128;
    public const byte ActuationColumn = 0, ReleaseColumn = 1, RtPressColumn = 2, RtReleaseColumn = 3, DksPointColumn = 4, HoldTimeColumn = 5,
        DeadZoneColumn = 6, ModeColumn = 7, DksActionsColumn = 8, PartnerColumn = 9, AllDksActionsColumn = 10, RapidTriggerBit = 0x80, NoPartner = 0xFF;
    public const byte KindPlain = 0, KindDks = 2, KindModTap = 3, KindToggle = 4, KindToggleRepeat = 5, KindSnap = 7;

    /// <summary>One slot's four DKS action bytes (sub-op 8 has no bulk form): [65, 8, 0, slot, 1] + the bytes at [8].</summary>
    public static byte[] DksActionsWrite(int slot, ReadOnlySpan<byte> actions) =>
        actions.Length == 4 ? Request(new byte[] { SetColumn, DksActionsColumn, 0, (byte)slot, 1 }, actions) : throw new ArgumentException("Four action bytes.");

    /// <summary>One slot of a column, stored at once: [65, sub-op, 0, slot, 1] + the value at [8] (u16 LE; a byte column uses [8]).</summary>
    public static byte[] ColumnWriteOne(byte subop, int slot, ushort value) =>
        Request(new byte[] { SetColumn, subop, 0, (byte)slot, 1 }, new[] { (byte)value, (byte)(value >> 8) });

    /// <summary>One keymap entry of one sub-layer, stored at once: [0A, profile, slot, 0, 0, 1, sub-layer] + the entry at [8].</summary>
    public static byte[] KeymapWrite(int profile, int slot, int subLayer, RoyuanKey key) =>
        Request(new byte[] { SetKeymap, (byte)profile, (byte)slot, 0, 0, 1, (byte)subLayer }, new[] { key.Type, key.A, key.B, key.C });

    /// <summary>A column page, not the board handing the request back (what it does with a read it doesn't handle).</summary>
    public static bool IsPage(ReadOnlySpan<byte> r, byte subop, int page) =>
        r.Length == PayloadLength + 1 && !(r[1] == GetColumn && r[2] == subop && r[3] == 1 && r[4] == page);

    /// <summary>What most keys have (null without keys); not uniform when the keys differ from each other in what <see cref="WithTravel"/> writes.</summary>
    public static PeripheralMagnetic? Summarize(RoyuanColumns c, double unitMm, IEnumerable<int>? only = null)
    {
        var keys = (only is null ? c.Keys : c.Keys.Intersect(only)).ToList();
        if (keys.Count == 0) return null;
        static T Most<T>(IEnumerable<T> v) => v.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        double Mm(ushort counts) => Math.Round(counts * unitMm, 3);
        var rt = Most(keys.Select(k => (c.Mode[k] & RapidTriggerBit) != 0));
        var uniform = keys.All(k => c.Actuation[k] == c.Actuation[keys[0]] && c.Release[k] == c.Release[keys[0]] && c.DeadZone[k] == c.DeadZone[keys[0]]
            && (c.Mode[k] & RapidTriggerBit) == (c.Mode[keys[0]] & RapidTriggerBit)
            && (!rt || c.RtPress[k] == c.RtPress[keys[0]] && c.RtRelease[k] == c.RtRelease[keys[0]]));
        return new PeripheralMagnetic(Mm(Most(keys.Select(k => c.Actuation[k]))), rt, Mm(Most(keys.Select(k => c.RtPress[k]))),
            Mm(Most(keys.Select(k => c.RtRelease[k]))), Mm(Most(keys.Select(k => c.DeadZone[k]))), uniform, unitMm);
    }

    /// <summary>
    /// The columns with the same travel on every key (slots without a key stay as they are): actuation, release point = actuation,
    /// bottom dead zone, the rapid-trigger bit (each key's kind in bits 0..6 kept) and, with rapid trigger on, its steps.
    /// </summary>
    public static RoyuanColumns WithTravel(RoyuanColumns c, ushort actuation, bool rapidTrigger, ushort rtPress, ushort rtRelease, ushort deadZone,
        IEnumerable<int>? only = null)
    {
        var w = c.Copy();
        var keys = only is null ? c.Keys : c.Keys.Intersect(only);
        foreach (var k in keys)
        {
            w.Actuation[k] = w.Release[k] = actuation;
            w.DeadZone[k] = deadZone;
            w.Mode[k] = (byte)(c.Mode[k] & ~RapidTriggerBit | (rapidTrigger ? RapidTriggerBit : 0));
            if (rapidTrigger) { w.RtPress[k] = rtPress; w.RtRelease[k] = rtRelease; }
        }
        return w;
    }

    /// <summary>Column read: [E5, subop, 1, page]; u16 columns answer 4 pages of 32 values, u8 columns 2 pages of 64.</summary>
    public static byte[] ColumnRead(byte subop, int page) => Request(new byte[] { GetColumn, subop, 1, (byte)page });

    public static void DecodeU16Page(ReadOnlySpan<byte> r, int page, ushort[] into)
    {
        for (var i = 0; i < 32; i++) into[page * 32 + i] = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(1 + 2 * i, 2));
    }

    public static void DecodeU8Page(ReadOnlySpan<byte> r, int page, byte[] into) => r.Slice(1, 64).CopyTo(into.AsSpan(page * 64));

    /// <summary>
    /// The whole-column write of 128 u16 values: pages 0..4 of 28 values (the last 16) at [8], [0x65, subop, 1, page, last]. Only the
    /// last page has <c>last</c> set: that one stores to flash and applies; the others stay in RAM.
    /// </summary>
    public static List<byte[]> ColumnWriteU16(byte subop, IReadOnlyList<ushort> values)
    {
        if (values.Count != Slots) throw new ArgumentException($"A column has {Slots} values.");
        var pages = new List<byte[]>();
        for (int page = 0, from = 0; from < Slots; page++, from += 28)
        {
            var n = Math.Min(28, Slots - from);
            var data = new byte[n * 2];
            for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * i), values[from + i]);
            pages.Add(Request(new byte[] { SetColumn, subop, 1, (byte)page, (byte)(from + n == Slots ? 1 : 0) }, data));
        }
        return pages;
    }

    /// <summary>The whole-column write of 128 u8 values: pages 0..2 of 56 (the last 16).</summary>
    public static List<byte[]> ColumnWriteU8(byte subop, IReadOnlyList<byte> values)
    {
        if (values.Count != Slots) throw new ArgumentException($"A column has {Slots} values.");
        var pages = new List<byte[]>();
        for (int page = 0, from = 0; from < Slots; page++, from += 56)
        {
            var n = Math.Min(56, Slots - from);
            pages.Add(Request(new byte[] { SetColumn, subop, 1, (byte)page, (byte)(from + n == Slots ? 1 : 0) }, values.Skip(from).Take(n).ToArray()));
        }
        return pages;
    }

    /// <summary>Keymap read: [8A, profile, FF, page, sub-layer], 8 raw pages of 16 slots × 4 bytes, no echo.</summary>
    public static byte[] KeymapRead(int profile, int page, int subLayer) => Request(new byte[] { GetKeymap, (byte)profile, 0xFF, (byte)page, (byte)subLayer });

    public static void DecodeKeymapPage(ReadOnlySpan<byte> r, int page, RoyuanKey[] into)
    {
        for (var i = 0; i < 16; i++) into[page * 16 + i] = new RoyuanKey(r[1 + 4 * i], r[2 + 4 * i], r[3 + 4 * i], r[4 + 4 * i]);
    }

    /// <summary>Fn layer read (host layer 0, Windows): [90, 0, profile, FF, page], 8 raw pages.</summary>
    public static byte[] FnRead(int profile, int page) => Request(new byte[] { GetFn, 0, (byte)profile, 0xFF, (byte)page });
    public static bool IsFnPage(ReadOnlySpan<byte> r, int profile, int page) =>
        r.Length == PayloadLength + 1 && !(r[1] == GetFn && r[2] == 0 && r[3] == profile && r[4] == 0xFF && r[5] == page);
    /// <summary>One Fn-layer entry (Windows): [10, 0, profile, slot] + the entry at [8].</summary>
    public static byte[] FnWrite(int profile, int slot, RoyuanKey key) =>
        Request(new byte[] { SetFn, 0, (byte)profile, (byte)slot }, new[] { key.Type, key.A, key.B, key.C });

    /// <summary>Macro read: [8B, slot, page], pages 0..3, raw 64 bytes.</summary>
    public static byte[] MacroRead(int slot, int page) => Request(new byte[] { GetMacro, (byte)slot, (byte)page });
    public static bool IsMacroPage(ReadOnlySpan<byte> r, int slot, int page) =>
        r.Length == PayloadLength + 1 && !(r[1] == GetMacro && r[2] == slot && r[3] == page);

    /// <summary>The per-key picture holds 126 keys (slots 0..125), RGB each: 378 bytes.</summary>
    public const int PictureKeys = 126;

    /// <summary>
    /// The per-key picture upload: seven pages [0C, picture slot, FF, page, length (56, then 42), last on page 6] + RGB data at [8], in
    /// slot order. Light mode 13 shows it, its option nibble picking the picture slot. Facts from sharkfin's docs/PROTOCOL.md
    /// ("Per-key colour", gen2 [FW]).
    /// </summary>
    public static List<byte[]> PicturePages(int pictureSlot, IReadOnlyList<int> colors)
    {
        if (pictureSlot is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(pictureSlot));
        if (colors.Count != PictureKeys || colors.Any(c => c is < 0 or > 0xFFFFFF)) throw new ArgumentException($"A picture is {PictureKeys} colors #RRGGBB.");
        var data = colors.SelectMany(c => new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c }).ToArray();
        return Enumerable.Range(0, 7).Select(p =>
        {
            var n = Math.Min(56, data.Length - p * 56);
            return Request(new byte[] { SetPicture, (byte)pictureSlot, 0xFF, (byte)p, (byte)n, (byte)(p == 6 ? 1 : 0) }, data.AsSpan(p * 56, n));
        }).ToList();
    }

    /// <summary>A keymap page, not the request handed back.</summary>
    public static bool IsKeymapPage(ReadOnlySpan<byte> r, int profile, int page, int subLayer) =>
        r.Length == PayloadLength + 1 && !(r[1] == GetKeymap && r[2] == profile && r[3] == 0xFF && r[4] == page && r[5] == subLayer);

    /// <summary>Live key travel on or off (a RAM flag, not stored): input reports id 5 on collection FFFF:0001.</summary>
    public static byte[] EncodeTravelStream(bool on) => Request(new byte[] { TravelStream, (byte)(on ? 1 : 0) });
    public const ushort TravelUsage = 0x0001;
    public const byte TravelReportId = 5;

    /// <summary>A travel report [5, 1B, travel u16 LE, slot]: the key's slot and its travel in the board's unit (0 when released).</summary>
    public static (int Slot, int Counts)? ParseTravel(ReadOnlySpan<byte> r) =>
        r.Length >= 5 && r[0] == TravelReportId && r[1] == TravelStream ? (r[4], BinaryPrimitives.ReadUInt16LittleEndian(r[2..4])) : null;

    /// <summary>Millimetres per travel unit for a firmware version: &lt; 3.00 0.1 mm, 3.00..4.xx 0.01 mm, 5.00 and up 0.005 mm.</summary>
    public static double TravelUnitMm(int version) => version >= 0x500 ? 0.005 : version >= 0x300 ? 0.01 : 0.1;
}
