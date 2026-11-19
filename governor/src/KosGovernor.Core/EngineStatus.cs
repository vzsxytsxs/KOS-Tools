using System.Globalization;

namespace KosGovernor.Core;

/// <summary>One line of the status window. CPU and memory are null where the line has no work of its own to measure.</summary>
public sealed record StatusLine(string Name, string State, bool Active, double? CpuPercent = null, long? Bytes = null, bool Estimate = true);

/// <summary>What the status window shows: the engine's totals, then one line per task.</summary>
public sealed record StatusView(double CpuPercent, long PrivateBytes, IReadOnlyList<StatusLine> Lines);

/// <summary>What the engine knows about its tasks right now, as plain values (read only while the status window is open).</summary>
public sealed record EngineFacts
{
    public string Profile { get; init; } = "";
    public int Rules { get; init; }
    public bool Gamemode { get; init; }
    /// <summary>The game that turned gamemode on; null = by hand.</summary>
    public string? GamemodeGame { get; init; }
    public bool AutoGamemode { get; init; }
    public bool FanCurveRunning { get; init; }
    /// <summary>A gamemode GPU profile is chosen / is applied now.</summary>
    public bool GpuProfileChosen { get; init; }
    public bool GpuProfileApplied { get; init; }
    public int Batteries { get; init; }
    /// <summary>Devices with a gamemode profile (DPI, polling rate, lighting) / the profile is applied now.</summary>
    public int PeripheralProfiles { get; init; }
    public bool PeripheralProfilesApplied { get; init; }
    /// <summary>The timer resolution held, in 100 ns units; null = none.</summary>
    public uint? TimerHeld { get; init; }
    public bool Overlay { get; init; }
    /// <summary>KOS Firewall is on; its filters in the filter engine; connections they blocked since the engine started; popups on.</summary>
    public bool FirewallOn { get; init; }
    public int FirewallFilters { get; init; }
    public int FirewallBlocked { get; init; }
    public bool FirewallPopups { get; init; }
}

public static class EngineStatus
{
    public static StatusView Build(EngineFacts f, TaskMeter.Sample s)
    {
        // The estimates count memory a task took and gave back: together they never get more than the process holds above its base
        long room = Math.Max(0, s.PrivateBytes - s.BaseBytes), claimed = s.Tasks.Sum(t => t.Bytes);
        var scale = claimed > room && claimed > 0 ? (double)room / claimed : 1;
        TaskMeter.Usage U(EngineTask t) => s.Tasks[(int)t];
        StatusLine Line(string name, string state, bool active, EngineTask t) => new(name, state, active, U(t).CpuPercent, (long)(U(t).Bytes * scale));
        var lines = new List<StatusLine>
        {
            Line("Process rules", $"{f.Rules} rule{(f.Rules == 1 ? "" : "s")} · {f.Profile}", true, EngineTask.Rules),
            new("Gamemode", !f.Gamemode ? f.AutoGamemode ? "Off · turns on when a game starts" : "Off"
                : f.GamemodeGame is { } game ? $"On · {game}" : "On · turned on by hand", f.Gamemode),
            Line("GPU fan curve", f.FanCurveRunning ? "Running" : "Off", f.FanCurveRunning, EngineTask.FanCurve),
            Line("GPU gamemode profile", f.GpuProfileApplied ? "Applied" : f.GpuProfileChosen ? "Waiting for gamemode" : "None chosen",
                f.GpuProfileApplied, EngineTask.GpuProfile),
            Line("Peripherals", Peripherals(f), f.Batteries > 0 || f.PeripheralProfiles > 0, EngineTask.Peripherals),
            new("Timer resolution", f.TimerHeld is { } t ? (t / 10000.0).ToString("0.###", CultureInfo.InvariantCulture) + " ms held" : "Off", f.TimerHeld is not null),
            Line("RTSS overlay", f.Overlay ? "Showing" : "Off", f.Overlay, EngineTask.Rtss),
            Line("KOS Firewall", Firewall(f), f.FirewallOn && f.FirewallFilters > 0, EngineTask.Firewall),
            Line("Tray and this window", "Open", true, EngineTask.Tray),
        };
        // What no task accounts for: the runtime, the configuration in memory, threads waiting.
        var measured = lines.Where(l => l.CpuPercent is not null).ToList();
        lines.Add(new("Everything else", "Runtime and idle threads", true,
            Math.Max(0, s.CpuPercent - measured.Sum(l => l.CpuPercent!.Value)), Math.Max(0, s.PrivateBytes - measured.Sum(l => l.Bytes!.Value))));
        return new(s.CpuPercent, s.PrivateBytes, lines);
    }

    /// <summary>The filtering itself runs in Windows; the engine's share is the popups and the count.</summary>
    static string Firewall(EngineFacts f) => !f.FirewallOn ? "Off"
        : f.FirewallFilters == 0 ? "On, but no filters in place: open the toolbox's Firewall page"
        : $"{f.FirewallFilters} filters · {f.FirewallBlocked} blocked{(f.FirewallPopups ? "" : " · no popups")}";

    static string Peripherals(EngineFacts f)
    {
        var parts = new List<string>();
        if (f.Batteries > 0) parts.Add($"{f.Batteries} batter{(f.Batteries == 1 ? "y" : "ies")}");
        if (f.PeripheralProfiles > 0)
            parts.Add($"gamemode profile {(f.PeripheralProfilesApplied ? "applied" : "waiting")} ({f.PeripheralProfiles} device{(f.PeripheralProfiles == 1 ? "" : "s")})");
        return parts.Count == 0 ? "Nothing to do" : string.Join(" · ", parts);
    }

    /// <summary>Two decimals: an idle task uses hundredths of a percent, and less than that still isn't nothing.</summary>
    public static string Cpu(double percent) => percent <= 0 ? "0 %" : percent < 0.005 ? "< 0.01 %" : percent.ToString("0.00", CultureInfo.InvariantCulture) + " %";

    public static string Memory(long bytes, bool estimate) => (estimate ? "≈ " : "") + (bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB" : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB");
}
