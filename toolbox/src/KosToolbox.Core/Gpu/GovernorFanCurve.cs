using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// Fan curve run by the KOS Governor engine (which lives in the tray): the toolbox only configures. The two
/// programs talk through two files in the governor folder, writable only by administrators (the engine writes the
/// fan as administrator): fan-curve.json (toolbox → engine) and fan-curve-state.json (engine → toolbox).
/// </summary>
public sealed class GovernorFanCurve
{
    public bool Enabled { get; set; }
    public string? BusId { get; set; }
    public List<FanPoint> Points { get; set; } = new();
    public int Hysteresis { get; set; } = 3;

    public static string Directory => Path.Combine(AppPaths.TheKosRoot, "Governor");
    public static string FilePath => Path.Combine(Directory, "fan-curve.json");
    public static string StatePath => Path.Combine(Directory, "fan-curve-state.json");
    const string EngineMutex = @"Local\KosGovernor.Engine", ReloadEvent = @"Local\KosGovernor.Reload";

    /// <summary>The governor engine is running in this session.</summary>
    public static bool EngineRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(EngineMutex, out var m)) return false;
            m.Dispose();
            return true;
        }
    }

    /// <summary>Null if the folder and file are trustworthy (not links and owned by SYSTEM/Administrators).</summary>
    public static string? Problem(string file) =>
        GpuTuner.FolderProblem(AppPaths.TheKosRoot, Directory) ?? (File.Exists(file) ? ToolDiscovery.OwnerProblem(file) : null);

    /// <summary>The saved curve; null with no file or with the folder/file refused.</summary>
    public static GovernorFanCurve? Load() => File.Exists(FilePath) && Problem(FilePath) is null ? JsonFile.Load(FilePath, KosJson.Default.GovernorFanCurve) : null;

    /// <summary>Writes (as administrator) and wakes the engine, which applies it immediately.</summary>
    public void Save()
    {
        GpuTuner.PrepareDirectory(Directory);
        File.Delete(FilePath + ".tmp"); // an old .tmp could be a hard link to another file
        JsonFile.Save(FilePath, this, KosJson.Default.GovernorFanCurve);
        WakeEngine();
    }

    /// <summary>The engine rereads its files now instead of at its next interval.</summary>
    internal static void WakeEngine()
    {
        if (EventWaitHandle.TryOpenExisting(ReloadEvent, out var wake)) using (wake) wake.Set();
    }

    public sealed class State
    {
        public bool Running { get; set; }
        public string? BusId { get; set; }
        public string? Error { get; set; }
    }

    public static State? ReadState()
    {
        try { return File.Exists(StatePath) ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(StatePath), KosJson.Default.State) : null; }
        catch (Exception) { return null; } // the engine may be writing right now; the next read picks it up
    }

    /// <summary>Only the engine writes (already elevated, folder already prepared by it or by the toolbox).</summary>
    public static void WriteState(State s)
    {
        GpuTuner.PrepareDirectory(Directory);
        JsonFile.Save(StatePath, s, KosJson.Default.State);
    }
}
