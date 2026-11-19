using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Peripherals;

/// <summary>
/// Mouse settings the KOS Governor engine writes while gamemode is on, chosen on the toolbox's Peripherals page. Volatile settings only
/// (live DPI, polling rate): nothing goes to onboard flash. gamemode-peripheral.json (toolbox → engine) sits next to gamemode-gpu.json in
/// the admin-only governor folder; gamemode-peripheral-state.json (engine only) keeps what each device had, to put it back.
/// It also carries <see cref="DockBattery"/>, the one peripheral setting the engine applies outside gamemode.
/// </summary>
public sealed class GamemodePeripheral
{
    public List<GamemodePeripheralEntry> Devices { get; set; } = new();
    /// <summary>Set by the engine: applying started and never finished (the PC may have crashed). Skipped until chosen again.</summary>
    public bool Suspended { get; set; }
    /// <summary>Not gamemode: the engine colors a Razer charging dock by the mouse battery (live only), all the time.</summary>
    public bool DockBattery { get; set; }
    /// <summary>Devices (VID &lt;&lt; 16 | PID) whose battery gets no tray icon from KOS Governor; every other device with a battery gets one.</summary>
    public List<uint> TrayHidden { get; set; } = new();

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

/// <summary>The dock color for a battery level: red at 0 % through yellow to green at 100 % (0xRRGGBB).</summary>
public static class DockBatteryColor
{
    public static int For(int percent)
    {
        var hue = Math.Clamp(percent, 0, 100) * 120 / 100; // 0 red, 60 yellow, 120 green
        var (r, g) = hue <= 60 ? (255, 255 * hue / 60) : (255 * (120 - hue) / 60, 255);
        return r << 16 | g << 8;
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
    /// <summary>The live brightness in percent: 0 turns the light off in gamemode; in the state, what the light had before.</summary>
    public int? Brightness { get; set; }
    /// <summary>A Razer charging dock only: the mouse battery on the ring during gamemode (true) or not (false), instead of
    /// <see cref="GamemodePeripheral.DockBattery"/>; null = as outside gamemode. Never in the state (nothing to put back).</summary>
    public bool? DockBattery { get; set; }
    /// <summary>The light in gamemode: an effect (<see cref="Color"/> for static and breathing); in the state, a device's live effect before.</summary>
    public LightingEffect? Effect { get; set; }
    public int? Color { get; set; }
    /// <summary>Keyboards with magnetic switches: rapid trigger on (both steps <see cref="RapidTriggerMm"/>) or off on every key, each key's actuation kept.</summary>
    public bool? RapidTrigger { get; set; }
    public double? RapidTriggerMm { get; set; }
    /// <summary>A DualShock 4's light bar in gamemode (0xRRGGBB, 0 = off); in the state, the color put back.</summary>
    public int? LightBar { get; set; }
    /// <summary>The light bar put back when gamemode turns off: the controller can't report it, so the toolbox keeps the color it last set.</summary>
    public int? LightBarAfter { get; set; }
    /// <summary>State only, ROYUAN keyboards (every write stored): the lighting and the switch columns from before gamemode, written back as they were.</summary>
    public Royuan.RoyuanLight? KeyboardLight { get; set; }
    public Royuan.RoyuanColumns? KeyboardSwitches { get; set; }
}

/// <summary>What the devices had before gamemode (same shape: the values to write back).</summary>
public sealed class GamemodePeripheralState
{
    public List<GamemodePeripheralEntry> Before { get; set; } = new();
    /// <summary>Applying the "on" values started; cleared when it returns.</summary>
    public bool Applying { get; set; }
}
