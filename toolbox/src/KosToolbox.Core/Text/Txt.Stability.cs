using KosToolbox.Core.SystemInfo;

namespace KosToolbox.Core.Text;

/// <summary>Texts for Hardware › Stability.</summary>
public static partial class Txt
{
    public const string PageStability = "Stability",
        PageStabilitySubtitle = "Blue screens, unexpected restarts, display driver resets and hardware errors, from the System log.";
    public const string StabilityNote = "Check here after changing an overclock, an undervolt or memory timings. The KOS cleanup's \"Clear event logs\" erases this history, and its crash dump cleanup deletes the dumps.",
        StabilityEvents = "History", StabilityNone = "Nothing in the System log: no crashes, driver resets or hardware errors recorded.",
        StabilityDumps = "Crash dumps", StabilityOpenDumps = "Open folder",
        StabilityNoDumps = "No crash dumps.",
        StabilityDumpsOff = "No crash dumps: Windows can't write one while the page file is off or too small (\"No crash dump written\" above).",
        StabilityDumpsNeedAdmin = "Listing the crash dumps needs the toolbox running as administrator.",
        ColTime = "Time", ColEvent = "Event", ColDetails = "Details",
        StabilitySessions = "Gamemode sessions",
        StabilitySessionsNote = "KOS Governor records each gamemode session: CPU, memory and GPU load, clocks and limits, and what interfered (busy programs, the game losing focus, devices and network links dropping, driver resets).",
        StabilityNoSessions = "No sessions yet: turn gamemode on in KOS Governor, play, and turn it off.";

    public static string StabilitySessionLabel(DateTime start, DateTime end, string? game)
    {
        var t = end - start;
        var length = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(0, (int)t.TotalSeconds)} s";
        return $"{start:g} · {length}{(game is null ? "" : $" · {game}")}";
    }

    /// <summary>Counts of the last 30 days by kind; empty = nothing.</summary>
    public static string StabilitySummary(IReadOnlyDictionary<StabilityKind, int> recent) =>
        StabilityCounts(recent) is { Length: > 0 } text ? $"Last 30 days: {text}." : "Nothing in the last 30 days.";

    public static string StabilityCounts(IReadOnlyDictionary<StabilityKind, int> recent)
    {
        string? Part(StabilityKind k, string one, string many) => recent.TryGetValue(k, out var n) && n > 0 ? $"{n} {(n == 1 ? one : many)}" : null;
        return string.Join(", ", new[]
        {
            Part(StabilityKind.BlueScreen, "blue screen", "blue screens"), Part(StabilityKind.UnexpectedRestart, "unexpected restart", "unexpected restarts"),
            Part(StabilityKind.DriverReset, "display driver reset", "display driver resets"), Part(StabilityKind.HardwareError, "hardware error", "hardware errors"),
        }.Where(p => p is not null));
    }
}
