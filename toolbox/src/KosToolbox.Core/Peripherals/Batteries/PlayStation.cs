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

    /// <summary>
    /// DualShock 4 USB output report 05, after its id (Linux hid-sony): flags (0x02 light bar, 0x04 its blink, here 0/0 = steady; 0x01
    /// rumble not set, so the motors at [3] and [4] are left alone), R, G, B at [5..7], blink on and off times at [8] and [9].
    /// </summary>
    public static byte[] Ds4LightBar(int rgb)
    {
        var p = new byte[31];
        p[0] = 0x06;
        p[5] = (byte)(rgb >> 16);
        p[6] = (byte)(rgb >> 8);
        p[7] = (byte)rgb;
        return p;
    }

    /// <summary>The USB rate nearest to the reports counted (a 0.5 s count is a few percent off).</summary>
    public static int Rate(int reports, double seconds) =>
        new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }.MinBy(r => Math.Abs(Math.Log(reports / seconds / r)));
}

/// <summary>
/// Battery on every model; on a DualShock 4 over USB also the light bar (Off or a color) and the polling rate it reports at, counted
/// from its input reports. The rate isn't a setting: the controller asks USB for it, and changing it takes a filter driver.
/// </summary>
public sealed class PlayStationDriver : PeripheralDriver
{
    // The light bar can't be read back: the last color sent by this process, per device (none = never sent).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Sent = new();
    readonly HidCollection _gamepad;

    PlayStationDriver(HidDevice d, HidCollection gamepad)
        : base(d, "PlayStation", PlayStationCodec.IsDualSense(d.ProductId) ? "DualSense" : "DualShock 4",
            tested: d.ProductId == 0x09CC && !d.Bluetooth) => _gamepad = gamepad; // DualShock 4 v2 on its cable: battery, rate and light bar checked

    public static PlayStationDriver? Claim(HidDevice d) =>
        d.VendorId == 0x054C && (PlayStationCodec.IsDualShock4(d.ProductId) || PlayStationCodec.IsDualSense(d.ProductId))
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0x01 && c.Usage == 0x05) is { } g ? new PlayStationDriver(d, g) : null;

    /// <summary>A DualShock 4 on its cable: the light bar is written, the rate counted.</summary>
    public bool HasLightBar => PlayStationCodec.IsDualShock4(Device.ProductId) && !Device.Bluetooth;
    public override bool BatteryOnly => !HasLightBar;
    public override PeripheralKind Kind => PeripheralKind.Controller;
    public override PeripheralConnection? Connection => Device.Bluetooth ? PeripheralConnection.Bluetooth : PeripheralConnection.Wired;
    public override IReadOnlySet<PeripheralSetting> Writable => HasLightBar ? new HashSet<PeripheralSetting> { PeripheralSetting.Lighting } : base.Writable;
    public override IReadOnlyList<LightingEffect> Effects => new[] { LightingEffect.Off, LightingEffect.Static };

    public override PeripheralState Read()
    {
        if (!HasLightBar) return new() { Name = Model, Battery = ReadBattery() };
        var (battery, hz) = With(_gamepad, true, t =>
        {
            t.FlushQueue();
            PeripheralBattery? b = null;
            var reports = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 500)
                if (t.Read(100) is { } r && r[0] == 0x01) { reports++; b ??= PlayStationCodec.Decode(Device.ProductId, r); }
            return (b, reports > 10 ? PlayStationCodec.Rate(reports, clock.Elapsed.TotalSeconds) : (int?)null);
        });
        return new()
        {
            Name = Model, Battery = battery,
            Polling = hz is { } h ? new PeripheralPolling(h, new[] { h }) : null,
            Lighting = Sent.TryGetValue(Device.Key, out var c)
                ? new PeripheralLighting(c == 0 ? LightingEffect.Off : LightingEffect.Static, c == 0 ? null : c)
                : new PeripheralLighting(LightingEffect.Static, null),
        };
    }

    public override PeripheralState SetLighting(LightingEffect effect, int color)
    {
        if (!HasLightBar) throw Unsupported();
        Require(effect is LightingEffect.Off or LightingEffect.Static, "The light bar takes one color, or off.");
        var rgb = effect == LightingEffect.Off ? 0 : color & 0xFFFFFF;
        With(_gamepad, true, t => { t.Write(0x05, PlayStationCodec.Ds4LightBar(rgb)); return 0; });
        Sent[Device.Key] = rgb;
        return Read();
    }

    public override PeripheralBattery? ReadBattery() => With(_gamepad, true, t =>
    {
        var end = Environment.TickCount64 + 500;
        while (Environment.TickCount64 < end)
            if (t.Read(100) is { } r && PlayStationCodec.Decode(Device.ProductId, r) is { } b) return b;
        return null;
    });
}
