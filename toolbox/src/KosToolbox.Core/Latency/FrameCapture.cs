using System.Globalization;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Latency;

/// <summary>A present mode and the share of frames that used it.</summary>
public sealed record PresentModeShare(string Mode, double Share);

/// <summary>What a capture measured for the busiest swap chain of the program.</summary>
public sealed record FrameReport(string Program, int Frames, double Seconds, double AverageFps, double Low1Fps, double Low01Fps,
    double MedianMs, double MaxMs, int Spikes, IReadOnlyList<PresentModeShare> PresentModes, double TearingShare, IReadOnlyList<double> FrameTimesMs)
{
    /// <summary>Independent flip: the game's frames reach the screen without the desktop compositor (what fullscreen optimizations and MPO aim for).</summary>
    public bool IndependentFlip => PresentModes.FirstOrDefault()?.Mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Frame times of a running game through PresentMon (the console build KOS ships in bin\3\PresentMon), which reads the
/// DXGI/D3D present events over ETW: average FPS, 1 % and 0.1 % lows, spikes and the present mode the game really got.
/// </summary>
public static class FrameCapture
{
    const string Session = "KosToolboxFrames";
    public static readonly int[] Durations = { 10, 30, 60 };

    /// <summary>The newest PresentMon in the KOS folder owned by SYSTEM or Administrators (a file a user dropped there is ignored); null when none.</summary>
    public static string? Exe
    {
        get
        {
            var folder = Path.Combine(AppPaths.TheKosRoot, "bin", "3", "PresentMon");
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "PresentMon*.exe").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(f => ToolDiscovery.OwnerProblem(f) is null)
                : null;
        }
    }

    /// <summary>A bare program file name (game.exe): what PresentMon matches.</summary>
    public static bool ValidProgram(string name) => Regex.IsMatch(name, @"^[^\\/:*?""<>|]{1,200}\.exe$", RegexOptions.IgnoreCase);

    public static async Task<FrameReport> CaptureAsync(string program, int seconds, CancellationToken ct = default)
    {
        if (!ValidProgram(program)) throw new ArgumentException($"Not a program name: {program}");
        if (seconds is < 3 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var found = Exe ?? throw new InvalidOperationException("PresentMon isn't in the KOS folder (bin\\3\\PresentMon).");
        if (ToolDiscovery.OwnerProblem(found) is { } owner) throw new InvalidOperationException(owner);
        // Elevated, it runs from a copy in an admin-only folder: users can add files beside the original (a DLL it would load), not there.
        var exe = Setup.Installer.Guard(found);
        var guarded = exe != found;
        var csv = Path.Combine(guarded ? Path.GetDirectoryName(exe)! : Path.GetTempPath(), $"kos-frames-{Guid.NewGuid():N}.csv");
        try
        {
            var args = $"--process_name {ProcessRunner.Quote(program)} --output_file {ProcessRunner.Quote(csv)} --timed {seconds} --terminate_after_timed " +
                       $"--no_console_stats --no_track_input --stop_existing_session --session_name {Session}";
            Log.Info($"Frames: PresentMon {args}");
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromSeconds(seconds + 30), ct: ct).ConfigureAwait(false);
            if (!File.Exists(csv)) throw new InvalidOperationException(r.Output.Trim() is { Length: > 0 } o ? o.Split('\n')[^1].Trim() : $"PresentMon wrote nothing (code {r.ExitCode}).");
            var report = Parse(program, File.ReadLines(csv)) ?? throw new InvalidOperationException($"No frames from {program}: is it running and drawing (not minimized)? Anti-cheats can block the capture.");
            Log.Info($"Frames: {program} {report.Frames} frames, {report.AverageFps:0.0} FPS average, 1 % low {report.Low1Fps:0.0}, mode {report.PresentModes.FirstOrDefault()?.Mode}.");
            return report;
        }
        finally
        {
            try { File.Delete(csv); } catch (IOException) { }
            if (guarded) Setup.Installer.Discard(exe);
        }
    }

    /// <summary>Reads PresentMon's CSV (v1 or v2 column names) and keeps the swap chain with the most frames.</summary>
    internal static FrameReport? Parse(string program, IEnumerable<string> lines)
    {
        using var e = lines.GetEnumerator();
        if (!e.MoveNext()) return null;
        var header = e.Current.Split(',');
        int Col(params string[] names) => Array.FindIndex(header, h => names.Any(n => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)));
        var frameCol = Col("MsBetweenPresents", "FrameTime");
        var modeCol = Col("PresentMode");
        var tearCol = Col("AllowsTearing");
        var chainCol = Col("SwapChainAddress");
        var pidCol = Col("ProcessID");
        if (frameCol < 0) return null;
        var rows = new List<(string Chain, double Ms, string Mode, bool Tearing)>();
        while (e.MoveNext())
        {
            var f = e.Current.Split(',');
            if (f.Length <= frameCol || !double.TryParse(f[frameCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 10_000) continue;
            string At(int i) => i >= 0 && i < f.Length ? f[i] : "";
            rows.Add(($"{At(pidCol)}|{At(chainCol)}", ms, At(modeCol), At(tearCol) == "1"));
        }
        var chain = rows.GroupBy(r => r.Chain).MaxBy(g => g.Count())?.ToList();
        if (chain is null || chain.Count < 10) return null;
        var times = chain.Select(r => r.Ms).ToList();
        var sorted = times.OrderBy(t => t).ToList();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
        var total = times.Sum();
        var median = Percentile(0.5);
        var modes = chain.GroupBy(r => r.Mode.Length > 0 ? r.Mode : "Unknown").Select(g => new PresentModeShare(g.Key, (double)g.Count() / chain.Count))
            .OrderByDescending(m => m.Share).ToList();
        return new FrameReport(program, chain.Count, total / 1000, 1000 * chain.Count / total, 1000 / Percentile(0.99), 1000 / Percentile(0.999),
            median, sorted[^1], times.Count(t => t > 2 * median), modes, (double)chain.Count(r => r.Tearing) / chain.Count, times);
    }
}
