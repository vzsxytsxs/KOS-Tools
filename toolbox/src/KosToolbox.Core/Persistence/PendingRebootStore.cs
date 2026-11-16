namespace KosToolbox.Core.Persistence;

/// <summary>
/// Written changes that only take effect after a reboot. Each record stores the boot time
/// at which it was made; after a restart it is no longer pending.
/// </summary>
public sealed class PendingRebootStore
{
    public static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    readonly object _gate = new();

    public IReadOnlyDictionary<string, DateTime> Current()
    {
        lock (_gate)
        {
            var all = JsonFile.Load<Dictionary<string, DateTime>>(AppPaths.Pending);
            var boot = BootTimeUtc;
            // 2 min tolerance for the imprecision of the boot time calculation.
            var live = all.Where(kv => kv.Value > boot.AddMinutes(-2)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (live.Count != all.Count) JsonFile.Save(AppPaths.Pending, live);
            return live;
        }
    }

    public void Mark(string tweakId)
    {
        lock (_gate)
        {
            var all = new Dictionary<string, DateTime>(Current()) { [tweakId] = DateTime.UtcNow };
            JsonFile.Save(AppPaths.Pending, all);
        }
    }
}
