using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Keyboards;

/// <summary>
/// VIA (QMK raw HID): collection FF60:0061, 32-byte output and input reports with report id 0. Commands from the VIA protocol as
/// summarised by keyboard-protocol (src/via/index.ts at 3f9158fe83d7): 0x01 get protocol version → [1..2] big-endian; 0x11 get layer
/// count → [1]. Only these two getters are sent; the keymap is not written.
/// </summary>
public static class ViaCodec
{
    public const ushort UsagePage = 0xFF60, Usage = 0x0061;
    public const byte ProtocolVersion = 0x01, LayerCount = 0x11;
    public const int PayloadLength = 32;

    public static byte[] Request(byte command)
    {
        var p = new byte[PayloadLength];
        p[0] = command;
        return p;
    }

    /// <summary>A reply (report id at [0]) echoing <paramref name="command"/> in its first byte; 0xFF there means unhandled.</summary>
    public static bool Answers(ReadOnlySpan<byte> r, byte command) => r.Length >= 4 && r[1] == command;
    public static int Version(ReadOnlySpan<byte> r) => r[2] << 8 | r[3];
    public static int Layers(ReadOnlySpan<byte> r) => r[2];
}

/// <summary>
/// Any QMK/VIA board, by its raw HID collection (VIA has no VID/PID list: that's its point). Read-only, labelled "Untested model". The
/// shared ODM VIDs are refused (their boards reuse vendor pages for other protocols; 3151:502D belongs to RoyuanDriver, which
/// writes only after Identify names a catalog board).
/// </summary>
public sealed class ViaDriver : PeripheralDriver
{
    static readonly ushort[] OdmVendors = { 0x3554, 0x373E, 0x258A, 0x25A7, 0x3151 };
    readonly HidCollection _raw;

    ViaDriver(HidDevice d, HidCollection raw) : base(d, "VIA (QMK)", null, tested: false) => _raw = raw;

    public static ViaDriver? Claim(HidDevice d) =>
        !OdmVendors.Contains(d.VendorId) && d.Collections.FirstOrDefault(c => c.UsagePage == ViaCodec.UsagePage && c.Usage == ViaCodec.Usage
            && c.OutputLength == ViaCodec.PayloadLength + 1 && c.InputLength == ViaCodec.PayloadLength + 1) is { } raw ? new ViaDriver(d, raw) : null;

    public override PeripheralKind Kind => PeripheralKind.Keyboard;
    public override PeripheralConnection? Connection => Device.Bluetooth ? PeripheralConnection.Bluetooth : PeripheralConnection.Wired;

    public override PeripheralState Read() => With(_raw, true, t =>
    {
        var version = Ask(t, ViaCodec.ProtocolVersion);
        var layers = version is null ? null : Ask(t, ViaCodec.LayerCount);
        return new PeripheralState
        {
            Name = Device.Product,
            Protocol = version is { } v ? $"VIA {ViaCodec.Version(v)}" : null,
            Layers = layers is { } l ? ViaCodec.Layers(l) : null,
        };
    });

    public override PeripheralBattery? ReadBattery() => null;

    static byte[]? Ask(IHidTransport t, byte command)
    {
        t.FlushQueue();
        t.Write(0, ViaCodec.Request(command));
        var end = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < end)
            if (t.Read(100) is { } r && ViaCodec.Answers(r, command)) return r;
        return null;
    }
}

/// <summary>Wooting keyboards (31E3, config page FF55 or 1337): recognised and labelled; their protobuf profiles are not decoded yet.</summary>
public sealed class WootingDriver : PeripheralDriver
{
    WootingDriver(HidDevice d) : base(d, "Wooting", null, tested: false) { }

    // Product ids from keyboard-protocol src/wooting/index.ts (WOOTING_PRODUCTS; variants match by pid & 0xFFF0).
    static readonly ushort[] Families = { 0x1100, 0x1200, 0x1210, 0x1220, 0x1230, 0x1300, 0x1310, 0x1320, 0x1340, 0x1400, 0x1410, 0x1500, 0x1510 };

    public static WootingDriver? Claim(HidDevice d) =>
        d.VendorId == 0x31E3 && Families.Contains((ushort)(d.ProductId & 0xFFF0))
        && d.Collections.Any(c => c.UsagePage is 0xFF55 or 0x1337) ? new WootingDriver(d) : null;

    public override PeripheralKind Kind => PeripheralKind.Keyboard;
    public override PeripheralConnection? Connection => PeripheralConnection.Wired;
    public override PeripheralState Read() => new() { Name = Device.Product };
    public override PeripheralBattery? ReadBattery() => null;
}
