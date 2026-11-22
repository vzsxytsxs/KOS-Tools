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

    public InterruptConfig Copy() => (InterruptConfig)MemberwiseClone();

    public bool SameValues(InterruptConfig o) => Msi == o.Msi && Limit == o.Limit && Policy == o.Policy && Priority == o.Priority && Mask == o.Mask;
}

/// <summary>Interrupt settings of several devices plus devices to restart afterwards (one elevated request).</summary>
public sealed class InterruptBatch
{
    public List<InterruptConfig> Configs { get; set; } = new();
    /// <summary>PCI USB host controllers and network adapters only (<see cref="DeviceRestart"/>).</summary>
    public List<string> Restart { get; set; } = new();
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

    /// <summary>
    /// Writes several devices' settings and then restarts the listed devices, in one elevated request. Saves each device's
    /// original state first (only on its first change). Returns the ids whose settings didn't read back as written, and
    /// whether any restart failed (details in the log).
    /// </summary>
    public static async Task<(List<string> Unconfirmed, bool RestartFailed)> ApplyBatchAsync(InterruptBatch batch, CancellationToken ct = default)
    {
        foreach (var c in batch.Configs) Validate(c);
        if (batch.Configs.Count > 0)
        {
            var all = JsonFile.Load<Dictionary<string, InterruptConfig>>(Backups);
            var added = false;
            foreach (var c in batch.Configs) added |= all.TryAdd(c.InstanceId, Read(c.InstanceId));
            if (added) JsonFile.Save(Backups, all);
        }

        int code;
        if (Elevation.IsAdmin)
        {
            ValidateBatch(batch);
            code = await Task.Run(() => ExecuteBatch(batch), ct).ConfigureAwait(false);
        }
        else code = await ElevatedRunner.RunAsync(new ElevatedJob { InterruptBatch = batch }, ct).ConfigureAwait(false);

        return (batch.Configs.Where(c => !Read(c.InstanceId).SameValues(c)).Select(c => c.InstanceId).ToList(), code == ElevatedJob.ExitPartial);
    }

    /// <summary>Storage controllers (the boot drive among them): left out of "Enable MSI on all", since Windows may not start if one misbehaves.</summary>
    public static bool IsStorage(string deviceClass) => deviceClass is "SCSIAdapter" or "HDC";

    internal static void ValidateBatch(InterruptBatch batch)
    {
        if (batch.Configs.Count + batch.Restart.Count is 0 or > 64) throw new InvalidOperationException("The batch must list 1 to 64 changes.");
        if (batch.Configs.Select(c => c.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Configs.Count
            || batch.Restart.Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Restart.Count)
            throw new InvalidOperationException("A device is listed twice.");
        foreach (var c in batch.Configs) Validate(c, checkDevice: true);
        var pci = PciDevices().ToList();
        // What the device itself reports, checked here too (the unelevated filter isn't trusted). A value the registry already
        // holds passes, so a layout batch that copies a device's existing settings isn't refused.
        foreach (var c in batch.Configs)
        {
            var d = pci.First(d => d.InstanceId.Equals(c.InstanceId, StringComparison.OrdinalIgnoreCase));
            var now = Read(c.InstanceId);
            if (c.Msi == 1 && now.Msi != 1 && (d.InterruptSupport & 6) is not > 0)
                throw new InvalidOperationException($"{d.Name} doesn't report MSI support.");
            if (c.Limit is { } l && l != now.Limit && d.MessageMaximum is { } max && l > max)
                throw new InvalidOperationException($"{d.Name} supports at most {max} messages.");
        }
        foreach (var id in batch.Restart) DeviceRestart.Validate(id, pci);
    }

    /// <summary>Writes, then restarts. Returns an ElevatedJob exit code (partial when a restart failed). Requires administrator.</summary>
    internal static int ExecuteBatch(InterruptBatch batch)
    {
        // One failed device doesn't stop the others; only devices whose write went through are restarted.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;
        foreach (var c in batch.Configs)
        {
            try { Write(c); written.Add(c.InstanceId); }
            catch (Exception ex) { failed = true; Log.Warn($"Interrupts {c.InstanceId}: writing failed: {ex.Message}"); }
        }
        var restart = batch.Restart.Where(id => written.Contains(id) || !batch.Configs.Any(c => c.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        failed |= restart.Count < batch.Restart.Count;
        return DeviceRestart.RestartAll(restart) == 0 && !failed ? ElevatedJob.ExitOk : ElevatedJob.ExitPartial;
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
