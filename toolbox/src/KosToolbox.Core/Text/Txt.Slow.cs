using KosToolbox.Core.SystemInfo;

namespace KosToolbox.Core.Text;

/// <summary>Texts for Diagnose › Slow.</summary>
public static partial class Txt
{
    public const string PageSlow = "What's slow",
        PageSlowSubtitle = "Why the PC feels slow right now, from what Windows already records. Nothing is changed.";
    public const string SlowVerdictNothing = "Nothing stands out right now: no window is frozen, the disks answer quickly and there is free memory.",
        SlowNow = "Now", SlowSampling = "Sampling for 3 seconds…",
        SlowHungNone = "No window is marked as not responding.",
        SlowDisksNoCounters = "The disk counters (PhysicalDisk) aren't available, so disk speed can't be read.",
        SlowRecentHangs = "Programs that stopped responding (30 days)",
        SlowHangsNone = "No program stopped responding in the last 30 days (Application log, Application Hang).",
        SlowSinceBoot = "Since boot",
        SlowStartupNone = "None of the sign-in programs (Run keys and Startup folders) is running in this session, or they started more than 15 minutes after boot.",
        SlowStartupNote = "When each sign-in program started, counted from boot: not how much it slowed the boot. A program that starts late was waiting for something, or for the programs before it.",
        SlowBootLogEmpty = "Windows' own boot-timing log is empty here because the diagnostic services are off, so how long each program delayed the boot isn't known.",
        SlowMore = "Drivers holding the processor (DPC/ISR) are measured on Latency; crashes and driver resets are on Stability.",
        SlowOpenLatency = "Open Latency", SlowOpenStability = "Open Stability",
        SlowFine = "fine", SlowSlow = "slow", SlowVerySlow = "very slow", SlowIdle = "idle";

    public static string SlowVerdictHung(IEnumerable<string> programs) => $"Not responding now: {string.Join(", ", programs)}.";
    public static string SlowVerdictDisk(string disk, double ms) => $"The disk answers slowly: {ms:N0} ms per request ({disk}).";
    public static string SlowVerdictMemory(double commitPercent, double availableMb) =>
        $"Memory is nearly full: {commitPercent:N0} % committed, {availableMb:N0} MB free, so Windows moves memory to the page file.";
    public static string SlowVerdictHangs(string program, int count) => $"{program} stopped responding {count} times in the last 30 days.";

    public static string SlowDiskName(string number, string? letters) => letters is null ? $"Disk {number}" : $"Disk {number} ({letters})";
    public static string SlowDiskSpeed(DiskSpeed s) => s switch { DiskSpeed.VerySlow => SlowVerySlow, DiskSpeed.Slow => SlowSlow, DiskSpeed.Idle => SlowIdle, _ => SlowFine };
    public static string SlowDiskDetail(DiskSample d) =>
        $"{d.LatencyMs:N1} ms per request · queue {d.Queue:N1} · {d.BusyPercent:N0} % busy · {d.TransfersPerSecond:N0} requests/s";
    public static string SlowMemory(MemorySample m) =>
        $"Memory: {m.CommitPercent:N0} % committed (of RAM plus page file) · {m.AvailableMb:N0} MB of {m.TotalMb:N0} MB free";
    public static string SlowHangCount(int count, string? path) => $"{count} time{(count == 1 ? "" : "s")}{(path is null ? "" : $" · {path}")}";
    public static string SlowBoot(DateTime boot, TimeSpan uptime, bool? fastStartup) =>
        $"Last boot {boot:g} · up for {(uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} d {uptime.Hours} h" : $"{(int)uptime.TotalHours} h {uptime.Minutes} min")}" +
        $" · Fast Startup {fastStartup switch { true => "on (a shutdown doesn't restart Windows; boot is the last restart)", false => "off", null => "not set (on by default)" }}";
    public static string SlowAfterBoot(double seconds) => $"+{seconds:N0} s";
    public static string SlowStartupRefused(int count) => $"{count} process{(count == 1 ? "" : "es")} didn't give their start time (running as administrator or another user).";
}
