using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Batteries;

/// <summary>
/// Battery decoders of the read-only headset and mouse queries. Every reply here is an input report as ReadFile returns it (report id at
/// [0]), so offsets are Wireless Battery's (src/drivers/steelseries.cpp, corsair.cpp, hyperx.cpp at bbb5c75f4aac), which follow
/// HeadsetControl, rivalcfg and HyperXWM.
/// </summary>
public static class VendorBatteryCodec
{
    /// <summary>A raw value mapped from <paramref name="lo"/>..<paramref name="hi"/> onto 0–100 %.</summary>
    public static int Map(int raw, int lo, int hi) => Math.Clamp((raw - lo) * 100 / (hi - lo), 0, 100);

    /// <summary>SteelSeries Aerox / Prime: [1] = (percent / 5 + 1) | charging &lt;&lt; 7; 0 = mouse off or out of range.</summary>
    public static PeripheralBattery SteelSeriesAerox(ReadOnlySpan<byte> r)
    {
        var raw = r[1] & 0x7F;
        return raw == 0 ? new PeripheralBattery(null, null, Asleep: true) : new PeripheralBattery(Math.Min((raw - 1) * 5, 100), (r[1] & 0x80) != 0);
    }

    /// <summary>SteelSeries Rival 3 Wireless: [0] percent, [2] charging.</summary>
    public static PeripheralBattery SteelSeriesRival(ReadOnlySpan<byte> r) => new(Math.Min((int)r[0], 100), r[2] != 0);

    /// <summary>Arctis 7 / Pro 2019: [2] percent; 0 = headset off.</summary>
    public static PeripheralBattery Arctis7(ReadOnlySpan<byte> r) =>
        r[2] == 0 ? new PeripheralBattery(null, null, Asleep: true) : new PeripheralBattery(Math.Min((int)r[2], 100), null);

    /// <summary>Arctis Nova Pro Wireless: [6] level 0–8, [15] 1 off / 2 charging.</summary>
    public static PeripheralBattery NovaProWireless(ReadOnlySpan<byte> r) =>
        r[15] == 1 ? new PeripheralBattery(null, null, Asleep: true) : new PeripheralBattery(Map(r[6], 0, 8), r[15] == 2);

    /// <summary>Corsair Void family: [2] &amp; 0x7F percent (bit 7 = mic up), [4] 0 off, 1–2 on battery, 4–5 charging.</summary>
    public static PeripheralBattery CorsairVoid(ReadOnlySpan<byte> r) => r[4] switch
    {
        0 => new PeripheralBattery(null, null, Asleep: true),
        4 or 5 => new PeripheralBattery(Math.Min(r[2] & 0x7F, 100), true),
        _ => new PeripheralBattery(Math.Min(r[2] & 0x7F, 100), false),
    };

    /// <summary>Corsair Bragi property get: [2] = 02, [3] status (0 OK), value [4..7] little-endian. Battery property 0F is 0–1000.</summary>
    public static uint? BragiValue(ReadOnlySpan<byte> r) =>
        r.Length >= 8 && r[2] == 0x02 && r[3] == 0 ? (uint)(r[4] | r[5] << 8 | r[6] << 16 | r[7] << 24) : null;

    public static int BragiPercent(uint level) => (int)Math.Min((level + 5) / 10, 100);

    /// <summary>HyperX Cloud III Wireless, command 89: [2..3] millivolts, [4] percent.</summary>
    public static int? HyperXPercent(ReadOnlySpan<byte> r) => r[4] <= 100 ? r[4] : null;
}

/// <summary>A query: report id and payload to write, a reply filter and its decoder. One entry per product family.</summary>
public sealed record VendorBatteryModel(ushort VendorId, ushort[] ProductIds, string Name, PeripheralKind Kind,
    Func<HidCollection, bool> Accept, Func<IHidTransport, PeripheralBattery?> Read);

/// <summary>
/// Read-only battery queries for headsets and mice of SteelSeries, Corsair and HyperX. Each writes one request (a query, never a
/// setting) to the collection Wireless Battery's tables name, by exact VID and PID.
/// </summary>
public sealed class VendorBatteryDriver : PeripheralDriver
{
    readonly VendorBatteryModel _model;
    readonly HidCollection _collection;

    VendorBatteryDriver(HidDevice device, VendorBatteryModel model, HidCollection collection) : base(device, "Battery query", model.Name, tested: false)
    {
        _model = model;
        _collection = collection;
    }

    public override bool BatteryOnly => true;
    public override PeripheralKind Kind => _model.Kind;
    public override PeripheralState Read() => new() { Name = _model.Name, Battery = ReadBattery() };
    public override PeripheralBattery? ReadBattery() => With(_collection, true, _model.Read);

    public static VendorBatteryDriver? Claim(HidDevice d) =>
        Models.FirstOrDefault(m => m.VendorId == d.VendorId && m.ProductIds.Contains(d.ProductId)) is { } m
        && d.Collections.FirstOrDefault(m.Accept) is { } c ? new VendorBatteryDriver(d, m, c) : null;

    /// <summary>Writes the query, then reads until <paramref name="accept"/> takes a reply or the time runs out.</summary>
    static byte[]? Query(IHidTransport t, byte reportId, byte[] payload, int timeoutMs, Func<byte[], bool>? accept = null)
    {
        t.FlushQueue();
        t.Write(reportId, payload);
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
            if (t.Read((int)Math.Max(1, end - Environment.TickCount64)) is { } r && (accept?.Invoke(r) ?? true)) return r;
        return null;
    }

    static bool Interface(HidCollection c, int n) => c.Interface == n && c.OutputLength > 0;

    static PeripheralBattery? Bragi(IHidTransport t)
    {
        var link = new Corsair.BragiLink(t); // report 02 where the collection is numbered (the Virtuoso's receiver), id 0 where it isn't
        uint? Get(byte target, byte property) => link.GetProperty(target, property) is { Ok: true } r ? r.Value : null;
        foreach (byte target in new byte[] { 0x09, 0x0A, 0x0B, 0x08 }) // paired devices behind a receiver first, then the device itself
            if (Get(target, 0x0F) is { } level and <= 1000)
                return new PeripheralBattery(VendorBatteryCodec.BragiPercent(level), Get(target, 0x10) is { } charge ? charge is 1 or 3 : null);
        return null;
    }

    public static readonly IReadOnlyList<VendorBatteryModel> Models = new VendorBatteryModel[]
    {
        new(0x1038, new ushort[] { 0x1838, 0x183A, 0x1852, 0x1854, 0x1858, 0x185A, 0x1840, 0x1842 }, "SteelSeries Aerox / Prime Wireless", PeripheralKind.Mouse,
            c => Interface(c, 3), t => Query(t, 0, new byte[] { (byte)(new ushort[] { 0x183A, 0x1854, 0x185A, 0x1842 }.Contains(t.Collection.ProductId) ? 0x92 : 0xD2) }, 300)
                is { Length: >= 2 } r ? VendorBatteryCodec.SteelSeriesAerox(r) : null),
        new(0x1038, new ushort[] { 0x1830, 0x1872 }, "SteelSeries Rival 3 Wireless", PeripheralKind.Mouse,
            c => Interface(c, 3), t => Query(t, 0, new byte[] { 0xAA, 0x01 }, 300) is { Length: >= 3 } r ? VendorBatteryCodec.SteelSeriesRival(r) : null),
        new(0x1038, new ushort[] { 0x1260, 0x12AD, 0x1252 }, "SteelSeries Arctis 7", PeripheralKind.Headset,
            c => c.UsagePage == 0xFF43 && c.Usage == 0x0202, t => Query(t, 0x06, new byte[] { 0x18 }, 1000) is { Length: >= 3 } r ? VendorBatteryCodec.Arctis7(r) : null),
        new(0x1038, new ushort[] { 0x12E0 }, "SteelSeries Arctis Nova Pro Wireless", PeripheralKind.Headset,
            c => Interface(c, 4), t => Query(t, 0x06, new byte[] { 0xB0 }, 1000) is { Length: >= 16 } r ? VendorBatteryCodec.NovaProWireless(r) : null),
        new(0x1B1C, new ushort[] { 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 },
            "Corsair wireless headset", PeripheralKind.Headset,
            c => (c.UsagePage == 0xFFC5 || c.UsagePage == 0xFF00) && c.OutputLength > 0,
            t => Query(t, 0xC9, new byte[] { 0x64 }, 1000) is { Length: >= 5 } r ? VendorBatteryCodec.CorsairVoid(r) : null),
        // 0A41–0A44: the Virtuoso RGB Wireless (0A42 + 0A41 read on the friend's PC: battery 0F = 710, numbered report 02, collection FF42/0001).
        new(0x1B1C, new ushort[] { 0x2A08, 0x2A02, 0x0A97, 0x0A64, 0x0A62, 0x0A3E, 0x0A3D, 0x0A41, 0x0A42, 0x0A43, 0x0A44 }, "Corsair wireless device", PeripheralKind.Other,
            c => c.UsagePage >= 0xFF00 && c.OutputLength is 64 or 65 && c.InputLength is 64 or 65, Bragi),
        new(0x03F0, new ushort[] { 0x05B7, 0x0C9D }, "HyperX Cloud III Wireless", PeripheralKind.Headset,
            c => c.OutputLength == 62 && c.InputLength == 62,
            t =>
            {
                bool Is(byte[] r, byte cmd) => r.Length >= 5 && r[0] == 0x66 && r[1] == cmd;
                if (Query(t, 0x66, new byte[] { 0x82 }, 800, r => Is(r, 0x82)) is { } on && on[2] == 0) return new PeripheralBattery(null, null, Asleep: true);
                var charging = Query(t, 0x66, new byte[] { 0x8A }, 800, r => Is(r, 0x8A)) is { } ch ? ch[2] == 1 : (bool?)null;
                return Query(t, 0x66, new byte[] { 0x89 }, 800, r => Is(r, 0x89)) is { } b && VendorBatteryCodec.HyperXPercent(b) is { } p
                    ? new PeripheralBattery(p, charging) : null;
            }),
    };
}
