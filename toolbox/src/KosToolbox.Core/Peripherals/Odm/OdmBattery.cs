using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Odm;

/// <summary>A product of one of the shared ODM families: exact VID and PID, and the delay between a request and its reply.</summary>
public sealed record OdmModel(ushort VendorId, ushort ProductId, string Name, bool Receiver, int ReplyDelayMs = 100);

/// <summary>
/// The feature-report-0, 64-byte family (Glorious, Hitscan, G-Wolves XVI). Payload offsets (the Win32 buffer adds the id in front):
/// request [2] target (2 = mouse), [3] length, [4] page, [5] command; reply [0] status (A0 waking, A1 OK, A2 unsupported, A3 busy,
/// A4 asleep), [5] echoed command. Battery: command 0x83, reply [6] charging, [7] percent. Wire facts from Wireless Battery
/// (src/drivers/glorious.cpp, +1 for its id byte) and mouse-protocol src/compx/.
/// </summary>
public static class Feature64Codec
{
    public const int PayloadLength = 64;
    public const byte Mouse = 2, Battery = 0x83, Waking = 0xA0, Ok = 0xA1, Asleep = 0xA4;

    public static byte[] Request(byte command, byte length = 2, byte page = 0, byte target = Mouse)
    {
        var p = new byte[PayloadLength];
        p[2] = target;
        p[3] = length;
        p[4] = page;
        p[5] = command;
        return p;
    }

    /// <summary>A reply (report id at [0]) to <paramref name="command"/>: its status byte, or null when it isn't this protocol's.</summary>
    public static byte? Status(ReadOnlySpan<byte> report, byte command) =>
        report.Length > 8 && report[6] == command && (report[1] & 0xF0) == 0xA0 ? report[1] : null;

    public static PeripheralBattery DecodeBattery(ReadOnlySpan<byte> report) => report[1] == Ok && report[8] <= 100
        ? new PeripheralBattery(Math.Max((int)report[8], 1), report[7] == 1)
        : new PeripheralBattery(null, null, Asleep: true);
}

/// <summary>
/// The "report 8" family (Kysona, Pulsar, ATK/VXE, Redragon). Output report 08, 16-byte payload: [0] command, [1] status in a reply,
/// [15] = 0x55 − (0x08 + Σ payload[0..14]). Battery: command 04 → [5] percent, [6] charging, [7..8] millivolts. Read without the
/// command-03 bracket (that one is a write). Wire facts from Wireless Battery (src/drivers/kysona.cpp) and Linux hid-kysona.c.
/// </summary>
public static class Report8Codec
{
    public const byte ReportId = 0x08, Battery = 0x04;
    public const int PayloadLength = 16;

    public static byte Checksum(ReadOnlySpan<byte> payload)
    {
        var sum = ReportId;
        for (var i = 0; i < 15; i++) sum += payload[i];
        return (byte)(0x55 - sum);
    }

    public static byte[] Request(byte command)
    {
        var p = new byte[PayloadLength];
        p[0] = command;
        p[15] = Checksum(p);
        return p;
    }

    /// <summary>A reply to <paramref name="command"/> (report id at [0]): checksum right and the command echoed.</summary>
    public static bool Answers(ReadOnlySpan<byte> report, byte command) =>
        report.Length >= PayloadLength + 1 && report[0] is ReportId or 0x09 && report[1] == command && report[16] == Checksum(report.Slice(1, PayloadLength));

    public static PeripheralBattery DecodeBattery(ReadOnlySpan<byte> report) =>
        new(report[6] <= 100 ? report[6] : null, report[7] != 0);
}

/// <summary>Battery-only drivers for the two ODM families (their settings are not implemented yet).</summary>
public sealed class OdmBatteryDriver : PeripheralDriver
{
    readonly OdmModel _model;
    readonly HidCollection _collection;
    readonly bool _feature64;

    OdmBatteryDriver(HidDevice device, OdmModel model, HidCollection collection, bool feature64)
        : base(device, feature64 ? "Feature-report 64-byte family" : "Report-8 family", model.Name, tested: false)
    {
        _model = model;
        _collection = collection;
        _feature64 = feature64;
    }

    // Our entries. Sources: Wireless Battery glorious.cpp (Glorious, Hitscan; G-Wolves names), kysona.cpp (Redragon, verified there on
    // an M916 Pro 1K), mouse-protocol src/pulsar/index.ts (XS-1 ids; the 8K dongle 5406 verified in Wireless Battery).
    public static readonly IReadOnlyList<OdmModel> Feature64Models = new OdmModel[]
    {
        new(0x258A, 0x2011, "Glorious Model O Wireless", false, 50), new(0x258A, 0x2022, "Glorious Model O Wireless", true, 50),
        new(0x258A, 0x2012, "Glorious Model D Wireless", false, 50), new(0x258A, 0x2023, "Glorious Model D Wireless", true, 50),
        new(0x258A, 0x2013, "Glorious Model O- Wireless", false, 50), new(0x258A, 0x2024, "Glorious Model O- Wireless", true, 50),
        new(0x258A, 0x2014, "Glorious Model D- Wireless", false, 50), new(0x258A, 0x2025, "Glorious Model D- Wireless", true, 50),
        new(0x3770, 0x0136, "Hitscan Hyperlight X", false, 200), new(0x3770, 0x0194, "Hitscan Hyperlight N", false, 200),
        new(0x33E4, 0x3854, "G-Wolves wireless mouse", true, 100),
    };

    public static readonly IReadOnlyList<OdmModel> Report8Models = new OdmModel[]
    {
        new(0x3554, 0xF55D, "Redragon wireless mouse", true), new(0x3554, 0xF5D5, "Redragon wireless mouse", true),
        new(0x3554, 0xF55E, "Redragon wireless mouse", false),
        new(0x3710, 0x5402, "Pulsar wireless mouse", true), new(0x3710, 0x5403, "Pulsar wireless mouse", true), new(0x3710, 0x5406, "Pulsar 8K dongle", true),
        new(0x3770, 0x0200, "Hitscan Hyperlight", true),
    };

    public static OdmBatteryDriver? Claim(HidDevice device)
    {
        if (Feature64Models.FirstOrDefault(m => m.VendorId == device.VendorId && m.ProductId == device.ProductId) is { } f
            && device.Collections.FirstOrDefault(c => c.FeatureLength == Feature64Codec.PayloadLength + 1) is { } fc)
            return new OdmBatteryDriver(device, f, fc, true);
        if (Report8Models.FirstOrDefault(m => m.VendorId == device.VendorId && m.ProductId == device.ProductId) is { } r
            && device.Collections.FirstOrDefault(c => c.OutputLength == 17 && c.InputLength == 17
                                                      || c.UsagePage >= 0xFF00 && c.OutputLength >= 17 && c.InputLength >= 17) is { } rc)
            return new OdmBatteryDriver(device, r, rc, false);
        return null;
    }

    public override bool BatteryOnly => true;
    public override PeripheralKind Kind => PeripheralKind.Mouse;
    public override PeripheralConnection? Connection => _model.Receiver ? PeripheralConnection.Receiver : PeripheralConnection.Wired;

    public override PeripheralState Read() => new() { Name = _model.Name, Battery = ReadBattery() };

    public override PeripheralBattery? ReadBattery() => _feature64 ? Feature64() : Report8();

    PeripheralBattery? Feature64() => With(_collection, false, t =>
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            t.SetFeature(0, Feature64Codec.Request(Feature64Codec.Battery));
            Delay(_model.ReplyDelayMs);
            var reply = t.GetFeature(0);
            var status = Feature64Codec.Status(reply, Feature64Codec.Battery);
            if (status is null) return null;
            if (status == Feature64Codec.Waking && attempt == 0) { Delay(400); continue; }
            return Feature64Codec.DecodeBattery(reply);
        }
        return new PeripheralBattery(null, null, Asleep: true);
    });

    PeripheralBattery? Report8() => With(_collection, true, t =>
    {
        t.FlushQueue();
        t.Write(Report8Codec.ReportId, Report8Codec.Request(Report8Codec.Battery));
        var end = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < end)
            if (t.Read(100) is { } r && Report8Codec.Answers(r, Report8Codec.Battery)) return Report8Codec.DecodeBattery(r);
        return null;
    });
}
