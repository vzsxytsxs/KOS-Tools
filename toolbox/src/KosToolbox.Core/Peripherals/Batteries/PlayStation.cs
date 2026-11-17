using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Batteries;

/// <summary>
/// DualShock 4 and DualSense: the battery is part of the gamepad collection's regular input report, so nothing is sent. Offsets (report
/// id at [0]) from Linux hid-playstation.c as summarised by Wireless Battery (src/drivers/playstation.cpp): DualShock 4 USB report 01
/// byte 30, Bluetooth 11 byte 32 (low nibble level, bit 4 cable); DualSense USB 01 byte 53, Bluetooth 31 byte 54 (low nibble 0–10, high
/// nibble 1 charging, 2 full). On Bluetooth the controller sends reduced reports until a feature report is read; that switch is a state
/// change, so this reader never asks for it and simply finds no full report.
/// </summary>
public static class PlayStationCodec
{
    public static bool IsDualShock4(ushort pid) => pid is 0x05C4 or 0x09CC or 0x0BA0;
    public static bool IsDualSense(ushort pid) => pid is 0x0CE6 or 0x0DF2;

    public static PeripheralBattery? Decode(ushort pid, ReadOnlySpan<byte> r)
    {
        if (IsDualShock4(pid))
        {
            var at = r[0] == 0x01 ? 30 : r[0] == 0x11 ? 32 : 0;
            if (at == 0 || r.Length <= at) return null;
            int level = r[at] & 0x0F;
            var cable = (r[at] & 0x10) != 0;
            return cable ? new PeripheralBattery(Math.Min(level * 10, 100), level < 11) : new PeripheralBattery(Math.Min(level * 10 + 5, 100), false);
        }
        if (IsDualSense(pid))
        {
            var at = r[0] == 0x01 ? 53 : r[0] == 0x31 ? 54 : 0;
            if (at == 0 || r.Length <= at) return null;
            int level = r[at] & 0x0F, state = r[at] >> 4;
            return state == 2 ? new PeripheralBattery(100, true) : new PeripheralBattery(Math.Min(level * 10 + 5, 100), state == 1);
        }
        return null;
    }
}

public sealed class PlayStationDriver : PeripheralDriver
{
    readonly HidCollection _gamepad;

    PlayStationDriver(HidDevice d, HidCollection gamepad)
        : base(d, "Battery (input report)", PlayStationCodec.IsDualSense(d.ProductId) ? "DualSense" : "DualShock 4", tested: false) => _gamepad = gamepad;

    public static PlayStationDriver? Claim(HidDevice d) =>
        d.VendorId == 0x054C && (PlayStationCodec.IsDualShock4(d.ProductId) || PlayStationCodec.IsDualSense(d.ProductId))
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0x01 && c.Usage == 0x05) is { } g ? new PlayStationDriver(d, g) : null;

    public override bool BatteryOnly => true;
    public override PeripheralKind Kind => PeripheralKind.Controller;
    public override PeripheralState Read() => new() { Name = Model, Battery = ReadBattery() };

    public override PeripheralBattery? ReadBattery() => With(_gamepad, true, t =>
    {
        var end = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < end)
            if (t.Read(100) is { } r && PlayStationCodec.Decode(Device.ProductId, r) is { } b) return b;
        return null;
    });
}
