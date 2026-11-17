using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Peripherals;

/// <summary>
/// Mouse settings the KOS Governor engine writes while gamemode is on, chosen on the toolbox's Peripherals page. Volatile settings only
/// (live DPI, polling rate): nothing goes to onboard flash. gamemode-peripheral.json (toolbox → engine) sits next to gamemode-gpu.json in
/// the admin-only governor folder; gamemode-peripheral-state.json (engine only) keeps what each device had, to put it back.
/// </summary>
public sealed class GamemodePeripheral
{
    public List<GamemodePeripheralEntry> Devices { get; set; } = new();
    /// <summary>Set by the engine: applying started and never finished (the PC may have crashed). Skipped until chosen again.</summary>
    public bool Suspended { get; set; }

    public static string FilePath => Path.Combine(GovernorFanCurve.Directory, "gamemode-peripheral.json");
    public static string StatePath => Path.Combine(GovernorFanCurve.Directory, "gamemode-peripheral-state.json");

    public static GamemodePeripheral? Load() =>
        File.Exists(FilePath) && GovernorFanCurve.Problem(FilePath) is null ? JsonFile.Load(FilePath, KosJson.Default.GamemodePeripheral) : null;

    /// <summary>Writes (as administrator) and wakes the engine.</summary>
    public void Save()
    {
        GpuTuner.PrepareDirectory(GovernorFanCurve.Directory);
        File.Delete(FilePath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(FilePath, this, KosJson.Default.GamemodePeripheral);
        GovernorFanCurve.WakeEngine();
    }

    public static GamemodePeripheralState? LoadState() =>
        File.Exists(StatePath) && GovernorFanCurve.Problem(StatePath) is null ? JsonFile.Load(StatePath, KosJson.Default.GamemodePeripheralState) : null;

    /// <summary>Engine only (already elevated).</summary>
    public static void SaveState(GamemodePeripheralState? state)
    {
        if (state is null) { if (File.Exists(StatePath) && GovernorFanCurve.Problem(StatePath) is null) File.Delete(StatePath); return; }
        GpuTuner.PrepareDirectory(GovernorFanCurve.Directory);
        File.Delete(StatePath + ".tmp");
        JsonFile.Save(StatePath, state, KosJson.Default.GamemodePeripheralState);
    }
}

/// <summary>One device by VID and PID, with the values to write; null = left as it is.</summary>
public sealed class GamemodePeripheralEntry
{
    public ushort VendorId { get; set; }
    public ushort ProductId { get; set; }
    public string Name { get; set; } = "";
    public int? Dpi { get; set; }
    public int? PollingHz { get; set; }
}

/// <summary>What the devices had before gamemode (same shape: the values to write back).</summary>
public sealed class GamemodePeripheralState
{
    public List<GamemodePeripheralEntry> Before { get; set; } = new();
    /// <summary>Applying the "on" values started; cleared when it returns.</summary>
    public bool Applying { get; set; }
}
