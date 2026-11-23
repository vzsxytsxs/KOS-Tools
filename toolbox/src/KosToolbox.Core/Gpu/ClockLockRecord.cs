using System.Text.Json;

namespace KosToolbox.Core.Gpu;

/// <summary>
/// The clock the toolbox locked, or the minimum clock it set (one NVML lock either way; NVML has no function to read it back). Valid only for this
/// Windows boot, because a restart releases the lock; kept in the toolbox folder that only administrators can write.
/// shortcut: a driver reset (TDR) also releases the lock without updating this record; replace with a driver read if NVML/NVAPI start exposing the lock.
/// </summary>
internal static class ClockLockRecord
{
    internal sealed record Entry(string BusId, int Mhz, DateTime BootUtc, int MinMhz = 0);

    static string FilePath => Path.Combine(GpuTuner.SignInDirectory, "gpu-clock-lock.json");
    static DateTime BootUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static int Read(string busId) => Load(busId)?.Mhz ?? 0;

    /// <summary>The minimum clock set in this boot (0 = none).</summary>
    public static int ReadMin(string busId) => Load(busId)?.MinMhz ?? 0;

    static Entry? Load(string busId)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            // Users can create files in the toolbox folder on some installs: only a record written by an administrator counts.
            if ((GpuTuner.FolderProblem(Persistence.AppPaths.TheKosRoot, GpuTuner.SignInDirectory) ?? Tools.ToolDiscovery.OwnerProblem(FilePath)) is not null) return null;
            var e = JsonSerializer.Deserialize(File.ReadAllText(FilePath), Persistence.KosJson.Default.Entry);
            return e is not null && e.BusId.Equals(busId, StringComparison.OrdinalIgnoreCase)
                   && (e.BootUtc - BootUtc).Duration() < TimeSpan.FromMinutes(2) ? e : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Only in the elevated process, after NVML accepted the lock (or the reset).</summary>
    public static void Write(string busId, int mhz, int minMhz = 0)
    {
        GpuTuner.PrepareDirectory(GpuTuner.SignInDirectory);
        if (mhz == 0 && minMhz == 0) File.Delete(FilePath);
        else File.WriteAllText(FilePath, JsonSerializer.Serialize(new Entry(busId, mhz, BootUtc, minMhz), Persistence.KosJson.Default.Entry));
    }
}
