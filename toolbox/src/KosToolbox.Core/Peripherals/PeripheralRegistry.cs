using System.Diagnostics;
using KosToolbox.Core.Peripherals.AttackShark;
using KosToolbox.Core.Peripherals.Batteries;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Peripherals.Keyboards;
using KosToolbox.Core.Peripherals.Logitech;
using KosToolbox.Core.Peripherals.Odm;
using KosToolbox.Core.Peripherals.Razer;
using KosToolbox.Core.Peripherals.Royuan;

namespace KosToolbox.Core.Peripherals;

/// <summary>
/// Which driver talks to a device. Vendor drivers first, in order, each claiming only an exact VID + PID from its catalog and the
/// collection it needs; then the battery-only readers, on devices nobody claimed (Wireless Battery's order). A device nobody claims gets
/// no driver: it is only enumerated and dumped, never written to.
/// </summary>
public static class PeripheralRegistry
{
    static readonly Func<HidDevice, PeripheralDriver?>[] Vendors =
    {
        d => RazerCatalog.Find(d.VendorId, d.ProductId) is { } m && RazerDriver.ControlCollection(d) is not null ? new RazerDriver(d, m) : null,
        d => LogitechCatalog.Find(d.VendorId, d.ProductId) is { } m && LogitechCatalog.Collections(d) is var (s, l) && (s ?? l) is not null ? new HidppDriver(d, m) : null,
        OdmBatteryDriver.Claim,
        WootingDriver.Claim,
        RoyuanDriver.Claim,
        AttackSharkDriver.Claim,
        ViaDriver.Claim,
    };

    static readonly Func<HidDevice, PeripheralDriver?>[] BatteryReaders =
    {
        VendorBatteryDriver.Claim,
        PlayStationDriver.Claim,
        d => d.Bluetooth && BluetoothBatteryDriver.Container(d) is not null ? new BluetoothBatteryDriver(d, BluetoothBattery.ByContainer) : null,
    };

    public static PeripheralDriver? Claim(HidDevice device)
    {
        foreach (var claim in Vendors.Concat(BatteryReaders))
            if (claim(device) is { } driver) return driver;
        return null;
    }

    /// <summary>Vendor apps that write the same settings, by brand (process names as each installer ships them; checked on no install here).</summary>
    static readonly (string Family, string App, string[] Processes)[] Apps =
    {
        ("Razer", "Razer Synapse", new[] { "RazerAppEngine", "Razer Synapse 3", "Razer Synapse Service" }),
        ("Logitech HID++", "Logitech G HUB", new[] { "lghub", "lghub_agent" }),
        // AttackShark.exe from the X11 installer (ATTACK SHARK X11 SOFT, read statically; not run): it takes the same one-shot read grant.
        ("Attack Shark", "Attack Shark", new[] { "AttackShark" }),
    };

    /// <summary>The vendor app running now that may overwrite this family's settings, or null.</summary>
    public static string? RunningVendorApp(string family)
    {
        foreach (var (f, app, names) in Apps)
        {
            if (f != family) continue;
            foreach (var name in names)
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                if (found.Length > 0) return app;
            }
        }
        return null;
    }
}

/// <summary>A Bluetooth HID device nobody claimed, with the battery Windows reports for its container. Read-only, nothing sent.</summary>
public sealed class BluetoothBatteryDriver : PeripheralDriver
{
    readonly Func<Dictionary<Guid, int>> _source;

    public BluetoothBatteryDriver(HidDevice d, Func<Dictionary<Guid, int>> source) : base(d, "Bluetooth battery (Windows)", null, tested: true) => _source = source;

    public static Guid? Container(HidDevice d) => d.Collections.Select(c => c.ContainerId).FirstOrDefault(g => g is not null && g != Guid.Empty);

    public override bool BatteryOnly => true;
    public override PeripheralConnection? Connection => PeripheralConnection.Bluetooth;
    public override PeripheralState Read() => new() { Name = Device.Product, Battery = ReadBattery() };
    public override PeripheralBattery? ReadBattery() =>
        Container(Device) is { } id && _source().TryGetValue(id, out var percent) ? new PeripheralBattery(percent, null) : null;
}
