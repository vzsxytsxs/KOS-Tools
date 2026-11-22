using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using static KosToolbox.Core.Tweaks.Tweak;

namespace KosToolbox.Core.Catalog;

/// <summary>
/// Services › Options: Windows services and drivers whose start type is a choice, each with its service name, a friendly name and
/// what it does. The Windows default comes from the Windows 11 26H2 image (offline SYSTEM hive). Only constants and methods
/// here: static initializers across partial files have no guaranteed order.
/// </summary>
public static partial class KosCatalog
{
    const string Automatic = "auto", Manual = "manual", SystemStart = "system", Disabled = "disabled";

    /// <summary>A service's start type as a choice among <paramref name="starts"/> (Start values 1 system, 2 automatic, 3 manual, 4 disabled).</summary>
    static Tweak ServiceChoice(string service, string friendly, string description, string group, string kosDefault, string windowsDefault, params string[] starts)
    {
        static string Label(string k) => k switch { Automatic => Txt.StartAutomatic, Manual => Txt.StartManual, SystemStart => Txt.StartSystem, _ => Txt.Disabled };
        static string Value(string k) => k switch { SystemStart => "1", Automatic => "2", Manual => "3", _ => "4" };
        var t = Choice("svc-" + service.ToLowerInvariant(), $"{friendly} ({service})", description, Pages.Services, null, group,
            starts.Select(k => (k, Label(k) + (k == windowsDefault ? Txt.StartWindowsDefault : ""))).ToArray());
        var spec = new (string, Target)[starts.Length];
        for (var i = 0; i < starts.Length; i++) spec[i] = (starts[i], Target.Set(Value(starts[i])));
        t.Add(RegistrySetting.ServiceStart(service), spec);
        t.RequiresReboot = true;
        t.KosDefault = kosDefault;
        t.WindowsDefault = windowsDefault;
        t.Source = "Service " + service + " (Start)";
        return t;
    }

    internal static IEnumerable<Tweak> ServiceTweaks() => new[]
    {
        ServiceChoice("VaultSvc", "Credential Manager",
            "Stores saved sign-ins in the Windows vault (Credential Manager): web and Windows credentials that apps and game launchers keep between sessions. Disabled, apps that keep their sign-in there ask for it again. Starts only when an app uses it.",
            Txt.ServiceGroupAccounts, Manual, Manual, Manual, Disabled),
        ServiceChoice("PEAUTH", "Protected Environment Authentication",
            "Kernel driver for protected media (PlayReady DRM): streaming video in apps and browsers, e.g. Netflix in high resolution. Disabled, protected video refuses to play or drops to low resolution.",
            Txt.ServiceGroupMedia, Automatic, Automatic, Automatic, Manual, Disabled),
        ServiceChoice("DsmSvc", "Device Setup Manager",
            "Finishes the setup of new devices: driver software, device apps and their information from Windows Update. Disabled, a new device can stay with its setup incomplete. Starts only when a device is connected.",
            Txt.ServiceGroupDevices, Manual, Manual, Automatic, Manual, Disabled),
        ServiceChoice("acpitime", "ACPI wake alarm",
            "Driver of the ACPI time and alarm device, which wakes a PC from sleep at a set time (wake timers, mostly on laptops and tablets). Not needed on a desktop.",
            Txt.ServiceGroupPower, Disabled, Manual, Manual, Disabled),
        ServiceChoice("acpipagr", "ACPI processor aggregator",
            "Driver that lets the firmware take processors out of use to cap power or heat (ACPI processor aggregator). Laptops and servers with that firmware feature use it.",
            Txt.ServiceGroupPower, Disabled, Manual, Manual, Disabled),
        ServiceChoice("acpipmi", "ACPI power meter",
            "Driver of the ACPI power meter, which reports the PC's power draw to Windows (and power caps on servers). Rarely present on desktops.",
            Txt.ServiceGroupPower, Disabled, Manual, Manual, Disabled),
        ServiceChoice("dam", "Desktop Activity Moderator",
            "Driver that pauses desktop apps while a laptop with Modern Standby sleeps, to save battery. Disabled, apps keep running in that sleep and the battery drains faster; desktops don't use it.",
            Txt.ServiceGroupPower, Disabled, SystemStart, SystemStart, Disabled),
    };
}
