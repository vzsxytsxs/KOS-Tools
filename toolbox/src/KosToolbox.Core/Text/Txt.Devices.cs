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
    // ---------- interrupts in effect, advice, suggested layout ----------
    public const string ColInEffect = "In effect";
    public static string IrqInEffect(Devices.IrqState? s, bool pending, ulong allMask)
    {
        if (s is not { Count: > 0 }) return NotSetShort;
        var a = s.Affinity & allMask;
        var cpus = a == 0 ? "" : a == allMask ? " · all CPUs" : $" · {(System.Numerics.BitOperations.PopCount(a) == 1 ? "CPU" : "CPUs")} {SystemInfo.Cpus.Format(a)}";
        var mode = s.MessageSignaled ? $"MSI ×{s.Count}" : s.Lines.Count > 0 ? $"Line IRQ {string.Join(",", s.Lines)}" : $"Mixed ×{s.Count}";
        return mode + cpus + (pending ? " · pending restart" : "");
    }

    public const string AdviceTitle = "Advice", AdviceNone = "Nothing to point out in the interrupts in effect.";
    public static string AdviceLineBased(string device) => $"{device}: runs on a line-based interrupt; MSI mode gives it its own.";
    public static string AdviceSharedLine(uint irq, IEnumerable<string> devices) => $"{string.Join(" and ", devices)} share line IRQ {irq}.";
    public static string AdviceOverlap(string a, string b, string cpus) => $"{a} and {b} take interrupts on the same CPU(s) {cpus}: one waits behind the other.";
    public static string AdviceInputOnCore0(string device) => $"{device} has mouse or keyboard input and is pinned to CPU 0's core, where Windows handles most other interrupts and DPCs.";

    public const string LayoutTitle = "Suggested layout", ApplyLayout = "Apply suggested layout", LayoutKept = "already set",
        LayoutNote = "P-cores only, never CPU 0's core (nor the game CCD on CPUs with several). Applying pins each device (Specified processors) and restarts the USB controllers and network adapters: input and network drop for a moment. The graphics card and audio take it after a Windows restart.",
        LayoutShortNote = "USB and network restart at once (a short drop); graphics and audio after a Windows restart.",
        LayoutEmpty ="No suggestion: this CPU has no core to spare besides CPU 0's.", LayoutNothingToDo = "The suggested layout is already set.";
    public static string LayoutRow(string device, ulong mask, bool kept) => $"{device} → CPU {SystemInfo.Cpus.Format(mask)}" + (kept ? $" ({LayoutKept})" : "");

    public const string EnableMsiAll = "Enable MSI on all supported devices",
        EnableMsiAllTooltip = "Sets MSISupported = 1 on every connected PCI device that reports MSI or MSI-X support; others are never touched. Storage controllers (NVMe, SATA) are left out: if the boot drive's controller misbehaves Windows may not start; set them one by one.",
        MsiAllNone = "Every device that supports MSI already has it on.",
        MsiAllWarning = "MSI takes effect after a restart; if a device misbehaves, select it and use Restore original.";
    public static string MsiAllApplied(int done, int total) => done == total ? $"MSI turned on for {done} devices and read back." : $"MSI turned on for {done} of {total} devices; the rest didn't read back as written.";

    public const string RestartDevice = "Restart device", RestartDeviceTooltip = "Restarts a USB controller or network adapter so its interrupt settings take effect now (it drops for a moment).",
        RestartNeedsWindows = "Graphics, audio and storage controllers aren't restarted here: their interrupt settings take effect after a Windows restart.",
        DeviceRestartFailed = "A device restart failed; details are in the log. Its settings take effect after a Windows restart.",
        RestartInEffect ="Restarted: the settings are in effect now.", RestartNotYet = "Restarted, but the settings take effect after a Windows restart.";

    /// <summary>Result of applying several devices: which read back, which are in effect now and which wait for Windows.</summary>
    public static string IrqBatchResult(IReadOnlyCollection<string> now, IReadOnlyCollection<string> later, IReadOnlyCollection<string> failed) =>
        string.Join("\n", new[]
        {
            now.Count > 0 ? $"In effect now: {string.Join(", ", now)}." : null,
            later.Count > 0 ? $"After a Windows restart: {string.Join(", ", later)}." : null,
            failed.Count > 0 ? $"Didn't read back as written: {string.Join(", ", failed)}." : null,
        }.Where(s => s is not null));

    public static string DevicesReadFailed(string message) => $"The devices couldn't be read: {message}";

    public const string GhostNote = "Devices installed before but not connected now (Device Manager › Show hidden devices). Removing one deletes its entry and settings, like \"Uninstall device\"; drivers stay installed and Windows adds the device again when it's connected. Connected devices are never removed. Check all skips devices with an interrupt affinity set (marked), so they keep it if they come back.";
    public const string RemoveChecked = "Remove checked", GhostNoneChecked = "Check the devices to remove first.", GhostAffinity = "interrupt affinity set";
    public static string GhostRemoved(int removed, int total) => removed == total ? $"{removed} devices removed." : $"{removed} of {total} devices removed. Details are in the log.";
    public static string GhostCount(int shown, int total) => shown == total ? $"{total} devices" : $"{shown} of {total} devices";
}
