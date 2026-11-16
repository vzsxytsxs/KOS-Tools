using System.Text.Json;
using KosToolbox.Core.Bios;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Display;
using KosToolbox.Core.Drivers;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Network;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Power;
using KosToolbox.Core.Services;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Storage;
using KosToolbox.Core.Tweaks;
using Microsoft.Win32;

namespace KosToolbox.Core.Execution;

public sealed class JobWrite
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

public sealed class TaskToggle
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class ServiceStartChange
{
    public string Name { get; set; } = "";
    public int Start { get; set; }
    public int? Delayed { get; set; }
}

public sealed class SnapshotJob
{
    public const string Capture = "capture", Restore = "restore", Organize = "organize";
    public string Mode { get; set; } = "";
    public string? Name { get; set; }
    public bool Full { get; set; }
    public bool Force { get; set; }
    public List<string> Services { get; set; } = new();
}

/// <summary>
/// Request to the elevated process. For tweaks, it receives no values: it receives the tweak and the option, and recomputes
/// from the catalog what to write or which script to run. The other types (startup, task, service, power,
/// snapshots) are structurally validated before any write. The request travels on the command line (not in a
/// file another process could swap while UAC is open) and the result comes back through the exit code.
/// </summary>
public sealed class ElevatedJob
{
    public const string StartupApprovedPrefix = @"reg:HKLM\" + StartupApproved.Root + @"\";
    static readonly string[] StartupSubKeys = { "Run", "Run32", "StartupFolder" };

    public const int ExitOk = 0, ExitRefused = 2, ExitFailed = 3, ExitScriptCode = 4, ExitPartial = 5;

    public string? TweakId { get; set; }
    /// <summary>Option of the tweak: runs the option's script or writes the option's machine values.</summary>
    public string? Option { get; set; }
    public string? ActionId { get; set; }
    /// <summary>StartupApproved flags in HKLM.</summary>
    public List<JobWrite> Writes { get; set; } = new();
    public TaskToggle? TaskChange { get; set; }
    public ServiceStartChange? Service { get; set; }
    public List<PowerWrite> Power { get; set; } = new();
    /// <summary>Plan operation (copy, rename, delete, import, restore) or of a setting (remove value, hide).</summary>
    public PowerPlanOp? PowerPlan { get; set; }
    public SnapshotJob? Snapshot { get; set; }
    /// <summary>Interrupt values of a connected PCI device (Devices page).</summary>
    public InterruptConfig? Interrupts { get; set; }
    /// <summary>Disconnected devices to remove; connected ones are always refused.</summary>
    public List<string> RemoveDevices { get; set; } = new();
    /// <summary>NVIDIA GPU tuning and/or the logon task; revalidated against the device's limits.</summary>
    public GpuJob? Gpu { get; set; }
    /// <summary>Ids of machine cleanup categories; the paths come from the JunkCleaner catalog.</summary>
    public List<string> CleanJunk { get; set; } = new();
    /// <summary>Advanced properties of a network card; revalidated against the driver's NdiParams.</summary>
    public NetAdapterJob? NetAdapter { get; set; }
    /// <summary>Delete or export oemNN.inf packages from the driver store (pnputil).</summary>
    public DriverJob? Drivers { get; set; }
    /// <summary>EDID override of a monitor (validated) or restart of the video adapters.</summary>
    public DisplayJob? Display { get; set; }
    /// <summary>Read the BIOS or write its settings (SCEWIN); changes are revalidated against a fresh read.</summary>
    public BiosJob? Bios { get; set; }
    /// <summary>A debloater option; only ids, the paths and values come from the catalog here.</summary>
    public Debloat.DebloatJob? Debloat { get; set; }
    /// <summary>Display Driver Uninstaller run (vendor and option switches from <see cref="KosToolbox.Core.Drivers.Ddu.Options"/>).</summary>
    public DduJob? Ddu { get; set; }
    /// <summary>NVIDIA driver install from a downloaded package (component folders and tweak ids revalidated).</summary>
    public NvidiaInstallJob? NvidiaInstall { get; set; }
    /// <summary>Items of the KOS state check, by id; the values come from the embedded KOS list or the admin-only last good state.</summary>
    public KosState.KosFixJob? KosFix { get; set; }
    /// <summary>sfc /scannow or DISM /RestoreHealth (only the tool's name; the command line is fixed). Progress and result go to files.</summary>
    public KosState.RepairJob? Repair { get; set; }
    /// <summary>A VBIOS file to load into the NVIDIA driver's key (checked again here), or remove the loaded one.</summary>
    public VbiosJob? Vbios { get; set; }
    /// <summary>Only prepares Logs\Toolbox (Log.Initialize runs at the start of the elevated process).</summary>
    public bool PrepareLogs { get; set; }

    string Describe() => TweakId ?? ActionId ?? (Writes.Count > 0 ? "startup entries" : null) ?? (TaskChange is not null ? $"task {TaskChange.Path}" : null)
        ?? (Service is not null ? $"service {Service.Name}" : null) ?? (Power.Count > 0 ? "power plan" : null) ?? (PowerPlan is not null ? $"power plan {PowerPlan.Op}" : null) ?? (PrepareLogs ? "log folder" : null)
        ?? (Interrupts is not null ? $"interrupts {Interrupts.InstanceId}" : null) ?? (RemoveDevices.Count > 0 ? $"remove {RemoveDevices.Count} devices" : null) ?? (Gpu is not null ? "GPU tuning" : null) ?? (CleanJunk.Count > 0 ? $"cleanup {string.Join(",", CleanJunk)}" : null) ?? (NetAdapter is not null ? $"network adapter {NetAdapter.InstanceId}" : null) ?? (Drivers is not null ? "driver store" : null) ?? (Display is not null ? "display" : null)
        ?? (Bios is not null ? $"BIOS {Bios.Mode}" : null) ?? (Debloat is not null ? $"debloat {Debloat.App}/{Debloat.Option}" : null) ?? (Ddu is not null ? $"DDU {Ddu.Vendor}" : null) ?? (NvidiaInstall is not null ? "NVIDIA driver install" : null) ?? (KosFix is not null ? $"KOS state ({KosFix.Ids.Count} items)" : null) ?? (Repair is not null ? $"Windows repair ({Repair.Tool})" : null) ?? (Vbios is not null ? (Vbios.Path is null ? "VBIOS removal" : "VBIOS load") : null) ?? $"service snapshots ({Snapshot?.Mode})";

    /// <summary>Unelevated side: opens UAC with the request and waits. Returns the elevated process's exit code.</summary>
    public async Task<int> RunElevatedAsync(CancellationToken ct = default)
    {
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
        Log.Info($"Requesting elevation for {Describe()}.");
        // sfc and DISM stop themselves after 4 hours (WindowsRepair); everything else fits in the default limit.
        var limit = Repair is not null ? TimeSpan.FromHours(4.5) : (TimeSpan?)null;
        var code = await Elevation.RunSelfElevatedAsync(new[] { "--elevated-job", payload }, ct, limit).ConfigureAwait(false);
        Log.Info($"Elevated process for {Describe()} exited with code {code}.");
        return code switch
        {
            ExitOk or ExitScriptCode or ExitPartial => code,
            ExitRefused => throw new InvalidOperationException(Text.Txt.ElevatedRefused(Log.Directory)),
            _ => throw new InvalidOperationException(Text.Txt.ElevatedFailed(code, Log.Directory)),
        };
    }

    /// <summary>Elevated side: validates and runs. Returns the process's exit code.</summary>
    public static async Task<int> ExecuteFromArgumentAsync(string payload)
    {
        ElevatedJob job;
        try
        {
            if (!Elevation.IsAdmin) throw new InvalidOperationException("The elevated process doesn't have administrator rights.");
            job = JsonSerializer.Deserialize<ElevatedJob>(Convert.FromBase64String(payload))
                ?? throw new InvalidOperationException("Empty request.");
            job.Validate();
        }
        catch (Exception ex)
        {
            Log.Error("[elevated] Request refused", ex);
            return ExitRefused;
        }
        try
        {
            Log.Info($"[elevated] {job.Describe()}: started");
            var code = await job.ExecuteAsync().ConfigureAwait(false);
            Log.Info($"[elevated] {job.Describe()}: finished with code {code}");
            return code;
        }
        catch (Exception ex)
        {
            Log.Error($"[elevated] {job.Describe()}: failed", ex);
            return ExitFailed;
        }
    }

    int Kinds() => new[] { TweakId is not null, ActionId is not null, Writes.Count > 0, TaskChange is not null, Service is not null, Power.Count > 0, PowerPlan is not null, Snapshot is not null, PrepareLogs,
        Interrupts is not null, RemoveDevices.Count > 0, Gpu is not null, CleanJunk.Count > 0, NetAdapter is not null, Drivers is not null, Display is not null,
        Bios is not null, Debloat is not null, Ddu is not null, NvidiaInstall is not null, KosFix is not null, Repair is not null, Vbios is not null }.Count(b => b);

    /// <summary>Refuses mixed requests and anything that is not exactly an allowed value.</summary>
    internal void Validate()
    {
        if (Kinds() != 1) throw new InvalidOperationException("The request must contain exactly one kind of change.");

        if (ActionId is not null)
        {
            var action = Catalog.KosCatalog.Actions.FirstOrDefault(a => a.Id == ActionId)
                ?? throw new InvalidOperationException($"Unknown action: {ActionId}");
            if (action.Script is not { RequiresAdmin: true } && !(action.Run is not null && action.RunElevated))
                throw new InvalidOperationException($"The action {ActionId} doesn't run elevated.");
            if (Option is not null) throw new InvalidOperationException("Mixed request.");
            return;
        }
        if (Writes.Count > 0) { foreach (var w in Writes) StartupTarget(w); return; }
        if (TaskChange is not null) { ScheduledTaskProvider.ValidateStartupTask(TaskChange.Path); return; }
        if (Service is not null) { ServiceStartupProvider.Validate(Service); return; }
        if (Power.Count > 0) { PowerPlans.Validate(Power); return; }
        if (PowerPlan is not null) { PowerPlans.Validate(PowerPlan); return; }
        if (Snapshot is not null) { ValidateSnapshot(Snapshot); return; }
        if (Interrupts is not null) { Devices.Interrupts.Validate(Interrupts, checkDevice: true); return; }
        if (RemoveDevices.Count > 0) { GhostDevices.Validate(RemoveDevices); return; }
        if (Gpu is not null) { GpuTuner.Validate(Gpu); return; }
        if (CleanJunk.Count > 0) { JunkCleaner.Validate(CleanJunk); return; }
        if (NetAdapter is not null) { NetAdapters.Validate(NetAdapter); return; }
        if (Drivers is not null) { DriverStore.Validate(Drivers); return; }
        if (Display is not null) { Monitors.Validate(Display); return; }
        if (Bios is not null) { Scewin.Validate(Bios); return; }
        if (Debloat is not null) { KosToolbox.Core.Debloat.DebloatCatalog.Validate(Debloat); return; }
        if (Ddu is not null) { KosToolbox.Core.Drivers.Ddu.Validate(Ddu); return; }
        if (NvidiaInstall is not null) { NvidiaDriver.Validate(NvidiaInstall); return; }
        if (KosFix is not null) { KosState.KosStateFix.Validate(KosFix); return; }
        if (Repair is not null) { KosState.WindowsRepair.Validate(Repair); return; }
        if (Vbios is not null) { VbiosLoader.Validate(Vbios); return; }
        if (PrepareLogs) { if (Option is not null) throw new InvalidOperationException("Mixed request."); return; }

        var tweak = Catalog.KosCatalog.Find(TweakId!) ?? throw new InvalidOperationException($"Unknown setting: {TweakId}");
        if (Option is null || tweak.Options.All(o => o.Key != Option))
            throw new InvalidOperationException($"Invalid option for {tweak.Id}: {Option}");
    }

    static void ValidateSnapshot(SnapshotJob s)
    {
        switch (s.Mode)
        {
            case SnapshotJob.Capture:
                if (!ServiceSnapshots.IsValidName(s.Name) || s.Name == ServiceSnapshots.Current) throw new InvalidOperationException($"Invalid snapshot name: {s.Name}");
                if (s.Services.Count > 0) throw new InvalidOperationException("Capture takes no service list.");
                break;
            case SnapshotJob.Restore:
                ServiceSnapshots.PrepareRestore(s.Name ?? "", s.Services);
                if (s.Full || s.Force) throw new InvalidOperationException("Invalid restore options.");
                break;
            case SnapshotJob.Organize:
                if (s.Name is not null || s.Services.Count > 0 || s.Full || s.Force) throw new InvalidOperationException("Invalid organize options.");
                break;
            default:
                throw new InvalidOperationException($"Unknown snapshot mode: {s.Mode}");
        }
    }

    /// <summary>Machine writes of an option (the user ones are done by the unelevated process).</summary>
    internal static List<(ISetting Setting, string? Value)> MachineWrites(Tweak tweak, string option) =>
        tweak.Specs
            .Where(s => s.Setting.MachineWide && s.Targets.TryGetValue(option, out var t) && !t.Ignore && s.Setting.Unavailable() is null)
            .Select(s => (s.Setting, s.Targets[option].Value)).ToList();

    /// <summary>Only the enabled/disabled flag (12 bytes, 02 or 03) in the three subkeys Windows uses.</summary>
    static RegistrySetting StartupTarget(JobWrite w)
    {
        if (!w.Key.StartsWith(StartupApprovedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Write not allowed: {w.Key}");
        var rest = w.Key[StartupApprovedPrefix.Length..];
        var sep = rest.IndexOf('\\');
        var sub = sep > 0 ? rest[..sep] : "";
        var name = sep > 0 ? rest[(sep + 1)..] : "";
        if (!StartupSubKeys.Contains(sub, StringComparer.OrdinalIgnoreCase) || name.Length == 0)
            throw new InvalidOperationException($"Write not allowed: {w.Key}");
        byte[] data;
        try { data = Convert.FromHexString(w.Value ?? ""); } catch (FormatException) { data = Array.Empty<byte>(); }
        if (data.Length != 12 || data[0] is not (0x02 or 0x03))
            throw new InvalidOperationException($"Invalid startup value for {name}.");
        return new RegistrySetting(Hive.LocalMachine, $@"{StartupApproved.Root}\{sub}", name, RegistryValueKind.Binary);
    }

    internal async Task<int> ExecuteAsync()
    {
        if (ActionId is not null)
        {
            var action = Catalog.KosCatalog.Actions.First(a => a.Id == ActionId);
            if (action.Script is null)
            {
                await action.Run!(CancellationToken.None).ConfigureAwait(false);
                return ExitOk;
            }
            var r = await action.Script.RunAsync().ConfigureAwait(false);
            return r.ExitCode == 0 ? ExitOk : ExitScriptCode;
        }

        if (Writes.Count > 0)
        {
            foreach (var w in Writes)
            {
                var setting = StartupTarget(w);
                setting.Write(w.Value);
                Log.Info($"[elevated] {setting.Display} = {w.Value}");
            }
            return ExitOk;
        }
        if (TaskChange is not null) { ScheduledTaskProvider.SetEnabled(TaskChange.Path, TaskChange.Enabled); return ExitOk; }
        if (Service is not null) { ServiceStartupProvider.Apply(Service); return ExitOk; }
        if (Power.Count > 0) { PowerPlans.Apply(Power); return ExitOk; }
        if (PowerPlan is not null) { PowerPlans.Run(PowerPlan); return ExitOk; }
        if (PrepareLogs) return Log.Problem is null ? ExitOk : ExitFailed;
        if (Interrupts is not null) { Devices.Interrupts.Write(Interrupts); return ExitOk; }
        if (Gpu is not null) { GpuTuner.Execute(Gpu); return ExitOk; }
        if (CleanJunk.Count > 0) { foreach (var id in CleanJunk) JunkCleaner.Clean(JunkCleaner.Find(id)!); return ExitOk; }
        if (NetAdapter is not null) { NetAdapters.Execute(NetAdapter); return ExitOk; }
        if (Drivers is not null) { DriverStore.Execute(Drivers); return ExitOk; }
        if (Display is not null) { Monitors.Execute(Display); return ExitOk; }
        // The result (including a SCEWIN error) goes to Scewin.ResultFile; the exit code only says the request ran.
        if (Bios is not null) { await Scewin.RunAsync(Bios).ConfigureAwait(false); return ExitOk; }
        if (Debloat is not null) { await KosToolbox.Core.Debloat.DebloatCatalog.ExecuteAsync(Debloat).ConfigureAwait(false); return ExitOk; }
        if (Ddu is not null) { KosToolbox.Core.Drivers.Ddu.Execute(Ddu); return ExitOk; }
        // setup.exe's exit code only goes to the log; the toolbox confirms by reading the installed driver back.
        if (NvidiaInstall is not null) return NvidiaDriver.Execute(NvidiaInstall) == 0 ? ExitOk : ExitScriptCode;
        if (KosFix is not null) return KosState.KosStateFix.Execute(KosFix);
        // The tool's verdict goes to its result file; the exit code only says the request ran.
        if (Repair is not null) { await KosState.WindowsRepair.RunAsync(Repair).ConfigureAwait(false); return ExitOk; }
        if (Vbios is not null) { VbiosLoader.Execute(Vbios); return ExitOk; }
        if (RemoveDevices.Count > 0) return PnpDevices.RemoveNotPresent(RemoveDevices, out _).All(r => r.Error is null) ? ExitOk : ExitPartial;
        if (Snapshot is not null)
        {
            switch (Snapshot.Mode)
            {
                case SnapshotJob.Capture: ServiceSnapshots.Capture(Snapshot.Name!, Snapshot.Full, Snapshot.Force); return ExitOk;
                case SnapshotJob.Organize: ServiceSnapshots.Organize(); return ExitOk;
                default: return ServiceSnapshots.Restore(Snapshot.Name!, Snapshot.Services).AllOk ? ExitOk : ExitPartial;
            }
        }

        var tweak = Catalog.KosCatalog.Find(TweakId!)!;
        // The tweak scripts' exit code is not used: what confirms is re-reading the state.
        if (tweak.Scripts.TryGetValue(Option!, out var tweakScript))
        {
            await tweakScript.RunAsync().ConfigureAwait(false);
            return ExitOk;
        }
        foreach (var (setting, value) in MachineWrites(tweak, Option!))
        {
            setting.Write(value);
            Log.Info($"[elevated] {setting.Display} = {value ?? "(deleted)"}");
        }
        return ExitOk;
    }
}
