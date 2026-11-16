using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Devices;

/// <summary>
/// Interrupt configuration of a PCI device, in Enum\&lt;id&gt;\Device Parameters\Interrupt Management
/// (the same values as MSI Utility). Null = value absent (driver default).
/// </summary>
public sealed class InterruptConfig
{
    public string InstanceId { get; set; } = "";
    /// <summary>MSISupported: 0 = line-based interrupt, 1 = MSI/MSI-X.</summary>
    public uint? Msi { get; set; }
    /// <summary>MessageNumberLimit: maximum MSI messages.</summary>
    public uint? Limit { get; set; }
    /// <summary>DevicePolicy (IRQ_DEVICE_POLICY): 0–5.</summary>
    public uint? Policy { get; set; }
    /// <summary>DevicePriority (IRQ_PRIORITY): 0 undefined, 1 low, 2 normal, 3 high.</summary>
    public uint? Priority { get; set; }
    /// <summary>AssignmentSetOverride (KAFFINITY), used with policy 4 (specified processors).</summary>
    public ulong? Mask { get; set; }

    public bool SameValues(InterruptConfig o) => Msi == o.Msi && Limit == o.Limit && Policy == o.Policy && Priority == o.Priority && Mask == o.Mask;
}

public static class Interrupts
{
    public const uint PolicySpecified = 4;
    const string Msi = "MessageSignaledInterruptProperties", Affinity = "Affinity Policy";
    static string Backups => Path.Combine(AppPaths.DataRoot, "interrupt-backups.json");

    static string Base(string id) => $@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters\Interrupt Management";
    static RegistrySetting Dw(string id, string sub, string name) => new(Hive.LocalMachine, $@"{Base(id)}\{sub}", name, RegistryValueKind.DWord);
    static RegistrySetting MaskSetting(string id) => new(Hive.LocalMachine, $@"{Base(id)}\{Affinity}", "AssignmentSetOverride", RegistryValueKind.Binary);

    public static IEnumerable<PnpDevice> PciDevices() => PnpDevices.Enumerate("PCI", presentOnly: true);

    public static InterruptConfig Read(string id) => new()
    {
        InstanceId = id,
        Msi = Num(Dw(id, Msi, "MSISupported").Read()),
        Limit = Num(Dw(id, Msi, "MessageNumberLimit").Read()),
        Policy = Num(Dw(id, Affinity, "DevicePolicy").Read()),
        Priority = Num(Dw(id, Affinity, "DevicePriority").Read()),
        Mask = MaskSetting(id).Read() is { } hex ? MaskFromHex(hex) : null,
    };

    static uint? Num(string? s) => uint.TryParse(s, out var v) ? v : null;

    /// <summary>Little-endian KAFFINITY; accepts the sizes other tools write (1 to 8 bytes).</summary>
    internal static ulong? MaskFromHex(string hex)
    {
        byte[] b;
        try { b = Convert.FromHexString(hex); } catch (FormatException) { return null; }
        if (b.Length is 0 or > 8) return null;
        ulong v = 0;
        for (var i = b.Length - 1; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    internal static string MaskToHex(ulong mask) => Convert.ToHexString(BitConverter.GetBytes(mask));

    /// <summary>
    /// Saves the original state (only on the first change), writes (elevated if needed) and re-reads to confirm.
    /// </summary>
    public static async Task ApplyAsync(InterruptConfig target, CancellationToken ct = default)
    {
        Validate(target);
        var before = Read(target.InstanceId);
        var all = JsonFile.Load<Dictionary<string, InterruptConfig>>(Backups);
        if (all.TryAdd(target.InstanceId, before)) JsonFile.Save(Backups, all);

        if (Elevation.IsAdmin) await Task.Run(() => Write(target), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Interrupts = target }, ct).ConfigureAwait(false);

        if (!Read(target.InstanceId).SameValues(target)) throw new InvalidOperationException(Txt.ChangeNotConfirmed);
    }

    public static InterruptConfig? Original(string id) => JsonFile.Load<Dictionary<string, InterruptConfig>>(Backups).GetValueOrDefault(id);

    internal static void Write(InterruptConfig c)
    {
        var id = c.InstanceId;
        Dw(id, Msi, "MSISupported").Write(c.Msi?.ToString());
        Dw(id, Msi, "MessageNumberLimit").Write(c.Limit?.ToString());
        Dw(id, Affinity, "DevicePolicy").Write(c.Policy?.ToString());
        Dw(id, Affinity, "DevicePriority").Write(c.Priority?.ToString());
        MaskSetting(id).Write(c.Mask is { } m ? MaskToHex(m) : null);
        Log.Info($"Interrupts {id}: MSISupported={c.Msi?.ToString() ?? "-"}, MessageNumberLimit={c.Limit?.ToString() ?? "-"}, " +
                 $"DevicePolicy={c.Policy?.ToString() ?? "-"}, DevicePriority={c.Priority?.ToString() ?? "-"}, AssignmentSetOverride={c.Mask?.ToString("X") ?? "-"}");
    }

    /// <summary>Valid ranges; in the elevated process it also requires a connected PCI device with that id.</summary>
    internal static void Validate(InterruptConfig c, bool checkDevice = false)
    {
        if (!c.InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || c.InstanceId.Contains("..") || c.InstanceId.Length > 200)
            throw new InvalidOperationException($"Invalid device: {c.InstanceId}");
        if (c.Msi is > 1) throw new InvalidOperationException("MSISupported must be 0 or 1.");
        if (c.Limit is 0 or > 2048) throw new InvalidOperationException("MessageNumberLimit must be 1–2048.");
        if (c.Policy is > 5) throw new InvalidOperationException("DevicePolicy must be 0–5.");
        if (c.Priority is > 3) throw new InvalidOperationException("DevicePriority must be 0–3.");
        if (c.Mask is { } m && (m == 0 || (m & ~Cpus.AllMask) != 0)) throw new InvalidOperationException(Txt.CpuMaskOutOfRange);
        if (c.Policy == PolicySpecified && c.Mask is null) throw new InvalidOperationException(Txt.IrqNeedsCpus);
        if (checkDevice && !PciDevices().Any(d => d.InstanceId.Equals(c.InstanceId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"No connected PCI device has the id {c.InstanceId}.");
    }
}
