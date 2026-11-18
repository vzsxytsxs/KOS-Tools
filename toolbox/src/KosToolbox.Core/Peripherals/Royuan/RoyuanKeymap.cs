namespace KosToolbox.Core.Peripherals.Royuan;

/// <summary>
/// One keymap slot as the board stores it: [type, a, b, c]. Type 0 = HID keyboard usage at b; 1 = mouse button; 3 = consumer usage
/// (b low, c high); 9 = macro (a mode, b index); 0x0A = special (a = 1: Fn). All zero: no key (on the base layer, a dead key).
/// Facts from sharkfin's docs/PROTOCOL.md ("Slot values"); checked on an R82HE (A at slot 9, Space at 41, Fn at 59).
/// </summary>
public readonly record struct RoyuanKey(byte Type, byte A, byte B, byte C)
{
    public bool IsEmpty => Type == 0 && A == 0 && B == 0 && C == 0;

    /// <summary>What the key does, as a short label ("A", "F5", "Shift", "Vol+", "Fn", "M3").</summary>
    public string Label => Type switch
    {
        _ when IsEmpty => "",
        0 => Usage(B),
        3 => (B | C << 8) switch { 0xE2 => "Mute", 0xE9 => "Vol+", 0xEA => "Vol−", 0xCD => "Play", 0xB5 => "Next", 0xB6 => "Prev", var u => $"C{u:X3}" },
        9 => $"M{B + 1}",
        0x0A when A == 1 => "Fn",
        _ => $"{Type:X2}{A:X2}",
    };

    /// <summary>HID keyboard usage (page 7) names.</summary>
    public static string Usage(byte u) => u switch
    {
        >= 0x04 and <= 0x1D => ((char)('A' + u - 0x04)).ToString(),
        >= 0x1E and <= 0x26 => ((char)('1' + u - 0x1E)).ToString(),
        0x27 => "0",
        >= 0x3A and <= 0x45 => $"F{u - 0x39}",
        >= 0x68 and <= 0x73 => $"F{u - 0x68 + 13}",
        0x28 => "Enter", 0x29 => "Esc", 0x2A => "Backspace", 0x2B => "Tab", 0x2C => "Space", 0x2D => "-", 0x2E => "=", 0x2F => "[", 0x30 => "]",
        0x31 => "\\", 0x32 => "#", 0x33 => ";", 0x34 => "'", 0x35 => "`", 0x36 => ",", 0x37 => ".", 0x38 => "/", 0x39 => "Caps",
        0x46 => "PrtSc", 0x47 => "ScrLk", 0x48 => "Pause", 0x49 => "Ins", 0x4A => "Home", 0x4B => "PgUp", 0x4C => "Del", 0x4D => "End",
        0x4E => "PgDn", 0x4F => "→", 0x50 => "←", 0x51 => "↓", 0x52 => "↑", 0x53 => "NumLk", 0x65 => "Menu",
        0xE0 or 0xE4 => "Ctrl", 0xE1 or 0xE5 => "Shift", 0xE2 or 0xE6 => "Alt", 0xE3 or 0xE7 => "Win",
        _ => $"0x{u:X2}",
    };
}

/// <summary>Where a key sits: its slot, its row (0 = function row) and its left edge and width in key units (1 = one standard key).</summary>
public sealed record RoyuanKeyPosition(int Slot, int Row, double X, double Width);

/// <summary>
/// A key's mode: its kind (0 plain, 2 dynamic keystroke, 3 mod-tap, 4 toggle, 5 toggle repeating, 7 snap), the snap partner's slot,
/// the mod-tap hold time in ms, the dynamic-keystroke first point in mm, the four DKS action bytes (sub-layers 0..3; per event A..D a
/// 2-bit cell: 0 nothing, 1 tap, 2 press until the next event, 3 hold) and the key's entries in sub-layers 0..3 (0 is the key itself;
/// mod-tap: 0 hold, 1 tap; toggle latches sub-layer 0).
/// </summary>
public sealed record RoyuanKeyMode(byte Kind, int? Partner, int HoldMs, double DksPointMm, IReadOnlyList<byte> Actions, IReadOnlyList<RoyuanKey> Layers);
