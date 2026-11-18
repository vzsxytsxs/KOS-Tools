namespace KosToolbox.Core.Peripherals.Royuan;

/// <summary>
/// A ROYUAN gen2 board. <paramref name="DeviceId"/> is what Identify (0x8F) answers: the PID is shared by dozens of boards, some of
/// another command family, so nothing but Identify is sent until it matches.
/// </summary>
public sealed record RoyuanModel(ushort VendorId, ushort ProductId, uint DeviceId, string Name, int Profiles, int[] PollingRates, bool Tested,
    IReadOnlyList<RoyuanKeyPosition>? Layout = null, string? DefaultKeymap = null, bool MacroWrites = false)
{
    /// <summary>The factory keymap entry of a slot (sub-layer 0), when the catalog has it.</summary>
    public RoyuanKey? Default(int slot)
    {
        if (DefaultKeymap is null || slot is < 0 or >= RoyuanCodec.Slots) return null;
        var b = Convert.FromHexString(DefaultKeymap.AsSpan(slot * 8, 8));
        return new RoyuanKey(b[0], b[1], b[2], b[3]);
    }
}

/// <summary>Hand-written from the facts in sharkfin's board registry (GPL-3.0; facts only, nothing copied) and checked on the board.</summary>
public static class RoyuanCatalog
{
    public static readonly RoyuanModel[] Models =
    {
        // Wired only, 75 % (80 keys), 4 profiles, 8 kHz; lighting layout "v" (static, breathing, spectrum and more, brightness 0..4).
        // Tested on firmware 5.05 (2026-11-17): polling, brightness, static/breathing/spectrum and profile written, read back, restored.
        new(0x3151, 0x502D, 2844, "Attack Shark R82HE", 4, new[] { 8000, 4000, 2000, 1000, 500, 250, 125 }, Tested: true, R82Layout(), R82Keymap),
    };

    /// <summary>The R82HE's default base keymap (sub-layer 0, 128 slots x 4 bytes) as its profile 1 held it, read 2026-11-17.</summary>
    const string R82Keymap =
        "000029000000350000002B00000039000000E1000000E00000003A0000001E000000140000000400000000000000E30000003B0000001F0000001A0000001600" +
        "00001D000000E20000003C0000002000000008000000070000001B000000000000003D00000021000000150000000900000006000000000000003E0000002200" +
        "0000170000000A00000019000000000000003F000000230000001C0000000B000000050000002C0000004000000024000000180000000D000000110000000000" +
        "000041000000250000000C0000000E00000010000000000000004200000026000000120000000F00000036000A01000000004300000027000000130000003300" +
        "000037000000E4000000440000002D0000002F000000340000003800000000000000450000002E0000003000000000000000E500000050000000000000002A00" +
        "0000310000002800000052000000510000004C0000004A0000004B0000004E000000000000004F00000000000300E2000300E9000300EA000D0201000D020200" +
        "0D0301000D0302000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The R82HE's 80 keys. The matrix is column-major (slot = column × 6 + row, row 0 the function row); which key each slot is comes
    /// from this board's own default keymap (sub-layer 0 of profile 1, read 2026-11-17) and the travel stream (A = 9, D = 21,
    /// Space = 41 pressed and seen). Widths are the usual 75 % ones.
    /// </summary>
    static RoyuanKeyPosition[] R82Layout()
    {
        var keys = new List<RoyuanKeyPosition>();
        void Row(int row, params (int Slot, double Width)[] keysInRow)
        {
            var x = 0.0;
            foreach (var (slot, width) in keysInRow)
            {
                if (slot >= 0) keys.Add(new RoyuanKeyPosition(slot, row, x, width));
                x += width;
            }
        }
        const int Gap = -1;
        // Esc F1..F12 Del
        Row(0, (0, 1), (6, 1), (12, 1), (18, 1), (24, 1), (30, 1), (36, 1), (42, 1), (48, 1), (54, 1), (60, 1), (66, 1), (72, 1), (Gap, 2), (84, 1));
        // ` 1..0 - = Backspace Home
        Row(1, (1, 1), (7, 1), (13, 1), (19, 1), (25, 1), (31, 1), (37, 1), (43, 1), (49, 1), (55, 1), (61, 1), (67, 1), (73, 1), (79, 2), (85, 1));
        // Tab Q..P [ ] \ PgUp
        Row(2, (2, 1.5), (8, 1), (14, 1), (20, 1), (26, 1), (32, 1), (38, 1), (44, 1), (50, 1), (56, 1), (62, 1), (68, 1), (74, 1), (80, 1.5), (86, 1));
        // Caps A..L ; ' Enter PgDn
        Row(3, (3, 1.75), (9, 1), (15, 1), (21, 1), (27, 1), (33, 1), (39, 1), (45, 1), (51, 1), (57, 1), (63, 1), (69, 1), (81, 2.25), (87, 1));
        // Shift Z../ Shift ↑
        Row(4, (4, 2.25), (16, 1), (22, 1), (28, 1), (34, 1), (40, 1), (46, 1), (52, 1), (58, 1), (64, 1), (70, 1), (76, 1.75), (82, 1));
        // Ctrl Win Alt Space Fn Ctrl ← ↓ →
        Row(5, (5, 1.25), (11, 1.25), (17, 1.25), (41, 6.25), (59, 1), (65, 1), (Gap, 1), (77, 1), (83, 1), (89, 1));
        return keys.ToArray();
    }

    public static RoyuanModel? Find(ushort vid, ushort pid, uint deviceId) =>
        Models.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid && m.DeviceId == deviceId);
}
