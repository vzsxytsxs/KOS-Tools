using System.Text;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Text;

/// <summary>Texts for the Peripherals page (mice, keyboards and other HID devices).</summary>
public static partial class Txt
{
    public const string PagePeripherals = "Peripherals",
        PagePeripheralsSubtitle = "Mice, keyboards and other HID devices, over plain HID without vendor software.";
    public const string PeripheralsNotSupportedHeader = "NOT SUPPORTED", PeripheralNotSupported = "Not supported",
        PeripheralNotSupportedNote = "The toolbox has no driver for this device yet, so it shows only what Windows reports about it; nothing is sent to the device. Copy device report puts its HID collections on the clipboard for a support request.",
        PeripheralsNone = "No HID device found.", PeripheralInfo = "Info", PeripheralCollections = "HID collections",
        CopyDeviceReport = "Copy device report", DeviceReportCopied = "Device report copied to the clipboard.", ClipboardBusy = "The clipboard is busy; try again.";
    public const string HidInterfaceHeader = "Interface", HidUsageHeader = "Usage",
        HidInputHeader = "Input", HidOutputHeader = "Output", HidFeatureHeader = "Feature", HidLengthsNote = "Report lengths in bytes, report id included.";
    public const string PeripheralVidPid = "VID:PID", PeripheralProduct = "Product string", PeripheralConnection = "Connection", PeripheralRevision = "Revision",
        PeripheralKindLabel = "Kind (guessed)", Bluetooth = "Bluetooth";

    public const string PeripheralsSupportedHeader = "DEVICES", PeripheralUntested = "Untested model",
        PeripheralUntestedNote = "This model hasn't been checked on hardware yet. Every change is read back from the device, but check it behaves as expected.",
        PeripheralBatteryOnlyNote = "The toolbox reads this device's battery only; it has no settings here.",
        PeripheralPerformance = "Performance", PeripheralPower = "Power", PeripheralLighting = "Lighting",
        PeripheralDpi = "DPI", PeripheralDpiStages = "DPI stages", PeripheralPolling = "Polling rate", PeripheralBatteryLabel = "Battery",
        PeripheralCharging = "Charging", PeripheralSleep = "Sleep after (minutes)", PeripheralLowBattery = "Low-battery warning (%)",
        PeripheralBrightness = "Brightness (%)", PeripheralFirmware = "Firmware", PeripheralSerial = "Serial number", PeripheralFamily = "Protocol",
        PeripheralLayers = "Layers", PeripheralReading = "Reading the device…", PeripheralAsleep = "Asleep",
        PeripheralAsleepNote = "The receiver answered but the device didn't: it may be asleep, off or out of range. Move it and press Refresh.",
        PeripheralOnboardNote = "The mouse runs its onboard profile, so its live DPI and report rate come from that profile. Host mode lets the toolbox set them until the mouse is unplugged or another program switches it back; the profile itself isn't changed.",
        PeripheralUseHostMode = "Use host mode", PeripheralChargingSuffix = ", charging", PeripheralWireless = "Wireless receiver", PeripheralWired = "Cable";

    public static string PeripheralBatteryText(Peripherals.PeripheralBattery? b) => b switch
    {
        null => "",
        { Asleep: true } => PeripheralAsleep,
        { Percent: { } p } => $"{p} %",
        { Level: { } l } => l,
        _ => "",
    };

    public static string PeripheralConnectionName(Peripherals.PeripheralConnection c) => c switch
    {
        Peripherals.PeripheralConnection.Receiver => PeripheralWireless, Peripherals.PeripheralConnection.Bluetooth => Bluetooth, _ => PeripheralWired,
    };

    public const string PeripheralGamemode = "Gamemode", PeripheralUseInGamemode = "Use these in gamemode", PeripheralGamemodeClear = "Remove",
        PeripheralGamemodeNote = "KOS Governor sets the live DPI and polling rate saved here when gamemode turns on, and puts back what the mouse had when it turns off. Nothing is stored in the mouse.",
        PeripheralGamemodeSaved = "Saved for gamemode: KOS Governor applies them when gamemode turns on.", PeripheralGamemodeRemoved = "Removed from gamemode.";

    public const string PeripheralEffect = "Effect", PeripheralColor = "Color (#RRGGBB)", PeripheralColorInvalid = "enter the color as #RRGGBB.",
        PeripheralDockBattery = "Mouse battery on the ring", PeripheralDockBatteryOn = "On", PeripheralDockBatteryOff = "Off",
        PeripheralTurnOn = "Turn on", PeripheralTurnOff = "Turn off",
        PeripheralDockBatteryNote = "KOS Governor colors the ring by the Razer mouse's battery: red when low, then yellow, green when full, breathing while charging. It checks every minute and stores nothing in the dock; Turn off puts back the effect saved in the dock.",
        PeripheralDockBatterySaved = "The ring shows the mouse battery: KOS Governor sets it in a few seconds.",
        PeripheralDockBatteryRemoved = "The ring no longer shows the mouse battery.";

    public static string LightingEffectName(LightingEffect e) => e switch
    {
        LightingEffect.Off => "Off", LightingEffect.Static => "Static", LightingEffect.Breathing => "Breathing", LightingEffect.Spectrum => "Spectrum", _ => "Other",
    };

    public static string PeripheralGamemodeValues(int? dpi, int? hz) =>
        string.Join(" · ", new[] { dpi is { } d ? $"{d} DPI" : null, hz is { } h ? $"{h} Hz" : null }.OfType<string>()) is { Length: > 0 } s ? s : "—";

    public static string PeripheralVendorApp(string app) => $"{app} is running and may overwrite these settings.";
    public static string PeripheralReadFailed(string message) => $"The device couldn't be read: {message}";
    public static string PeripheralWriteFailed(string message) => $"Not changed: {message}";
    public static string PeripheralApplied(string what) => $"{what} changed and read back from the device.";
    public static string PeripheralPollingApplied(int hz) => $"Polling rate set to {hz} Hz and read back. Check it in Latency › Mouse.";
    public static string PeripheralUntestedApplied(string what) => $"{what} changed on an untested model: check the device behaves as expected.";
    public static string PeripheralStage(int n, DpiStage s) => s.X == s.Y ? $"{n}: {s.X}" : $"{n}: {s.X}×{s.Y}";
    public static string PeripheralDpiRange(PeripheralDpi d) => $"{d.Min}–{d.Max}";

    public static string PeripheralsReadFailed(string message) => $"The HID devices couldn't be read: {message}";
    public static string PeripheralUnnamed(ushort vendorId, ushort productId) => $"HID device {HidIds(vendorId, productId)}";
    public static string HidIds(ushort vendorId, ushort productId) => $"{vendorId:X4}:{productId:X4}";
    public static string HidInterface(int number) => number < 0 ? "—" : $"MI_{number:X2}";
    public static string HidCollectionNumber(int number) => number < 0 ? "—" : $"COL{number:X2}";
    /// <summary>Interface and collection together (MI_01 COL02, MI_00, COL01), "—" when the path has neither.</summary>
    public static string HidLocation(int iface, int collection) =>
        string.Join(" ", new[] { iface < 0 ? null : HidInterface(iface), collection < 0 ? null : HidCollectionNumber(collection) }.OfType<string>()) is { Length: > 0 } s ? s : "—";
    /// <summary>bcdDevice as major.minor (0x0200 → 2.00).</summary>
    public static string HidRevision(ushort bcd) => $"{bcd >> 8:X}.{bcd & 0xFF:X2}";

    public static string PeripheralKindName(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Mouse => "Mouse", PeripheralKind.Keyboard => "Keyboard", PeripheralKind.Headset => "Headset",
        PeripheralKind.Controller => "Controller", _ => "Other HID device",
    };

    public static string HidUsage(ushort page, ushort usage) => $"{page:X4}:{usage:X4}" + (HidUsageName(page, usage) is { } name ? $" · {name}" : "");

    /// <summary>The name of the common top-level usages; null for the others.</summary>
    public static string? HidUsageName(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x01) => "Pointer", (0x01, 0x02) => "Mouse", (0x01, 0x04) => "Joystick", (0x01, 0x05) => "Gamepad",
        (0x01, 0x06) => "Keyboard", (0x01, 0x07) => "Keypad", (0x01, 0x80) => "System control",
        (0x0B, 0x05) => "Headset", (0x0C, 0x01) => "Consumer control", (0x0D, _) => "Digitizer", (0x84 or 0x85, _) => "Power",
        ( >= 0xFF00, _) => "Vendor-defined",
        _ => null,
    };

    /// <summary>
    /// A device as text for a support request: ids, product string and every collection. Never the device path or instance id, which
    /// can carry a serial number or a Bluetooth address.
    /// </summary>
    public static string PeripheralReport(HidDevice device)
    {
        var s = new StringBuilder();
        s.AppendLine("KOS Toolbox device report");
        s.AppendLine($"{PeripheralProduct}: {device.Product ?? "(none)"}");
        s.AppendLine($"{PeripheralVidPid}: {HidIds(device.VendorId, device.ProductId)}");
        s.AppendLine($"{PeripheralRevision}: {HidRevision(device.Version)}");
        s.AppendLine($"{Bluetooth}: {(device.Bluetooth ? "yes" : "no")}");
        s.AppendLine($"{PeripheralKindLabel}: {PeripheralKindName(device.Kind)}");
        s.AppendLine($"{PeripheralCollections} ({device.Collections.Count}):");
        foreach (var c in device.Collections)
            s.AppendLine($"  {HidInterface(c.Interface),-6} {HidCollectionNumber(c.Collection),-6} {HidUsage(c.UsagePage, c.Usage),-28}"
                + $"  input {c.InputLength,3}  output {c.OutputLength,3}  feature {c.FeatureLength,3}");
        s.AppendLine(HidLengthsNote);
        return s.ToString();
    }
}
