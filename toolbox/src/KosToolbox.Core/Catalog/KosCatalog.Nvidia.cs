using System.Net.Http;
using System.Xml;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Tweaks;
using Microsoft.Win32;
using static KosToolbox.Core.Settings.Hive;
using static KosToolbox.Core.Tweaks.Tweak;

namespace KosToolbox.Core.Catalog;

/// <summary>
/// Tab GPU › Tweaks: the functions of nvidia_tool.cmd (the KOS NVIDIA tool). Per-card tweaks use the connected NVIDIA
/// cards; with none, they are unavailable.
/// Only constants and methods here: static initializers across partial files have no guaranteed order.
/// </summary>
public static partial class KosCatalog
{
    const string NvTool = @"Setup\2-Install-Drivers\Nvidia\nvidia_tool.cmd";
    const string Nvlddmkm = @"SYSTEM\CurrentControlSet\Services\nvlddmkm";
    const string NvSoftware = @"SOFTWARE\NVIDIA Corporation";
    const string NipHost = "https://raw.githubusercontent.com/vzsxytsxs/host/refs/heads/main/nv/";

    /// <summary>Driver class key values of "Driver registry tweaks › Optimized": power and clock gating, ASPM, idle slowdown, ECC, DSC, HDCP 2.2, flips.</summary>
    static (string Name, uint Value)[] NvRegistryTweaks() => new (string, uint)[]
    {
        // Power management
        ("RmPowerFeature", 0x54455565), ("RmPowerFeature2", 0x05555555), ("D3PCLatency", 1), ("EnablePerformanceMode", 1),
        ("EnableRuntimePowerManagement", 0), ("EnableMsHybrid", 0), ("RMElcg", 0x55555554), ("RMGC6Feature", 0x000AAAAA),
        ("RMGC6Parameters", 0x55), ("RMGCOffFeature", 1), ("RMDidleFeatureGC5", 0x02AA8AAA), ("RMD3Feature", 2),
        ("RMClkSlowDown", 0x04400000), ("RmOptp2LowerMclk", 0), ("RmOverrideIdleSlowdownSettings", 0), ("RmClkPowerOffDramPllWhenUnused", 0),
        ("RMLpwrArch", 0x55555), ("RMLpwrEiClient", 5), ("RmLpwrCtrlGrRgParameters", 0x05555555), ("RmLpwrCtrlMsDifrCgParameters", 0x555),
        ("RmLpwrCtrlMsDifrSwAsrParameters", 0x1555), ("RmLpwrCtrlMsLtcParameters", 5), ("RmLpwrGrPgSwFilterFunction", 0), ("RmLpwrCacheStatsOnD3", 0),
        ("RmDwbMscg", 1), ("SlideMCLK", 0), ("RMEnableASPMAtLoad", 0), ("RMDisableGpuASPMFlags", 3), ("RMDisableOptimalPowerForPadlinkPll", 1),
        ("RMForceRtd3D3Hot", 2), ("RmMIONoPowerOff", 1), ("RM2644249", 1), ("RM303107", 0x10), ("RM2779240", 5), ("RM580312", 1),
        ("RMBug2519005War", 1), ("RmCeElcgWar1895530", 1), ("RmWar1760398", 1), ("RMOPSB", 0x2AA2), ("RmDisableInforomBBX", 0xF),
        ("RmPgCtrlParameters", 0x55555155), ("RmPgCtrlGrParameters", 0x55555555), ("RMElpgStateOnInit", 3),
        // Misc
        ("RmDisableACPI", 0x3FF), ("RMDisablePostL2Compression", 1), ("RMDisableNoncontigAlloc", 1), ("RMBandwidthFeature", 0x7103C400),
        ("RMBandwidthFeature2", 1), ("PreferSystemMemoryContiguous", 1), ("RMDisableEDC", 1), ("RMDisableIntrIllegalCompstatAccess", 1),
        ("RMDisableLRCCoalescing", 1), ("RmDisableHwFaultBuffer", 1),
        // HDCP 2.2 and ECC
        ("RmDisableHdcp22", 1), ("RMEnableL1ECC", 0), ("RMEnableSMECC", 0), ("RMEnableSHMECC", 0), ("RMAssertOnEccErrors", 0),
        // DSC, flips, DisplayPort
        ("EnableTiledDisplay", 0), ("DISABLE_DSC", 1), ("enableRS2FlipCollapse", 1), ("enableRS2ImmediateFlipCompletionReporting", 1),
        ("DP2X_IGNORE_CABLE_ID_CAPS", 1),
    };

    static async Task<string> LoadVbiosAsync(CancellationToken ct)
    {
        var path = ActionItem.PickFile?.Invoke("Load VBIOS", "VBIOS (*.rom;*.bin)|*.rom;*.bin|All files (*.*)|*.*");
        if (path is null) return "No file chosen: nothing was changed.";
        VbiosLoader.Check(path);
        var job = new VbiosJob { Path = path };
        if (Elevation.IsAdmin) VbiosLoader.Execute(job);
        else await TweakEngine.Instance.RunElevated(new ElevatedJob { Vbios = job }, ct);
        if (VbiosLoader.Loaded(path) != true) throw new InvalidOperationException("The VBIOS wasn't found in the driver's key after loading it.");
        TweakEngine.Instance.Pending.Mark("nv-vbios-load");
        return "VBIOS loaded (read back from the driver's key). The driver uses it after a restart.";
    }

    static async Task<string> RemoveVbiosAsync(CancellationToken ct)
    {
        if (VbiosLoader.Loaded() != true) return "No VBIOS is loaded from a file: the card uses its own.";
        var job = new VbiosJob();
        if (Elevation.IsAdmin) VbiosLoader.Execute(job);
        else await TweakEngine.Instance.RunElevated(new ElevatedJob { Vbios = job }, ct);
        if (VbiosLoader.Loaded() == true) throw new InvalidOperationException("The loaded VBIOS is still in the driver's key.");
        TweakEngine.Instance.Pending.Mark("nv-vbios-remove");
        return "Loaded VBIOS removed (read back). The card uses its own VBIOS after a restart.";
    }

    static List<(string InstanceId, string? DriverKey)> NvidiaGpus()
    {
        try { return GpuTuner.NvidiaAdapters(); }
        catch (Exception ex) { Log.Warn($"Listing NVIDIA GPUs failed: {ex.Message}"); return new(); }
    }

    /// <summary>A GPU tweak built from a fresh read of the cards (<see cref="Tweaks"/> keeps the class keys found at startup).</summary>
    internal static Tweak? FreshNvidiaTweak(string id) => NvidiaTweaks().FirstOrDefault(t => t.Id == id);

    static IEnumerable<Tweak> NvidiaTweaks()
    {
        var gpus = NvidiaGpus();
        var classKeys = gpus.Where(g => g.DriverKey is not null).Select(g => $@"SYSTEM\CurrentControlSet\Control\Class\{g.DriverKey}").ToList();
        string? NoNvidia() => gpus.Count == 0 ? "No NVIDIA GPU was found." : classKeys.Count == 0 ? "The NVIDIA driver isn't installed." : null;
        string? NoGpu() => gpus.Count == 0 ? "No NVIDIA GPU was found." : null;
        const string Driver = "Driver", Panel = "Control Panel and tray", Tasks = "Scheduled tasks";

        var pstate = Toggle("nv-pstate", "Dynamic P-states",
            "Lets the GPU lower clocks and voltage when idle. Off keeps it at full clocks (P0) all the time: more heat and power use.", Pages.Gpu, null, Driver);
        foreach (var k in classKeys) pstate.Add(R(LocalMachine, k, "DisableDynamicPstate"), (On, Target.SetOrAbsent("0")), (Off, S("1")));
        yield return pstate.With(t => { t.RequiresReboot = true; t.Compatibility = NoNvidia; t.Source = NvTool + " (Dynamic Power State)"; });

        var hdcp = Toggle("nv-hdcp", "HDCP (copy protection)",
            "High-bandwidth Digital Content Protection on HDMI/DisplayPort. Off can stop protected video (streaming services, Blu-ray) from playing.", Pages.Gpu, null, Driver);
        foreach (var k in classKeys) hdcp.Add(R(LocalMachine, k, "RMHdcpKeyglobZero"), (On, Target.SetOrAbsent("0")), (Off, S("1")));
        yield return hdcp.With(t => { t.RequiresReboot = true; t.Compatibility = NoNvidia; t.Source = NvTool + " (HDCP)"; });

        var msi = Toggle("nv-msi", "MSI mode (GPU)",
            "Message-signaled interrupts for the GPU, with the interrupt priority left undefined. The same values as Devices › Interrupts.", Pages.Gpu, null, Driver);
        foreach (var (id, _) in gpus)
        {
            var irq = $@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters\Interrupt Management";
            msi.Add(R(LocalMachine, irq + @"\MessageSignaledInterruptProperties", "MSISupported"), (On, S("1")), (Off, S("0")));
            msi.Add(R(LocalMachine, irq + @"\Affinity Policy", "DevicePriority"), (On, S("0")), (Off, Any));
        }
        yield return msi.With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Basics Tweaks › MSI)"; });

        yield return Toggle("nv-write-combining", "Write combining",
                "The driver's write-combined memory writes. The NVIDIA tool turns it off (DisableWriteCombining).", Pages.Gpu, null, Driver)
            .Reg(LocalMachine, Nvlddmkm, "DisableWriteCombining", RegistryValueKind.DWord, null, "1")
            .With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Basics Tweaks › Write Combining)"; });

        yield return Toggle("nv-telemetry", "Driver telemetry",
                "Telemetry sent by the display driver (SendTelemetryData).", Pages.Gpu, null, Driver)
            .Reg(LocalMachine, Nvlddmkm + @"\Global\Startup", "SendTelemetryData", RegistryValueKind.DWord, null, "0")
            .With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Basics Tweaks › Telemetry)"; });

        var logging = Toggle("nv-logging", "Driver event logging", "Warning, paging, event and error entries the driver writes to the event log.", Pages.Gpu, null, Driver);
        foreach (var name in new[] { "LogWarningEntries", "LogPagingEntries", "LogEventEntries", "LogErrorEntries" })
            logging.Add(R(LocalMachine, Nvlddmkm + @"\Parameters", name), (On, Target.SetOrAbsent("1")), (Off, S("0")));
        yield return logging.With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Misc › Nvidia Logging)"; });

        yield return Toggle("nv-display-power-saving", "Display power saving", "The driver's display power saving.", Pages.Gpu, null, Driver)
            .Add(R(LocalMachine, Nvlddmkm + @"\Global\NVTweak", "DisplayPowerSaving"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .Add(R(LocalMachine, NvSoftware + @"\Global\NVTweak", "DisplayPowerSaving"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Misc › Display Power Saving)"; });

        // ECC through NVML (like nvidia-smi -e): available when NVML reports support (professional cards and some GeForce RTX 40/50).
        var ecc = Toggle("nv-ecc", "Error correcting code (ECC)", "ECC on the GPU memory: catches and fixes memory errors, at a small cost in performance and usable VRAM. Available on cards that support it (workstation and data-center cards, and some GeForce RTX 40/50 like the RTX 4090 and 5090); detected through the driver.", Pages.Gpu, null, Driver);
        List<GpuDevice> nvml;
        try { nvml = gpus.Count == 0 ? new() : GpuTuner.List(); }
        catch (Exception ex) { nvml = new(); Log.Warn($"ECC: NVML isn't available: {ex.Message}"); }
        foreach (var d in nvml) ecc.Add(new NvidiaEccSetting(d.BusId), (On, S("1")), (Off, S("0")));
        yield return ecc.With(t => { t.RequiresReboot = true; t.Compatibility = NoNvidia; t.Source = NvTool + " (Misc › ECC, nvidia-smi -e; here through NVML)"; });

        var regTweaks = Choice("nv-registry-tweaks", "Driver registry tweaks",
            "About 70 driver registry values that turn off power gating, clock gating, ASPM, idle slowdown and several protections. " +
            "Optimized keeps the GPU at full power all the time: more heat and power. Driver default removes every override. " +
            "Optimized also turns off DSC (high-refresh modes may go black), HDCP 2.2 (protected video stops) and ECC.",
            Pages.Gpu, null, Driver, ("default", "Driver default"), ("optimized", "Optimized"));
        foreach (var k in classKeys)
            foreach (var (name, value) in NvRegistryTweaks())
                regTweaks.Add(R(LocalMachine, k, name), ("default", Del), ("optimized", S(value.ToString())));
        regTweaks.Add(R(LocalMachine, Nvlddmkm, "EnableHDAudioD3Cold"), ("default", Del), ("optimized", S("0")));
        yield return regTweaks.With(t =>
        {
            t.RequiresReboot = true; t.WindowsDefault = "default"; t.Compatibility = NoNvidia; t.Source = "nvlddmkm registry values (driver class key)";
            t.ConfirmationOption = "optimized";
            t.ConfirmationNote = "Optimized turns off DSC (high-refresh modes may go black), HDCP 2.2 (streaming DRM stops working) and ECC. The GPU stays at full power all the time. Driver default undoes it after a restart.";
        });

        yield return Toggle("nv-container", "NVIDIA Control Panel (Display Container service)",
                "The NVDisplay.ContainerLocalSystem service, which the NVIDIA Control Panel and the tray icon need.", Pages.Gpu, null, Panel)
            .Svc("NVDisplay.ContainerLocalSystem", "2", "4")
            .With(t => { t.RequiresReboot = true; t.Source = NvTool + " (Nvidia Control Panel)"; });

        yield return Toggle("nv-tray", "NVIDIA tray icon", "The NVIDIA icon in the notification area.", Pages.Gpu, null, Panel)
            .Reg(LocalMachine, NvSoftware + @"\Global\CoProcManager", "ShowTrayIcon", RegistryValueKind.DWord, null, "0")
            .Reg(LocalMachine, NvSoftware + @"\NvTray", "StartOnLogin", RegistryValueKind.DWord, null, "0")
            .Reg(LocalMachine, Nvlddmkm + @"\Global\NVTweak", "HideXGpuTrayIcon", RegistryValueKind.DWord, null, "1")
            .With(t => { t.RequiresReboot = true; t.Compatibility = NoGpu; t.Source = NvTool + " (Misc › Nvidia Tray Icon)"; });

        yield return Toggle("nv-dlss-indicator", "DLSS indicator", "Shows the DLSS version and mode in the corner of games that use it.", Pages.Gpu, null, Panel)
            .Add(R(LocalMachine, NvSoftware + @"\Global\NGXCore", "ShowDlssIndicator"), (On, S("1024")), (Off, Target.SetOrAbsent("0")))
            .With(t => { t.Compatibility = NoGpu; t.Source = NvTool + " (Misc › DLSS Indicator)"; });

        var tasks = Toggle("nv-tasks", "NVIDIA scheduled tasks",
            "Crash reports (NvTmRep), the daily driver update check and the GeForce Experience self-update. Tasks that don't exist are skipped.", Pages.Gpu, null, Tasks);
        foreach (var name in new[] { "NvTmRep_CrashReport1", "NvTmRep_CrashReport2", "NvTmRep_CrashReport3", "NvTmRep_CrashReport4", "NvDriverUpdateCheckDaily", "NVIDIA GeForce Experience SelfUpdate" })
            tasks.AddSpec(new ScheduledTaskSetting($@"\{name}_{{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}}"), optional: true, (On, S("1")), (Off, S("0")));
        yield return tasks.With(t => t.Source = NvTool + " (Basics Tweaks › Crash Report, Driver Update Check, GeForce Experience Self Update)");
    }

    static IEnumerable<ActionItem> NvidiaActions()
    {
        const string Vbios = "VBIOS";
        yield return new()
        {
            Id = "nv-vbios-load", Name = "Load VBIOS from file", Page = Pages.Gpu, Group = Vbios, ButtonText = "Load", RequiresReboot = true,
            Description = "Loads a VBIOS without flashing it, by writing it to the driver's registry key (driver 616.56 or later); the driver uses it after a restart. " +
                          "The file is checked first: an NVIDIA image for an installed card, with a correct checksum, of at most 512 KB. " +
                          "Warning: a modified VBIOS can run the card past its power, clock and voltage limits and damage it. Remove loaded VBIOS goes back to the card's own.",
            Run = LoadVbiosAsync, Source = "Display class key: vbios (REG_BINARY), EnableGpuFirmware",
        };
        yield return new()
        {
            Id = "nv-vbios-remove", Name = "Remove loaded VBIOS", Page = Pages.Gpu, Group = Vbios, ButtonText = "Remove", RequiresReboot = true,
            Description = "Deletes the VBIOS loaded from a file and puts EnableGpuFirmware back as it was: the card uses its own (flashed) VBIOS after a restart.",
            Run = RemoveVbiosAsync, Source = "Display class key: vbios (REG_BINARY), EnableGpuFirmware",
        };

        const string Profiles = "Driver profiles (NVIDIA Profile Inspector)", Maintenance = "Maintenance";
        foreach (var (id, name, label) in new[] { ("basic", "Basic profile", "Basic Profile"), ("latency", "Latency profile", "Latency Profile"), ("fps", "FPS average profile", "Fps Average") })
            yield return new()
            {
                Id = $"nvpi-{id}", Name = name, Page = Pages.Gpu, Group = Profiles, ButtonText = "Import",
                Description = $"Downloads {id}.nip from github.com/vzsxytsxs/host and imports it into the driver, replacing the settings of the profile it targets (like Profile Inspector's Replace). The previous settings are saved as a .nip backup first.",
                Run = ct => ImportNipAsync(id, ct), Source = NvTool + $" (Nvidia Profile Inspector › {label})",
            };

        yield return new()
        {
            Id = "nv-shader-cache", Name = "Clear shader cache", Page = Pages.Gpu, Group = Maintenance, ButtonText = "Clear",
            Description = @"Deletes the NVIDIA shader cache (%LocalAppData%\NVIDIA\DXCache and GLCache). Games rebuild it, so the first launch after is slower.",
            Run = ClearNvidiaShaderCacheAsync, Source = NvTool + " (Clear Shader Cache)",
        };

        var debloat = new ScriptRef(@"Setup\2-Install-Drivers\Nvidia\nvidia-debloat.cmd", "0482223CFA286A21785990B199678C457B26E2CA75CCBFAB67690FA6E2133F7B") { Timeout = TimeSpan.FromMinutes(10) };
        const string Removes = "telemetry (NvTelemetry64), NvCamera, NVWMI, the CUDA PTX JIT compiler, NVENC/NVDEC Media Foundation encoders (nvEncMFT/nvDevMFT), nvtopps, the profile updater and debug tools. Deleted driver files only come back by reinstalling the driver.";
        yield return new()
        {
            Id = "nv-debloat", Name = "Driver debloat (keeps NVIDIA app / GeForce Experience)", Page = Pages.Gpu, Group = Maintenance, ButtonText = "Debloat", RequiresReboot = true,
            Description = "Advanced. Deletes driver components: " + Removes + " Apps that use NVENC through Media Foundation or CUDA PTX may stop working until then.",
            Script = debloat with { Args = "keepapp" }, Source = NvTool + " (Driver Debloat, answer y); converted to nvidia-debloat.cmd",
        };
        yield return new()
        {
            Id = "nv-debloat-app", Name = "Driver debloat (without NVIDIA app / GeForce Experience)", Page = Pages.Gpu, Group = Maintenance, ButtonText = "Debloat", RequiresReboot = true,
            Description = "Advanced. Same as above, and also removes C:\\Program Files\\NVIDIA Corporation and the capture libraries (NvFBC/NvIFR) the NVIDIA app, GeForce Experience and some streaming apps use.",
            Script = debloat with { Args = "removeapp" }, Source = NvTool + " (Driver Debloat, answer n); converted to nvidia-debloat.cmd",
        };
    }

    /// <summary>Downloads a .nip profile from the KOS repository and imports it into the driver (replaces the profile; backup first).</summary>
    static async Task<string> ImportNipAsync(string name, CancellationToken ct)
    {
        var url = NipHost + name + ".nip";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1 << 20 };
        var bytes = await http.GetByteArrayAsync(url, ct);
        // Only a Profile Inspector profile (XML with an ArrayOfProfile root) goes on to it; any other response is refused.
        // Read as text (BOM decides): the repository's .nip files are UTF-8 but declare encoding="utf-16".
        try
        {
            using var text = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            using var reader = XmlReader.Create(new StringReader(text.ReadToEnd()), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            reader.MoveToContent();
            if (reader.LocalName != "ArrayOfProfile") throw new XmlException("unexpected root element");
        }
        catch (XmlException ex) { throw new InvalidOperationException($"{url} isn't an NVIDIA Profile Inspector profile ({ex.Message})."); }
        var dir = Path.Combine(AppPaths.DataRoot, "nip");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name + ".nip");
        await File.WriteAllBytesAsync(file, bytes, ct);
        // Always "Replace" (without Profile Inspector's prompt): the profile goes back to default and receives only what the .nip has.
        var (applied, refused, backups) = await Task.Run(() => DriverSettings.ReplaceFromNip(file, Path.Combine(dir, "backups")), ct);
        Log.Info($"Driver profile {name}.nip imported (replace): {applied} setting(s); refused: {string.Join(", ", refused)}; backup: {string.Join(", ", backups)}");
        return $"{name}.nip imported: {applied} driver setting(s) written, replacing the profile's previous settings (backup in {Path.Combine(dir, "backups")})."
               + (refused.Count > 0 ? $" Not in this driver: {string.Join(", ", refused)}." : "");
    }

    static Task<string> ClearNvidiaShaderCacheAsync(CancellationToken ct)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        int deleted = 0, skipped = 0;
        foreach (var dir in new[] { @"NVIDIA\DXCache", @"NVIDIA\GLCache" }.Select(d => Path.Combine(local, d)).Where(Directory.Exists))
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                try { f.Delete(); deleted++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; } // in use by an open game
            }
        Log.Info($"NVIDIA shader cache: {deleted} files deleted, {skipped} skipped.");
        return Task.FromResult(skipped == 0 ? $"{deleted} files deleted." : $"{deleted} files deleted; {skipped} in use were skipped (close the games and try again).");
    }
}
