namespace KosToolbox.Core.Persistence;

/// <summary>
/// Written changes that only take effect after a reboot. Each record stores the boot time
/// at which it was made; after a restart it is no longer pending.
/// </summary>
public sealed class PendingRebootStore
{
    public static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Whether a change made at <paramref name="madeAt"/> still waits for a restart. 10 s tolerance for the imprecision of the boot time
    /// calculation (2 minutes kept a change made just before a restart pending, and a restart takes about 25 s). A record dated in the
    /// future was made while the clock was wrong (a bad RTC or NTP step): it would never fall behind a boot, so it doesn't count.
    /// </summary>
    internal static bool IsPending(DateTime madeAt, DateTime boot, DateTime now) => madeAt > boot.AddSeconds(-10) && madeAt < now.AddMinutes(2);

    readonly object _gate = new();

    public IReadOnlyDictionary<string, DateTime> Current()
    {
        lock (_gate)
        {
            var all = JsonFile.Load<Dictionary<string, DateTime>>(AppPaths.Pending);
            var boot = BootTimeUtc;
            var now = DateTime.UtcNow;
            var live = all.Where(kv => IsPending(kv.Value, boot, now)).ToDictionary(kv => kv.Key, kv => kv.Value);
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
