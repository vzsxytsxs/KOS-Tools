using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// GPU profiles KOS Engine writes when gamemode turns on and off, chosen on the toolbox's GPU page.
/// gamemode-gpu.json (toolbox → engine) lives next to fan-curve.json in the admin-only governor folder;
/// gamemode-gpu-state.json (engine only) keeps what the card had before gamemode, to put it back afterwards.
/// </summary>
public sealed class GamemodeGpu
{
    /// <summary>Written when gamemode turns on; null = gamemode doesn't touch the card.</summary>
    public GamemodeGpuProfile? On { get; set; }
    /// <summary>Written when gamemode turns off; null = back to what the card had before gamemode turned on.</summary>
    public GamemodeGpuProfile? Off { get; set; }
    /// <summary>Set by the engine: writing the "on" profile started and never finished (the PC may have crashed). Skipped until chosen again.</summary>
    public bool Suspended { get; set; }

    public static string FilePath => Path.Combine(GovernorFanCurve.Directory, "gamemode-gpu.json");
    public static string StatePath => Path.Combine(GovernorFanCurve.Directory, "gamemode-gpu-state.json");

    /// <summary>The saved choice; null with no file or with the folder/file refused.</summary>
    public static GamemodeGpu? Load() => File.Exists(FilePath) && GovernorFanCurve.Problem(FilePath) is null ? JsonFile.Load(FilePath, KosJson.Default.GamemodeGpu) : null;

    /// <summary>Writes (as administrator) and wakes the engine.</summary>
    public void Save()
    {
        GpuTuner.PrepareDirectory(GovernorFanCurve.Directory);
        File.Delete(FilePath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(FilePath, this, KosJson.Default.GamemodeGpu);
        GovernorFanCurve.WakeEngine();
    }

    /// <summary>
    /// What the engine refuses even though each value fits the card: its own fan curve with thermal slowdown off. If the engine
    /// dies mid-game, the fan stays at the curve's last speed while nothing lowers the clocks on heat.
    /// </summary>
    public static string? Problem(GamemodeGpuProfile p) =>
        p.Curve is { Enabled: true } && p.Tuning.ThermalSlowdown == false ? "a fan curve with thermal slowdown off isn't allowed (a stopped curve would leave the card without either)." : null;

    public static GamemodeGpuState? LoadState() =>
        File.Exists(StatePath) && GovernorFanCurve.Problem(StatePath) is null ? JsonFile.Load(StatePath, KosJson.Default.GamemodeGpuState) : null;

    /// <summary>Engine only (already elevated).</summary>
    public static void SaveState(GamemodeGpuState? state)
    {
        if (state is null) { if (File.Exists(StatePath) && GovernorFanCurve.Problem(StatePath) is null) File.Delete(StatePath); return; }
        GpuTuner.PrepareDirectory(GovernorFanCurve.Directory);
        File.Delete(StatePath + ".tmp");
        JsonFile.Save(StatePath, state, KosJson.Default.GamemodeGpuState);
    }

    /// <summary>
    /// Everything the card has now that a profile can set, as a request that puts it back. <paramref name="fan"/> = false leaves
    /// the fan out (a fan curve is driving it and comes back by itself).
    /// </summary>
    public static GpuTuning Current(GpuState s, bool fan) => new()
    {
        PciBusId = s.BusId,
        PowerLimitMw = s.PowerProblem is null ? s.PowerLimitMw : null,
        TemperatureLimitC = s.Thermal?.Current,
        ThermalSlowdown = s.ThermalSlowdown,
        CoreOffsetMhz = s.Core?.Current,
        MemOffsetMhz = s.Memory?.Current,
        CurveChanges = s.Curve is { Points.Count: > 1 } curve
            ? curve.Points.Skip(1).Select(p => new GpuVfChange(p.Index, p.VoltageUv, Math.Clamp(p.DeltaKhz, curve.MinimumDeltaKhz, curve.MaximumDeltaKhz))).ToList()
            : null,
        LockVoltageUv = s.LockVoltageUv,
        ClockLockMhz = s.ClockLockMhz,
        MinClockMhz = s.MinClockMhz,
        FanAuto = fan && s.FanProblem is null && s.FanAuto == true,
        FanPercent = fan && s.FanProblem is null && s.FanAuto == false ? s.FanTarget : null,
    };
}

public sealed class GamemodeGpuProfile
{
    /// <summary>The toolbox profile it came from (shown on the GPU page).</summary>
    public string Name { get; set; } = "";
    public GpuTuning Tuning { get; set; } = new();
    /// <summary>Fan curve run while this profile is on; null = the fan follows <see cref="Tuning"/> (automatic or fixed).</summary>
    public GovernorFanCurve? Curve { get; set; }
}

/// <summary>What the card had before gamemode's profile was written.</summary>
public sealed class GamemodeGpuState
{
    public GpuTuning? Before { get; set; }
    /// <summary>Writing the "on" profile started; cleared when it returns.</summary>
    public bool Applying { get; set; }
}
