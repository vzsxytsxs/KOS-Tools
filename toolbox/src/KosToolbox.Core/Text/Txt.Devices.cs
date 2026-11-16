namespace KosToolbox.Core.Text;

/// <summary>Texts for the Devices page.</summary>
public static partial class Txt
{
    public const string On = "On", Off = "Off", NotSet = "Not set", NotSetShort = "—", Remove = "Remove", Removing = "Removing…";

    // ---------- processors ----------
    public const string CpuListInvalid = "Enter processors as numbers and ranges, for example 0-3,6.";
    public const string CpuMaskOutOfRange = "The processor mask includes processors this PC doesn't have.";
    public static string CpuOutOfRange(int count) => $"This PC has {count} logical processors: use numbers from 0 to {count - 1}.";
    public static string CpuInfo(int count) => $"This PC has {count} logical processors (0–{count - 1}).";

    // ---------- Devices ----------
    public const string PageDevices = "Devices", PageDevicesSubtitle = "Interrupt settings (MSI, priority, affinity) and devices that aren't connected anymore.";
    public const string TabInterrupts = "Interrupts", TabGhostDevices = "Ghost devices";
    public const string IrqNote = "Settings for connected PCI devices, read from Enum\\…\\Device Parameters\\Interrupt Management (the values MSI utilities use). They take effect after restarting the device or Windows. A device that doesn't support MSI can stop working with it on; if Windows doesn't start, undo it in Safe Mode.";
    public const string MsiOnlyFilter = "Only devices that support MSI";
    public const string ColDevice = "Device", ColClass = "Class", ColSupport = "Supports", ColMsi = "MSI", ColLimit = "Limit", ColPriority = "Priority",
        ColPolicy = "Affinity policy", ColLastSeen = "Last connected", ColInstanceId = "Instance ID";
    public const string LabelMsi = "Interrupt mode", LabelLimit = "Message limit", LabelPriority = "Device priority", LabelPolicy = "Affinity policy",
        LabelCpus = "Processors", LimitHint = "Empty = no limit (1–2048)", CpusHint = "Used with \"Specified processors\", for example 2,4-5";
    public const string IrqLineBased = "Line-based (MSI off)", IrqMsi = "MSI / MSI-X";
    public const string RestoreOriginal = "Restore original", RestoreOriginalTooltip = "Values saved before the toolbox first changed this device";
    public const string IrqLimitInvalid = "The message limit must be a whole number from 1 to 2048, or empty.";
    public const string IrqNeedsCpus = "\"Specified processors\" needs a list of processors.";
    public const string IrqNoChanges = "Nothing changed: these are the current values.";
    public const string IrqApplied = "Saved and verified. Restart the device or Windows for it to take effect.";
    public const string IrqMsiUnsupported = "Windows doesn't report MSI support for this device. With MSI on it can stop working; if Windows doesn't start, undo it in Safe Mode.";
    public static string IrqPendingName(string device) => $"Interrupt settings: {device}";
    public static string IrqSupport(uint? bits) => bits is not { } b ? Unknown
        : string.Join(", ", new[] { (1u, "Line"), (2u, "MSI"), (4u, "MSI-X") }.Where(x => (b & x.Item1) != 0).Select(x => x.Item2)) is { Length: > 0 } s ? s : Unknown;
    public static string IrqDetail(string id, string support, uint? max) =>
        $"{id}\nSupports: {support}" + (max is { } m ? $" · up to {m} messages" : "");
    public static string DevicesReadFailed(string message) => $"The devices couldn't be read: {message}";

    public const string GhostNote = "Devices installed before but not connected now (Device Manager › Show hidden devices). Removing one deletes its entry and settings, like \"Uninstall device\"; drivers stay installed and Windows adds the device again when it's connected. Connected devices are never removed. Check all skips devices with an interrupt affinity set (marked), so they keep it if they come back.";
    public const string RemoveChecked = "Remove checked", GhostNoneChecked = "Check the devices to remove first.", GhostAffinity = "interrupt affinity set";
    public static string GhostRemoved(int removed, int total) => removed == total ? $"{removed} devices removed." : $"{removed} of {total} devices removed. Details are in the log.";
    public static string GhostCount(int shown, int total) => shown == total ? $"{total} devices" : $"{shown} of {total} devices";
}
