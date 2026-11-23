using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Batteries;

/// <summary>
/// DualShock 4 and DualSense: the battery is part of the gamepad collection's regular input report, so nothing is sent. Offsets (report
/// id at [0]) from Linux hid-playstation.c as summarised by Wireless Battery (src/drivers/playstation.cpp): DualShock 4 USB report 01
/// byte 30, Bluetooth 11 byte 32 (low nibble level, bit 4 cable); DualSense USB 01 byte 53, Bluetooth 31 byte 54 (low nibble 0–10, high
/// nibble 1 charging, 2 full). On Bluetooth the controller sends the reduced report 01 (sticks and buttons, no battery: its byte 30 is
/// 0, which read as 5 %) until the calibration feature report (05, 41 bytes) is read, as Linux's hid-playstation does when it binds; then
/// it sends the full report. Reading that report only switches the report format to what a USB controller always sends.
/// </summary>
public static class PlayStationCodec
{
    public static bool IsDualShock4(ushort pid) => pid is 0x05C4 or 0x09CC or 0x0BA0;
    public static bool IsDualSense(ushort pid) => pid is 0x0CE6 or 0x0DF2;

    public static PeripheralBattery? Decode(ushort pid, ReadOnlySpan<byte> r, bool bluetooth = false)
    {
        if (bluetooth && r[0] == 0x01) return null; // the reduced report: no battery in it
        if (IsDualShock4(pid))
        {
            var at = r[0] == 0x01 ? 30 : r[0] == 0x11 ? 32 : 0;
            if (at == 0 || r.Length <= at) return null;
            int level = r[at] & 0x0F;
            var cable = (r[at] & 0x10) != 0;
            // On the battery the level runs 0 to 8 (full): the 5 % steps of 10n+5 never reached 100 %. On the cable it runs 0 to 11.
            return cable ? new PeripheralBattery(Math.Min(level * 10, 100), level < 11) : new PeripheralBattery(Math.Min(level * 100 / 8, 100), false);
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

    /// <summary>
    /// DualShock 4 Bluetooth output report 11 after its id (Linux hid-playstation): hardware control (HID report, CRC32, poll interval),
    /// audio control, then the same fields as the USB report (flags, rumble, light bar R G B, blink) from [2], zeros up to 73, and at
    /// [73..76] the CRC32 (little endian) of 0xA2, the report id and everything before it.
    /// </summary>
    public static byte[] Ds4LightBarBluetooth(int rgb)
    {
        var p = new byte[77];
        p[0] = 0xC4;
        p[2] = 0x06;
        p[7] = (byte)(rgb >> 16);
        p[8] = (byte)(rgb >> 8);
        p[9] = (byte)rgb;
        var crc = Crc32(0xA2, 0x11, p.AsSpan(0, 73));
        p[73] = (byte)crc;
        p[74] = (byte)(crc >> 8);
        p[75] = (byte)(crc >> 16);
        p[76] = (byte)(crc >> 24);
        return p;
    }

    /// <summary>CRC-32 (the zlib one) of the two seed bytes followed by <paramref name="data"/>.</summary>
    internal static uint Crc32(byte first, byte second, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        void Add(byte b)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Add(first);
        Add(second);
        foreach (var b in data) Add(b);
        return ~crc;
    }

    /// <summary>The USB rate nearest to the reports counted (a 0.5 s count is a few percent off).</summary>
    public static int Rate(int reports, double seconds) =>
        new[] { 125, 250, 500, 1000, 2000, 4000, 8000 }.MinBy(r => Math.Abs(Math.Log(reports / seconds / r)));
}

/// <summary>
/// Battery on every model; on a DualShock 4 also the light bar (Off or a color), over USB or Bluetooth, and over USB the polling rate it
/// reports at, counted from its input reports. The rate isn't a setting: the controller asks USB for it, and changing it takes a filter driver.
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

    /// <summary>A DualShock 4: the light bar is written (output report 05 on its cable, 11 with a CRC over Bluetooth); the rate is counted on the cable only.</summary>
    public bool HasLightBar => PlayStationCodec.IsDualShock4(Device.ProductId);
    public override bool BatteryOnly => !HasLightBar;
    public override PeripheralKind Kind => PeripheralKind.Controller;
    public override PeripheralConnection? Connection => Device.Bluetooth ? PeripheralConnection.Bluetooth : PeripheralConnection.Wired;
    public override IReadOnlySet<PeripheralSetting> Writable => HasLightBar ? new HashSet<PeripheralSetting> { PeripheralSetting.Lighting } : base.Writable;
    public override IReadOnlyList<LightingEffect> Effects => new[] { LightingEffect.Off, LightingEffect.Static };

    public override PeripheralState Read()
    {
        if (!HasLightBar) return new() { Name = Model, Battery = ReadBattery() };
        if (Device.Bluetooth) return new() { Name = Model, Battery = ReadBattery(), Lighting = LightingNow() };
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
            Lighting = LightingNow(),
        };
    }

    PeripheralLighting LightingNow() => Sent.TryGetValue(Device.Key, out var c)
        ? new PeripheralLighting(c == 0 ? LightingEffect.Off : LightingEffect.Static, c == 0 ? null : c)
        : new PeripheralLighting(LightingEffect.Static, null);

    /// <summary>Over Bluetooth the controller only sends its full reports (and takes the full output report) after this feature report is read.</summary>
    static void FullReports(IHidTransport t)
    {
        try { t.GetFeature(0x05, 41); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        { Persistence.Log.Warn($"PlayStation controller: the calibration report couldn't be read over Bluetooth: {ex.Message}"); }
    }

    public override PeripheralState SetLighting(LightingEffect effect, int color)
    {
        if (!HasLightBar) throw Unsupported();
        Require(effect is LightingEffect.Off or LightingEffect.Static, "The light bar takes one color, or off.");
        var rgb = effect == LightingEffect.Off ? 0 : color & 0xFFFFFF;
        With(_gamepad, true, t =>
        {
            if (Device.Bluetooth) { FullReports(t); t.Write(0x11, PlayStationCodec.Ds4LightBarBluetooth(rgb)); }
            else t.Write(0x05, PlayStationCodec.Ds4LightBar(rgb));
            return 0;
        });
        Sent[Device.Key] = rgb;
        return Read();
    }

    public override PeripheralBattery? ReadBattery() => With(_gamepad, true, t =>
    {
        var end = Environment.TickCount64 + (Device.Bluetooth ? 1500 : 500);
        var switched = false;
        while (Environment.TickCount64 < end)
        {
            if (t.Read(100) is not { } r) continue;
            if (PlayStationCodec.Decode(Device.ProductId, r, Device.Bluetooth) is { } b) return b;
            // Only the reduced report comes in: ask once for the full ones.
            if (Device.Bluetooth && r[0] == 0x01 && !switched) { switched = true; FullReports(t); }
        }
        return null;
    });
}
