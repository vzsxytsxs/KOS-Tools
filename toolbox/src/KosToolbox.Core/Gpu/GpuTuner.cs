using System.Security.AccessControl;
using System.Security.Principal;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Gpu;

public sealed record GpuDevice(string Name, string BusId);

/// <summary>
/// The card's bus: the BAR1 window next to the VRAM (about the VRAM size = Resizable BAR on, 256 MB = off) and the top PCIe generation
/// of the card itself next to the link's (the lower of the card and the slot). Null where NVML doesn't report it.
/// </summary>
public sealed record GpuBus(ulong? Bar1Bytes, ulong? VramBytes, uint? CardGen, uint? LinkGen, uint? LinkWidth);

/// <summary>Live readings; null = NVML does not report this value on this card or driver.</summary>
public sealed class GpuSensors
{
    public uint? TemperatureC { get; init; }
    public uint? GraphicsMhz { get; init; }
    public uint? MemoryMhz { get; init; }
    public uint? SmMhz { get; init; }
    public uint? GpuLoad { get; init; }
    public uint? MemoryLoad { get; init; }
    public uint? PowerMw { get; init; }
    public uint? PowerLimitMw { get; init; }
    public IReadOnlyList<uint?> Fans { get; init; } = Array.Empty<uint?>();
    public ulong? VramUsed { get; init; }
    public ulong? VramTotal { get; init; }
    public uint? PcieGen { get; init; }
    public uint? PcieWidth { get; init; }
    public uint? PcieGenMax { get; init; }
    public uint? PcieWidthMax { get; init; }
    public uint? PState { get; init; }
    public ulong? ThrottleReasons { get; init; }
    public int? VoltageUv { get; init; }
    public double? HotspotC { get; init; }
    /// <summary>Only memory with a junction sensor (GDDR6X and newer).</summary>
    public double? MemoryJunctionC { get; init; }
    public uint? PcieTxKb { get; init; }
    public uint? PcieRxKb { get; init; }
}

/// <summary>Range of a clock offset; <see cref="ClockOffsetsApi"/> = nvmlDevice[GS]etClockOffsets (new drivers).</summary>
public sealed record GpuRange(int Current, int Min, int Max, bool ClockOffsetsApi, bool Nvapi = false);

/// <summary>Current values and limits reported by the driver itself; a "Problem" says why the control is disabled.</summary>
public sealed class GpuState
{
    public string BusId { get; init; } = "";
    public uint? PowerLimitMw { get; init; }
    public uint? PowerMinMw { get; init; }
    public uint? PowerMaxMw { get; init; }
    public uint? PowerDefaultMw { get; init; }
    public string? PowerProblem { get; init; }
    public GpuThermalLimit? Thermal { get; init; }
    public string? ThermalProblem { get; init; }
    /// <summary>true = the driver's thermal slowdown is on (the default); null = unavailable.</summary>
    public bool? ThermalSlowdown { get; init; }
    public string? ThermalSlowdownProblem { get; init; }
    public GpuRange? Core { get; init; }
    public string? CoreProblem { get; init; }
    public GpuRange? Memory { get; init; }
    public string? MemoryProblem { get; init; }
    public GpuVfCurve? Curve { get; init; }
    public string? CurveProblem { get; init; }
    /// <summary>0 = unlocked; null = unavailable.</summary>
    public int? LockVoltageUv { get; init; }
    public string? LockProblem { get; init; }
    /// <summary>Clock locked by the toolbox in this boot (0 = none; null = unavailable). NVML does not read the lock back.</summary>
    public int? ClockLockMhz { get; init; }
    public string? ClockLockProblem { get; init; }
    /// <summary>Minimum graphics clock set by the toolbox in this boot (0 = none; null = unavailable). Shares the one NVML lock with <see cref="ClockLockMhz"/>.</summary>
    public int? MinClockMhz { get; init; }
    /// <summary>The highest graphics clock the card supports (the top of a minimum-clock lock); null = not read.</summary>
    public uint? MaxClockMhz { get; init; }
    public int FanCount { get; init; }
    public uint FanMin { get; init; }
    public uint FanMax { get; init; }
    /// <summary>true = all fans on automatic control; null = the policy could not be read.</summary>
    public bool? FanAuto { get; init; }
    public uint? FanTarget { get; init; }
    public string? FanProblem { get; init; }
}

/// <summary>Tuning request (and gpu-tuning.json format). Null = leave that value alone.</summary>
public sealed class GpuTuning
{
    public string PciBusId { get; set; } = "";
    public uint? PowerLimitMw { get; set; }
    public int? TemperatureLimitC { get; set; }
    /// <summary>Null = leave unchanged; false = turn the driver's thermal slowdown off.</summary>
    public bool? ThermalSlowdown { get; set; }
    public int? CoreOffsetMhz { get; set; }
    public int? MemOffsetMhz { get; set; }
    public List<GpuVfChange>? CurveChanges { get; set; }
    /// <summary>Null = leave unchanged; 0 = unlock; positive = lock to this V/F voltage anchor.</summary>
    public int? LockVoltageUv { get; set; }
    /// <summary>Null = leave unchanged; 0 = unlock; positive = lock the core clock to this value (nvmlDeviceSetGpuLockedClocks).</summary>
    public int? ClockLockMhz { get; set; }
    /// <summary>Null = leave unchanged; 0 = off; positive = the card's graphics clock never goes below this (the lock runs from it to the card's maximum), so it doesn't idle at low clocks.</summary>
    public int? MinClockMhz { get; set; }
    public uint? FanPercent { get; set; }
    public bool FanAuto { get; set; }

    public override string ToString() =>
        $"{PciBusId}: power={PowerLimitMw?.ToString() ?? "-"} mW, temp={TemperatureLimitC?.ToString() ?? "-"} °C, slowdown={ThermalSlowdown switch { true => "on", false => "off", null => "-" }}, core={CoreOffsetMhz?.ToString() ?? "-"} MHz, memory={MemOffsetMhz?.ToString() ?? "-"} MHz, VF={CurveChanges?.Count ?? 0} points, lock={LockVoltageUv?.ToString() ?? "-"} uV, clock lock={ClockLockMhz?.ToString() ?? "-"} MHz, min clock={MinClockMhz?.ToString() ?? "-"} MHz, fan={(FanAuto ? "auto" : FanPercent?.ToString() ?? "-")}";
}

/// <summary>Elevated request from the GPU page: apply and/or turn on (with the values) or turn off applying at logon.</summary>
public sealed class GpuJob
{
    /// <summary>Written now: only the values the user confirmed.</summary>
    public GpuTuning? Tuning { get; set; }
    /// <summary>Saved for logon and not written now; only with <see cref="SignIn"/> = true.</summary>
    public GpuTuning? Saved { get; set; }
    /// <summary>true = saves <see cref="Saved"/> and creates the logon task; false = removes both; null = only applies.</summary>
    public bool? SignIn { get; set; }
}

/// <summary>
/// NVIDIA cards through the public NVML: live readings, driver-reported limits, validated writes and the logon
/// "--gpu-apply" task. Every write requires administrator and is revalidated against the device's own limits.
/// </summary>
public static class GpuTuner
{
    public const string TaskName = "KOS Toolbox GPU tuning";
    static readonly object Gate = new();

    public static string SignInDirectory => Path.Combine(AppPaths.TheKosRoot, "Toolbox");
    public static string SignInFile => Path.Combine(SignInDirectory, "gpu-tuning.json");
    static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>"dddd:bb:dd.f" or "dddddddd:bb:dd.f" in hex, function 0-7. No Regex: it was the only use in KOS Engine (~1 MB).</summary>
    internal static bool IsBusId(string? id)
    {
        if (id is null || id.Length < 12) return false;
        var domain = id.Length - 8;
        if (domain is < 4 or > 8 || id[domain] != ':' || id[domain + 3] != ':' || id[domain + 6] != '.' || id[^1] is < '0' or > '7') return false;
        for (var i = 0; i < id.Length - 2; i++)
            if (i != domain && i != domain + 3 && i != domain + 6 && !char.IsAsciiHexDigit(id[i])) return false;
        return true;
    }

    /// <summary>Typed watts (dot or comma decimal) into milliwatts; null if not a positive number.</summary>
    public static uint? ParseWatts(string text)
    {
        var s = text.Trim().Replace(',', '.');
        return decimal.TryParse(s, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var w)
               && w > 0 && w <= 4_000_000 ? (uint)Math.Round(w * 1000) : null;
    }

    public static int? ParseMhz(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public static uint? ParsePercent(string text) =>
        uint.TryParse(text.Trim().TrimEnd('%').Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>NVIDIA cards. Throws with the reason if NVML is not available.</summary>
    public static List<GpuDevice> List()
    {
        lock (Gate)
        {
            if (Nvml.Initialize() is { } problem) throw new InvalidOperationException(problem);
            uint count = 0;
            if (Nvml.Call<Nvml.OutUInt>("nvmlDeviceGetCount_v2", f => f(out count), out var error) != Nvml.Success) throw new InvalidOperationException(error);
            var list = new List<GpuDevice>();
            for (uint i = 0; i < count; i++)
            {
                var index = i;
                IntPtr d = IntPtr.Zero;
                Nvml.PciInfo pci = default;
                var name = new byte[96]; // NVML_DEVICE_NAME_V2_BUFFER_SIZE
                if (Nvml.Call<Nvml.ByIndex>("nvmlDeviceGetHandleByIndex_v2", f => f(index, out d), out var e1) != Nvml.Success
                    || Nvml.Call<Nvml.DevPci>("nvmlDeviceGetPciInfo_v3", f => f(d, out pci), out e1) != Nvml.Success)
                {
                    Log.Warn($"GPU {i}: {e1}");
                    continue;
                }
                if (pci.BusId is not { } busId || !IsBusId(busId)) { Log.Warn($"GPU {i}: unexpected PCI bus id {pci.BusId}"); continue; }
                var named = Nvml.Call<Nvml.DevText>("nvmlDeviceGetName", f => f(d, name, (uint)name.Length), out _) == Nvml.Success;
                list.Add(new GpuDevice(named ? Nvml.Ansi(name) : Txt.GpuUnnamed, busId));
            }
            return list;
        }
    }

    public static string? DriverVersion()
    {
        lock (Gate)
        {
            if (Nvml.Initialize() is not null) return null;
            var buffer = new byte[80]; // NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE
            return Nvml.Call<Nvml.SysText>("nvmlSystemGetDriverVersion", f => f(buffer, (uint)buffer.Length), out _) == Nvml.Success ? Nvml.Ansi(buffer) : null;
        }
    }

    /// <summary>Video cards from other vendors (not yet supported), through SetupAPI.</summary>
    public static List<string> OtherAdapters() => PnpDevices.Enumerate("PCI", presentOnly: true)
        .Where(d => d.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && !d.InstanceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        .Select(d => d.Name).ToList();

    /// <summary>
    /// Connected NVIDIA cards (with or without driver/NVML): PCI instance id and the driver key in
    /// Control\Class (e.g. "{4d36e968-…}\0000"; null without an installed driver). Basis for the Tweaks tab settings.
    /// </summary>
    public static List<(string InstanceId, string? DriverKey)> NvidiaAdapters() => PnpDevices.Enumerate("PCI", presentOnly: true)
        .Where(d => d.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && d.InstanceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        .Select(d =>
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{d.InstanceId}");
            return (d.InstanceId, key?.GetValue("Driver") as string);
        })
        .ToList();

    /// <summary>ECC mode that applies after a restart; null when the card has no ECC (GeForce: only professional cards do).</summary>
    public static bool? EccPending(string busId)
    {
        lock (Gate)
        {
            var d = Open(busId);
            uint current = 0, pending = 0;
            var code = Nvml.Call<Nvml.DevTwoUInt>("nvmlDeviceGetEccMode", f => f(d, out current, out pending), out var error);
            if (code is Nvml.NotSupported or Nvml.FunctionNotFound) return null;
            if (code != Nvml.Success) throw new InvalidOperationException(error);
            return pending == 1;
        }
    }

    /// <summary>Turns ECC on/off (like nvidia-smi -e); applies after a restart.</summary>
    public static void SetEcc(string busId, bool on)
    {
        lock (Gate)
        {
            var d = Open(busId);
            if (Nvml.Call<Nvml.DevSetUInt>("nvmlDeviceSetEccMode", f => f(d, on ? 1u : 0u), out var error) != Nvml.Success)
                throw new InvalidOperationException(error);
            Log.Info($"GPU {busId}: ECC = {(on ? "on" : "off")} after restart");
        }
    }

    /// <summary>How many times NVML or NVAPI reopened the session (driver restarted); see <see cref="GpuResetWatch"/>.</summary>
    public static int Generation => Nvml.Generation + Nvapi.Generation;

    /// <summary>
    /// KOS Engine, once nothing it runs is reading the card through NVML: closes the session so the driver's nvml.dll unloads and its
    /// ~20 MB of private memory go back. The next NVML call opens it again. Under <see cref="Gate"/>: no call is inside NVML meanwhile.
    /// </summary>
    public static void ReleaseNvml()
    {
        lock (Gate) if (Nvml.Close()) Log.Info("NVML closed.");
    }

    /// <summary>KOS Engine: NVML opened inside the scope is closed at its end; a session already open (a reader still using it) stays.</summary>
    public static NvmlScope UseNvml() => new(Nvml.Loaded);

    public readonly struct NvmlScope(bool wasOpen) : IDisposable
    {
        public void Dispose() { if (!wasOpen) ReleaseNvml(); }
    }

    /// <summary>
    /// Checks the card (reopens a session the driver dropped): NVAPI, and NVML only when something in this process already opened it.
    /// Opening NVML just to check would cost ~20 MB; a restart is noticed by the adapter's LUID.
    /// </summary>
    public static void Probe(string busId)
    {
        lock (Gate)
        {
            if (Nvml.Loaded) Open(busId);
            Nvapi.ReadVoltage(busId);
        }
    }

    static IntPtr Open(string busId)
    {
        if (!IsBusId(busId)) throw new InvalidOperationException(Txt.GpuInvalidDevice(busId));
        // After the driver restarts, the NVML session is dead (handle or temperature fail): reopen once.
        for (var attempt = 0; ; attempt++)
        {
            if (Nvml.Initialize() is { } problem) throw new InvalidOperationException(problem);
            var d = IntPtr.Zero;
            var code = Nvml.Call<Nvml.ByBusId>("nvmlDeviceGetHandleByPciBusId_v2", f => f(busId, out d), out var error);
            // Lost session: NVML_ERROR_UNINITIALIZED (1), DRIVER_NOT_LOADED (9), GPU_IS_LOST (15), OPERATING_SYSTEM (17, what every
            // call returns after the adapter was disabled and enabled again) or UNKNOWN (999) on a simple read.
            var probe = code == Nvml.Success && Nvml.Fn<Nvml.DevArgUInt>("nvmlDeviceGetTemperature") is { } t ? t(d, Nvml.TemperatureGpu, out _) : code;
            if (code == Nvml.Success && probe is not (1 or 9 or 15 or 17 or 999)) return d;
            if (attempt > 0) throw new InvalidOperationException(code != Nvml.Success ? error : Nvml.Describe(probe));
            Nvml.Reset();
        }
    }

    static uint? U(IntPtr d, string fn) => Nvml.Fn<Nvml.DevUInt>(fn) is { } f && f(d, out var v) == Nvml.Success ? v : null;
    static ulong? U64(IntPtr d, string fn) => Nvml.Fn<Nvml.DevULong>(fn) is { } f && f(d, out var v) == Nvml.Success ? v : null;
    static uint? U(IntPtr d, string fn, uint arg) => Nvml.Fn<Nvml.DevArgUInt>(fn) is { } f && f(d, arg, out var v) == Nvml.Success ? v : null;

    /// <param name="throughput">Also the PCIe throughput, which NVML measures over 20 ms per direction (blocks meanwhile).</param>
    public static GpuSensors ReadSensors(string busId, bool throughput = true)
    {
        lock (Gate)
        {
            var d = Open(busId);
            Nvml.Memory? mem = Nvml.Fn<Nvml.DevMemory>("nvmlDeviceGetMemoryInfo") is { } fm && fm(d, out var m) == Nvml.Success ? m : null;
            Nvml.Utilization? util = Nvml.Fn<Nvml.DevUtil>("nvmlDeviceGetUtilizationRates") is { } fu && fu(d, out var u) == Nvml.Success ? u : null;
            var fans = U(d, "nvmlDeviceGetNumFans") ?? 0;
            var (hotspot, junction) = Nvapi.ReadThermalSensors(busId);
            return new GpuSensors
            {
                TemperatureC = U(d, "nvmlDeviceGetTemperature", Nvml.TemperatureGpu),
                GraphicsMhz = U(d, "nvmlDeviceGetClockInfo", Nvml.ClockGraphics),
                MemoryMhz = U(d, "nvmlDeviceGetClockInfo", Nvml.ClockMem),
                SmMhz = U(d, "nvmlDeviceGetClockInfo", Nvml.ClockSm),
                GpuLoad = util?.Gpu,
                MemoryLoad = util?.Memory,
                PowerMw = U(d, "nvmlDeviceGetPowerUsage"),
                PowerLimitMw = U(d, "nvmlDeviceGetEnforcedPowerLimit"),
                Fans = Enumerable.Range(0, (int)Math.Min(fans, 8)).Select(i => U(d, "nvmlDeviceGetFanSpeed_v2", (uint)i)).ToList(),
                VramUsed = mem?.Used,
                VramTotal = mem?.Total,
                PcieGen = U(d, "nvmlDeviceGetCurrPcieLinkGeneration"),
                PcieWidth = U(d, "nvmlDeviceGetCurrPcieLinkWidth"),
                PcieGenMax = U(d, "nvmlDeviceGetMaxPcieLinkGeneration"),
                PcieWidthMax = U(d, "nvmlDeviceGetMaxPcieLinkWidth"),
                PState = U(d, "nvmlDeviceGetPerformanceState"),
                ThrottleReasons = U64(d, "nvmlDeviceGetCurrentClocksThrottleReasons"),
                VoltageUv = Nvapi.ReadVoltage(busId),
                HotspotC = hotspot,
                MemoryJunctionC = junction,
                PcieTxKb = throughput ? U(d, "nvmlDeviceGetPcieThroughput", 0) : null,
                PcieRxKb = throughput ? U(d, "nvmlDeviceGetPcieThroughput", 1) : null,
            };
        }
    }

    /// <summary>
    /// KOS Engine's gamemode readers: NVAPI (no ~20 MB NVML session) unless board power is wanted or NVML is already open, then
    /// the full NVML reading (power and power limit only come from NVML).
    /// </summary>
    public static GpuSensors ReadSensorsLight(string busId, bool power = false) =>
        power || Nvml.Loaded ? ReadSensors(busId, throughput: false) : Nvapi.ReadLight(busId);

    /// <summary>The first NVIDIA card through NVAPI, NVML only when it's already open; null when there's none.</summary>
    public static GpuDevice? FirstLight() => Nvml.Loaded ? List().FirstOrDefault() : Nvapi.List().FirstOrDefault();

    public static GpuBus ReadBus(string busId)
    {
        lock (Gate)
        {
            var d = Open(busId);
            ulong? Total(string fn) => Nvml.Fn<Nvml.DevMemory>(fn) is { } f && f(d, out var m) == Nvml.Success ? m.Total : null; // nvmlBAR1Memory_t has the same layout
            return new GpuBus(Total("nvmlDeviceGetBAR1MemoryInfo"), Total("nvmlDeviceGetMemoryInfo"),
                U(d, "nvmlDeviceGetGpuMaxPcieLinkGeneration"), U(d, "nvmlDeviceGetMaxPcieLinkGeneration"), U(d, "nvmlDeviceGetMaxPcieLinkWidth"));
        }
    }

    public static GpuState ReadState(string busId)
    {
        lock (Gate) return ReadState(Open(busId), busId);
    }

    /// <summary>
    /// The fan curve's range and API: NVAPI when the card's fan coolers answer there (NVML's session costs KOS Engine ~20 MB of
    /// private memory while it is open, and a curve reads it every second; NVAPI's ~2 MB), otherwise NVML, which refuses cards without manual fan control.
    /// </summary>
    internal static (uint Min, uint Max, bool Nvapi) FanCurveRange(string busId, bool allowNvapi)
    {
        if (!IsBusId(busId)) throw new InvalidOperationException(Txt.GpuInvalidDevice(busId));
        if (allowNvapi)
            try { var (min, max) = Nvapi.ReadFanRange(busId); return (min, max, true); }
            catch (Exception ex) { Log.Info($"Fan curve through NVML: NVAPI fan control unavailable ({ex.Message})."); }
        var s = ReadState(busId);
        if (s.FanProblem is { } problem) throw new InvalidOperationException(problem);
        return (s.FanMin, s.FanMax, false);
    }

    /// <summary>Core temperature in °C (null if the read fails). Used by the fan curve.</summary>
    internal static uint? ReadTemperature(string busId, bool nvapi)
    {
        if (nvapi) return (uint?)Nvapi.ReadCoreTemperature(busId);
        lock (Gate) return U(Open(busId), "nvmlDeviceGetTemperature", Nvml.TemperatureGpu);
    }

    /// <summary>All fans at the requested %, or back to the driver's automatic mode with null. Throws if any fails.</summary>
    internal static void SetFans(string busId, uint? percent, bool nvapi)
    {
        if (nvapi) { Nvapi.SetFans(busId, percent); return; }
        lock (Gate)
        {
            var d = Open(busId);
            var fans = U(d, "nvmlDeviceGetNumFans") ?? 0;
            for (uint i = 0; i < fans; i++)
            {
                var fan = i;
                var code = percent is { } pct
                    ? Nvml.Call<Nvml.DevSetTwoUInt>("nvmlDeviceSetFanSpeed_v2", f => f(d, fan, pct), out var error)
                    : Nvml.Call<Nvml.DevSetUInt>("nvmlDeviceSetDefaultFanSpeed_v2", f => f(d, fan), out error);
                if (code != Nvml.Success) throw new InvalidOperationException(error);
            }
        }
    }

    static GpuState ReadState(IntPtr d, string busId)
    {
        // Power limit: needs the reading, the range, the default and the write function.
        string? powerProblem = null;
        uint min = 0, max = 0, def = 0, limit = 0;
        if (!Nvml.Has("nvmlDeviceSetPowerManagementLimit")) powerProblem = Txt.GpuNotExported("nvmlDeviceSetPowerManagementLimit");
        else if (Nvml.Call<Nvml.DevTwoUInt>("nvmlDeviceGetPowerManagementLimitConstraints", f => f(d, out min, out max), out var e1) != Nvml.Success) powerProblem = e1;
        else if (Nvml.Call<Nvml.DevUInt>("nvmlDeviceGetPowerManagementDefaultLimit", f => f(d, out def), out var e2) != Nvml.Success) powerProblem = e2;
        else if (Nvml.Call<Nvml.DevUInt>("nvmlDeviceGetPowerManagementLimit", f => f(d, out limit), out var e3) != Nvml.Success) powerProblem = e3;
        else if (min >= max) powerProblem = Txt.GpuPowerFixed;

        var (core, coreProblem) = ReadOffset(d, Nvml.ClockGraphics, "Gpc");
        var (memory, memoryProblem) = ReadOffset(d, Nvml.ClockMem, "Mem");
        if (core is null || memory is null)
        {
            var fallback = Nvapi.ReadOffsets(busId);
            if (core is null && fallback.Core is { } c) { core = c with { Nvapi = true }; coreProblem = null; }
            if (memory is null && fallback.Memory is { } m) { memory = m with { Nvapi = true }; memoryProblem = null; }
            coreProblem ??= fallback.Problem;
            memoryProblem ??= fallback.Problem;
        }
        GpuThermalLimit? thermal = null;
        string? thermalProblem = null;
        try { thermal = Nvapi.ReadThermal(busId); }
        catch (Exception ex) { thermalProblem = ex.Message; }
        bool? slowdown = null;
        string? slowdownProblem = null;
        try { slowdown = Nvapi.ReadThermalSlowdown(busId); }
        catch (Exception ex) { slowdownProblem = ex.Message; }
        GpuVfCurve? curve = null;
        string? curveProblem = null;
        try { curve = Nvapi.ReadCurve(busId); }
        catch (Exception ex) { curveProblem = ex.Message; }
        int? lockVoltage = null;
        string? lockProblem = null;
        try { lockVoltage = Nvapi.ReadLock(busId); }
        catch (Exception ex) { lockProblem = ex.Message; }
        var clockLockProblem = !Nvml.Has("nvmlDeviceSetGpuLockedClocks") || !Nvml.Has("nvmlDeviceResetGpuLockedClocks")
            ? Txt.GpuNotExported("nvmlDeviceSetGpuLockedClocks") : null;

        // Fans: manual control only with both write functions and the range allowed by the driver.
        string? fanProblem = null;
        uint fanCount = 0, fanMin = 0, fanMax = 0;
        if (!Nvml.Has("nvmlDeviceSetFanSpeed_v2") || !Nvml.Has("nvmlDeviceSetDefaultFanSpeed_v2")) fanProblem = Txt.GpuNotExported("nvmlDeviceSetFanSpeed_v2");
        else if (Nvml.Call<Nvml.DevUInt>("nvmlDeviceGetNumFans", f => f(d, out fanCount), out var e4) != Nvml.Success) fanProblem = e4;
        else if (fanCount == 0) fanProblem = Txt.GpuNoFans;
        else if (Nvml.Call<Nvml.DevTwoUInt>("nvmlDeviceGetMinMaxFanSpeed", f => f(d, out fanMin, out fanMax), out var e5) != Nvml.Success) fanProblem = e5;
        else if (fanMin > fanMax || fanMax > 100) fanProblem = Txt.GpuFanRangeInvalid(fanMin, fanMax);

        bool? fanAuto = null;
        if (fanCount > 0)
        {
            var policies = Enumerable.Range(0, (int)fanCount).Select(i => U(d, "nvmlDeviceGetFanControlPolicy_v2", (uint)i)).ToList();
            if (policies.All(p => p is not null)) fanAuto = policies.All(p => p == Nvml.FanPolicyAuto);
        }
        return new GpuState
        {
            BusId = busId,
            PowerLimitMw = powerProblem is null ? limit : null,
            PowerMinMw = powerProblem is null ? min : null,
            PowerMaxMw = powerProblem is null ? max : null,
            PowerDefaultMw = powerProblem is null ? def : null,
            PowerProblem = powerProblem,
            Thermal = thermal,
            ThermalProblem = thermalProblem,
            ThermalSlowdown = slowdown,
            ThermalSlowdownProblem = slowdownProblem,
            Core = core,
            CoreProblem = coreProblem,
            Memory = memory,
            MemoryProblem = memoryProblem,
            Curve = curve,
            CurveProblem = curveProblem,
            LockVoltageUv = lockVoltage,
            LockProblem = lockProblem,
            ClockLockMhz = clockLockProblem is null ? ClockLockRecord.Read(busId) : null,
            ClockLockProblem = clockLockProblem,
            MinClockMhz = clockLockProblem is null ? ClockLockRecord.ReadMin(busId) : null,
            MaxClockMhz = U(d, "nvmlDeviceGetMaxClockInfo", Nvml.ClockGraphics),
            FanCount = (int)fanCount,
            FanMin = fanMin,
            FanMax = fanMax,
            FanAuto = fanAuto,
            FanTarget = fanCount > 0 ? U(d, "nvmlDeviceGetTargetFanSpeed", 0) ?? U(d, "nvmlDeviceGetFanSpeed_v2", 0) : null,
            FanProblem = fanProblem,
        };
    }

    /// <summary>Prefers nvmlDevice[GS]etClockOffsets (P0); otherwise the old VF functions with the driver's range.</summary>
    static (GpuRange?, string?) ReadOffset(IntPtr d, uint clock, string legacy)
    {
        if (Nvml.Has("nvmlDeviceSetClockOffsets") && Nvml.Fn<Nvml.DevOffset>("nvmlDeviceGetClockOffsets") is { } get)
        {
            var info = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = clock, PState = Nvml.PState0 };
            var code = get(d, ref info);
            if (code == Nvml.Success) return info.Min < info.Max ? (new GpuRange(info.Offset, info.Min, info.Max, true), null) : (null, Txt.GpuOffsetFixed);
            return (null, code == Nvml.NotSupported ? Txt.GpuOffsetNotSupported : $"nvmlDeviceGetClockOffsets: {Nvml.Describe(code)}");
        }
        string set = $"nvmlDeviceSet{legacy}ClkVfOffset", read = $"nvmlDeviceGet{legacy}ClkVfOffset", range = $"nvmlDeviceGet{legacy}ClkMinMaxVfOffset";
        if (!Nvml.Has(set)) return (null, Txt.GpuNoOffsetApi);
        int current = 0, min = 0, max = 0;
        var rc = Nvml.Call<Nvml.DevInt>(read, f => f(d, out current), out var e1);
        if (rc == Nvml.NotSupported) return (null, Txt.GpuOffsetNotSupported);
        if (rc != Nvml.Success) return (null, e1);
        if (Nvml.Call<Nvml.DevTwoInt>(range, f => f(d, out min, out max), out var e2) != Nvml.Success) return (null, e2);
        return min < max ? (new GpuRange(current, min, max, false), null) : (null, Txt.GpuOffsetFixed);
    }

    /// <summary>Null if the request fits the limits the device reports; otherwise the reason. No I/O (testable).</summary>
    public static string? Problem(GpuTuning t, GpuState s)
    {
        if (!IsBusId(t.PciBusId) || !t.PciBusId.Equals(s.BusId, StringComparison.OrdinalIgnoreCase)) return Txt.GpuInvalidDevice(t.PciBusId);
        if (t.PowerLimitMw is null && t.TemperatureLimitC is null && t.ThermalSlowdown is null && t.CoreOffsetMhz is null && t.MemOffsetMhz is null
            && t.CurveChanges is null && t.LockVoltageUv is null && t.ClockLockMhz is null && t.MinClockMhz is null && t.FanPercent is null && !t.FanAuto) return Txt.GpuNothingToApply;
        if (t.PowerLimitMw is { } p)
        {
            if (s.PowerProblem is { } e) return e;
            if (p < s.PowerMinMw || p > s.PowerMaxMw) return Txt.GpuPowerOutOfRange(s.PowerMinMw!.Value, s.PowerMaxMw!.Value);
        }
        if (OffsetProblem(t.CoreOffsetMhz, s.Core, s.CoreProblem) is { } core) return core;
        if (OffsetProblem(t.MemOffsetMhz, s.Memory, s.MemoryProblem) is { } mem) return mem;
        if (t.TemperatureLimitC is { } temp)
        {
            if (s.Thermal is not { } thermal) return s.ThermalProblem ?? Txt.GpuThermalUnavailable;
            if (temp < thermal.Minimum || temp > thermal.Maximum) return Txt.GpuThermalOutOfRange(thermal.Minimum, thermal.Maximum);
        }
        if (t.ThermalSlowdown is not null && s.ThermalSlowdown is null) return s.ThermalSlowdownProblem ?? Txt.GpuThermalSlowdownUnavailable;
        if (t.CurveChanges is { } changes)
        {
            if (s.Curve is not { } curve) return s.CurveProblem ?? Txt.GpuCurveUnavailable;
            if (changes.Count == 0 || changes.Select(c => c.Index).Distinct().Count() != changes.Count) return Txt.GpuCurveInvalid;
            foreach (var change in changes)
                if (change.Index < 1 || change.Index >= curve.Points.Count
                    || curve.Points[change.Index].VoltageUv != change.VoltageUv
                    || change.DeltaKhz < curve.MinimumDeltaKhz || change.DeltaKhz > curve.MaximumDeltaKhz)
                    return Txt.GpuCurveInvalid;
        }
        if (t.LockVoltageUv is { } voltage)
        {
            if (s.LockVoltageUv is null) return s.LockProblem ?? Txt.GpuLockUnavailable;
            if (voltage != 0 && (s.Curve is null || !s.Curve.Points.Any(p => p.VoltageUv == voltage))) return Txt.GpuCurveInvalid;
        }
        if (t.ClockLockMhz is { } clock)
        {
            if (s.ClockLockMhz is null) return s.ClockLockProblem ?? Txt.GpuLockUnavailable;
            if (clock != 0 && clock is < 100 or > 5000) return Txt.GpuClockLockOutOfRange;
        }
        if (t.MinClockMhz is { } floor)
        {
            if (s.MinClockMhz is null) return s.ClockLockProblem ?? Txt.GpuLockUnavailable;
            if (s.MaxClockMhz is not { } top) return Txt.GpuMinClockUnavailable;
            if (floor != 0 && (floor < 100 || floor > top)) return Txt.GpuMinClockOutOfRange((int)top);
            if (floor != 0 && t.ClockLockMhz is > 0) return Txt.GpuMinClockConflict;
        }
        if (t.FanAuto && t.FanPercent is not null) return Txt.GpuFanMixed;
        if ((t.FanAuto || t.FanPercent is not null) && s.FanProblem is { } fan) return fan;
        if (t.FanPercent is { } pct && (pct < s.FanMin || pct > s.FanMax)) return Txt.GpuFanOutOfRange(s.FanMin, s.FanMax);
        return null;
    }

    static string? OffsetProblem(int? value, GpuRange? range, string? problem) => value is not { } v ? null
        : range is null ? problem ?? Txt.GpuNoOffsetApi
        : v < range.Min || v > range.Max ? Txt.GpuOffsetOutOfRange(range.Min, range.Max) : null;

    /// <summary>What was requested and does not show up in the re-read (empty = everything confirmed).</summary>
    public static List<string> Unconfirmed(GpuTuning t, GpuState s)
    {
        var list = new List<string>();
        if (t.PowerLimitMw is { } p && s.PowerLimitMw != p) list.Add(Txt.GpuPowerLimit);
        if (t.TemperatureLimitC is { } temp && s.Thermal?.Current != temp) list.Add(Txt.GpuThermalLimit);
        if (t.ThermalSlowdown is { } on && s.ThermalSlowdown != on) list.Add(Txt.GpuThermalSlowdown);
        if (t.CoreOffsetMhz is { } c && s.Core?.Current != c) list.Add(Txt.GpuCoreOffset);
        if (t.MemOffsetMhz is { } m && s.Memory?.Current != m) list.Add(Txt.GpuMemOffset);
        if (t.CurveChanges is { } changes && (s.Curve is null || changes.Any(c => c.Index >= s.Curve.Points.Count
            || s.Curve.Points[c.Index].VoltageUv != c.VoltageUv || s.Curve.Points[c.Index].DeltaKhz != c.DeltaKhz)))
            list.Add(Txt.GpuVfCurve);
        if (t.LockVoltageUv is { } voltage && s.LockVoltageUv != voltage) list.Add(Txt.GpuVfLock);
        if (t.ClockLockMhz is { } clock && s.ClockLockMhz != clock) list.Add(Txt.GpuClockLock);
        if (t.MinClockMhz is { } floor && s.MinClockMhz != floor) list.Add(Txt.GpuMinClock);
        if (t.FanAuto && s.FanAuto == false) list.Add(Txt.GpuFan);
        if (t.FanPercent is { } f && (s.FanAuto == true || s.FanTarget is { } target && target != f)) list.Add(Txt.GpuFan);
        return list;
    }

    // ---------- writing (administrator only) ----------

    /// <summary>Unelevated side: writes in its own process as administrator; otherwise through the elevated request.</summary>
    public static async Task RunAsync(GpuJob job, CancellationToken ct = default)
    {
        if (Elevation.IsAdmin) await Task.Run(() => { Validate(job); Execute(job); }, ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { Gpu = job }, ct).ConfigureAwait(false);
    }

    /// <summary>Elevated process validation: reopens the device and checks each value against its limits.</summary>
    internal static void Validate(GpuJob job)
    {
        if (ShapeProblem(job) is { } shape) throw new InvalidOperationException(shape);
        if (job.SignIn == true && !Elevation.InProtectedFolder) throw new InvalidOperationException(Txt.GpuSignInNeedsProgramFiles);
        foreach (var t in new[] { job.Tuning, job.Saved })
            if (t is not null && Problem(t, ReadState(t.PciBusId)) is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>Accepted forms: apply; save for logon (applying or not); turn off without values. No I/O (testable).</summary>
    internal static string? ShapeProblem(GpuJob job)
    {
        var ok = job.SignIn switch
        {
            true => job.Saved is not null,
            false => job.Tuning is null && job.Saved is null,
            null => job.Tuning is not null && job.Saved is null,
        };
        if (!ok) return "Invalid GPU request.";
        return job.Tuning is { } a && job.Saved is { } b && !a.PciBusId.Equals(b.PciBusId, StringComparison.OrdinalIgnoreCase) ? "Mixed GPU request." : null;
    }

    internal static void Execute(GpuJob job)
    {
        if (job.Tuning is { } t) Write(t);
        if (job.SignIn == true) EnableSignIn(job.Saved!);
        if (job.SignIn == false) DisableSignIn();
    }

    /// <summary>Revalidates against the device and writes each value; continues after a failure and throws with all of them at the end.</summary>
    internal static void Write(GpuTuning t)
    {
        lock (Gate)
        {
            var d = Open(t.PciBusId);
            var s = ReadState(d, t.PciBusId);
            if (Problem(t, s) is { } problem) throw new InvalidOperationException(problem);
            var errors = new List<string>();
            void Run<T>(string fn, Func<T, int> call, string what) where T : Delegate
            {
                if (Nvml.Call(fn, call, out var error) == Nvml.Success) Log.Info($"GPU {t.PciBusId}: {what}");
                else { Log.Warn($"GPU {t.PciBusId}: {what} failed: {error}"); errors.Add(error!); }
            }
            if (t.PowerLimitMw is { } p) Run<Nvml.DevSetUInt>("nvmlDeviceSetPowerManagementLimit", f => f(d, p), $"power limit = {p} mW");
            if (t.TemperatureLimitC is { } temp) ApplyNvapi(() => Nvapi.SetThermal(t.PciBusId, temp), $"thermal limit = {temp} °C");
            if (t.ThermalSlowdown is { } slowdown) ApplyNvapi(() => Nvapi.SetThermalSlowdown(t.PciBusId, slowdown), $"thermal slowdown = {(slowdown ? "on" : "off")}");
            if (t.CoreOffsetMhz is { } c) SetOffset(Nvml.ClockGraphics, "Gpc", c, s.Core!.ClockOffsetsApi);
            if (t.MemOffsetMhz is { } m) SetOffset(Nvml.ClockMem, "Mem", m, s.Memory!.ClockOffsetsApi);
            if (t.CurveChanges is { } curve) ApplyNvapi(() => Nvapi.SetCurve(t.PciBusId, curve), $"V/F curve = {curve.Count} points");
            if (t.LockVoltageUv is { } voltage) ApplyNvapi(() => Nvapi.SetLockVoltage(t.PciBusId, voltage), $"V/F lock = {voltage} uV");
            if (t.ClockLockMhz is not null || t.MinClockMhz is not null)
            {
                // One NVML lock: a clock lock (low = high) wins, else the minimum clock (up to the card's maximum), else off.
                var clock = t.ClockLockMhz ?? 0;
                var floor = clock > 0 ? 0 : t.MinClockMhz ?? 0;
                var what = clock > 0 ? $"clock lock = {clock} MHz" : floor > 0 ? $"minimum clock = {floor} MHz" : "clock lock = off";
                var low = clock > 0 ? clock : floor;
                var high = clock > 0 ? clock : (int)(s.MaxClockMhz ?? 0);
                var ok = low == 0
                    ? Nvml.Call<Nvml.Dev>("nvmlDeviceResetGpuLockedClocks", f => f(d), out var error) == Nvml.Success
                    : Nvml.Call<Nvml.DevSetTwoUInt>("nvmlDeviceSetGpuLockedClocks", f => f(d, (uint)low, (uint)high), out error) == Nvml.Success;
                if (ok)
                {
                    ClockLockRecord.Write(t.PciBusId, clock, floor);
                    Log.Info($"GPU {t.PciBusId}: {what}");
                }
                else { Log.Warn($"GPU {t.PciBusId}: {what} failed: {error}"); errors.Add(error!); }
            }
            for (uint i = 0; i < s.FanCount; i++)
            {
                var fan = i;
                if (t.FanAuto) Run<Nvml.DevSetUInt>("nvmlDeviceSetDefaultFanSpeed_v2", f => f(d, fan), $"fan {fan} = automatic");
                else if (t.FanPercent is { } pct) Run<Nvml.DevSetTwoUInt>("nvmlDeviceSetFanSpeed_v2", f => f(d, fan, pct), $"fan {fan} = {pct}%");
            }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));

            void ApplyNvapi(Action action, string what)
            {
                try { action(); Log.Info($"GPU {t.PciBusId}: {what}"); }
                catch (Exception ex) { Log.Warn($"GPU {t.PciBusId}: {what} failed: {ex.Message}"); errors.Add(ex.Message); }
            }

            void SetOffset(uint clock, string legacy, int mhz, bool clockOffsetsApi)
            {
                var what = $"{(clock == Nvml.ClockMem ? "memory" : "core")} offset = {mhz} MHz";
                var range = clock == Nvml.ClockMem ? s.Memory! : s.Core!;
                if (range.Nvapi) { ApplyNvapi(() => Nvapi.SetOffset(t.PciBusId, clock == Nvml.ClockMem ? 4 : 0, mhz), what); return; }
                if (!clockOffsetsApi) { Run<Nvml.DevSetInt>($"nvmlDeviceSet{legacy}ClkVfOffset", f => f(d, mhz), what); return; }
                var info = new Nvml.ClockOffset { Version = Nvml.ClockOffsetV1, Type = clock, PState = Nvml.PState0, Offset = mhz };
                Run<Nvml.DevOffset>("nvmlDeviceSetClockOffsets", f => f(d, ref info), what);
            }
        }
    }

    /// <summary>KOS Engine (already elevated): revalidates against the card, writes, and returns what the re-read didn't confirm.</summary>
    public static List<string> Apply(GpuTuning t)
    {
        Write(t);
        return Unconfirmed(t, ReadState(t.PciBusId));
    }

    // ---------- apply at logon ----------

    /// <summary>The task exists (read-only query through schtasks).</summary>
    public static bool SignInEnabled()
    {
        try { return ProcessRunner.Run(Schtasks, $"/Query /TN {ProcessRunner.Quote(TaskName)}").ExitCode == 0; }
        catch (Exception ex) { Log.Warn($"GPU: checking the sign-in task failed: {ex.Message}"); return false; }
    }

    public static GpuTuning? SavedTuning() => File.Exists(SignInFile) ? JsonFile.Load(SignInFile, KosJson.Default.GpuTuning) : null;

    static void EnableSignIn(GpuTuning t)
    {
        PrepareDirectory();
        // A .tmp left before the protection could be a hard link to another file: delete instead of writing into it.
        File.Delete(SignInFile + ".tmp");
        if (FolderProblem() is { } problem) throw new InvalidOperationException(problem);
        JsonFile.Save(SignInFile, t, KosJson.Default.GpuTuning);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The toolbox executable path is unknown.");
        var args = $"/Create /TN {ProcessRunner.Quote(TaskName)} /SC ONLOGON /RL HIGHEST /TR {ProcessRunner.Quote($"\"{exe}\" --gpu-apply")} /F";
        var r = ProcessRunner.Run(Schtasks, args);
        if (r.ExitCode != 0) throw new InvalidOperationException($"schtasks /Create failed ({r.ExitCode}): {r.Output.Trim()}");
        Log.Info($"GPU: sign-in task created; saved {t}");
    }

    static void DisableSignIn()
    {
        var r = ProcessRunner.Run(Schtasks, $"/Delete /TN {ProcessRunner.Quote(TaskName)} /F");
        // Without the task the file is not used; removal only really fails if the task still exists.
        if (r.ExitCode != 0 && SignInEnabled()) throw new InvalidOperationException($"schtasks /Delete failed ({r.ExitCode}): {r.Output.Trim()}");
        // Without the task the file is not applied: in a suspicious folder it stays, instead of deleting through a redirected path.
        if (FolderProblem() is { } problem) Log.Warn($"GPU: {SignInFile} was left in place ({problem})");
        else if (File.Exists(SignInFile)) File.Delete(SignInFile);
        Log.Info("GPU: sign-in task removed.");
    }

    static void PrepareDirectory() => PrepareDirectory(SignInDirectory);

    /// <summary>
    /// Folder in TheKOS writable only by administrators (TheKOS\Toolbox, TheKOS\Governor): what lives in it is applied with
    /// privileges. The folder is born protected and the ACL of an existing folder is never rewritten (between the check and the
    /// write the path could become a link); an existing folder not owned by SYSTEM or Administrators is refused.
    /// </summary>
    internal static void PrepareDirectory(string dir)
    {
        Directory.CreateDirectory(AppPaths.TheKosRoot);
        if (FolderProblem(AppPaths.TheKosRoot, dir) is { } problem) throw new InvalidOperationException(problem);
        if (Directory.Exists(dir)) return;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Created already with the protected descriptor; if another process creates the folder first, nothing changes and the re-read refuses.
        new DirectoryInfo(dir).Create(acl);
        if (FolderProblem(AppPaths.TheKosRoot, dir) is { } after) throw new InvalidOperationException(after);
    }

    static string? FolderProblem() => FolderProblem(AppPaths.TheKosRoot, SignInDirectory);

    /// <summary>Null if the root and folder are not links and the folder (when it exists) belongs to SYSTEM or Administrators.</summary>
    internal static string? FolderProblem(string root, string dir) =>
        (new[] { root, dir }.FirstOrDefault(p => Directory.Exists(p) && new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint)) is { } link
            ? Txt.GpuFolderIsLink(link) : null)
        ?? (Directory.Exists(dir) ? ToolDiscovery.OwnerProblem(dir) : null);

    /// <summary>"--gpu-apply" mode (logon task): loads the protected file, revalidates against the device and applies.</summary>
    public static int RunAtSignIn()
    {
        try
        {
            if (!File.Exists(SignInFile)) { Log.Warn($"GPU sign-in: {SignInFile} doesn't exist; nothing applied."); return 0; }
            if ((FolderProblem() ?? ToolDiscovery.OwnerProblem(SignInFile)) is { } problem)
            {
                Log.Error($"GPU sign-in: refused ({problem})");
                return ElevatedJob.ExitRefused;
            }
            var t = JsonFile.Load(SignInFile, KosJson.Default.GpuTuning);
            Write(t);
            var unconfirmed = Unconfirmed(t, ReadState(t.PciBusId));
            if (unconfirmed.Count > 0) { Log.Warn($"GPU sign-in: applied but not confirmed: {string.Join(", ", unconfirmed)}"); return ElevatedJob.ExitPartial; }
            Log.Info($"GPU sign-in: applied and verified {t}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("GPU sign-in: applying the saved tuning failed", ex);
            return ElevatedJob.ExitFailed;
        }
    }
}
