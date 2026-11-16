using System.Globalization;
using System.Text;

namespace KosToolbox.Core.Latency;

/// <summary>One raw mouse report with movement: when it arrived (ms from the start) and the counts it moved.</summary>
public readonly record struct MouseSample(double Ms, int Dx, int Dy);

/// <summary>Intervals between reports, in ms; <see cref="StablePercent"/> = share of intervals within 10 % of the median.</summary>
public sealed record MouseStats(int Events, double DurationMs, long SumX, long SumY, double Path,
    double MedianMs, double MeanMs, double StdDevMs, double MinMs, double MaxMs, double StablePercent)
{
    /// <summary>The report rate the mouse is running at: one over the typical interval.</summary>
    public double RateHz => MedianMs > 0 ? 1000 / MedianMs : 0;
}

/// <summary>The graphs MouseTester draws.</summary>
public enum MouseGraph { XCounts, YCounts, XYCounts, Interval, Frequency, XVelocity, YVelocity, XYVelocity, XvsY }

/// <summary>
/// Mouse report-rate test like MouseTester: raw input reports stamped with the performance counter as they arrive, then
/// the interval statistics, the graphs, CPI from a measured distance and CSV.
/// </summary>
public static class MouseTest
{
    public static MouseStats? Stats(IReadOnlyList<MouseSample> s)
    {
        if (s.Count < 2) return null;
        var intervals = new double[s.Count - 1];
        for (var i = 1; i < s.Count; i++) intervals[i - 1] = s[i].Ms - s[i - 1].Ms;
        var sorted = intervals.Order().ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        var mean = intervals.Average();
        var std = Math.Sqrt(intervals.Sum(v => (v - mean) * (v - mean)) / intervals.Length);
        var stable = median > 0 ? 100.0 * intervals.Count(v => Math.Abs(v - median) <= median * 0.1) / intervals.Length : 0;
        return new MouseStats(s.Count, s[^1].Ms - s[0].Ms, s.Sum(x => (long)x.Dx), s.Sum(x => (long)x.Dy),
            s.Sum(x => Math.Sqrt((double)x.Dx * x.Dx + (double)x.Dy * x.Dy)), median, mean, std, sorted[0], sorted[^1], stable);
    }

    /// <summary>Counts per inch from a horizontal move of <paramref name="cm"/> (MouseTester's Measure).</summary>
    public static double? Cpi(IReadOnlyList<MouseSample> s, double cm) =>
        cm > 0 && s.Count > 0 ? Math.Abs(s.Sum(x => (double)x.Dx)) / (cm / 2.54) : null;

    /// <summary>
    /// Points of each series (X/Y graphs have two). Velocity is in m/s with a CPI, else in counts per ms. The first report has
    /// no interval, so interval, frequency and velocity graphs start at the second.
    /// </summary>
    public static List<List<(double X, double Y)>> Series(IReadOnlyList<MouseSample> s, MouseGraph g, double? cpi)
    {
        var a = new List<(double, double)>();
        var b = new List<(double, double)>();
        double scale = cpi is > 0 ? 0.0254 / cpi.Value * 1000 : 1; // counts/ms → m/s
        long x = 0, y = 0;
        for (var i = 0; i < s.Count; i++)
        {
            var p = s[i];
            var dt = i > 0 ? p.Ms - s[i - 1].Ms : 0;
            switch (g)
            {
                case MouseGraph.XCounts: a.Add((p.Ms, p.Dx)); break;
                case MouseGraph.YCounts: a.Add((p.Ms, p.Dy)); break;
                case MouseGraph.XYCounts: a.Add((p.Ms, p.Dx)); b.Add((p.Ms, p.Dy)); break;
                case MouseGraph.Interval when dt > 0: a.Add((p.Ms, dt)); break;
                case MouseGraph.Frequency when dt > 0: a.Add((p.Ms, 1000 / dt)); break;
                case MouseGraph.XVelocity when dt > 0: a.Add((p.Ms, p.Dx / dt * scale)); break;
                case MouseGraph.YVelocity when dt > 0: a.Add((p.Ms, p.Dy / dt * scale)); break;
                case MouseGraph.XYVelocity when dt > 0: a.Add((p.Ms, p.Dx / dt * scale)); b.Add((p.Ms, p.Dy / dt * scale)); break;
                case MouseGraph.XvsY:
                    x += p.Dx;
                    y += p.Dy;
                    a.Add((x, -y)); // screen Y grows downwards
                    break;
            }
        }
        return b.Count > 0 ? new() { a, b } : new() { a };
    }

    public static string ToCsv(IReadOnlyList<MouseSample> s, double? cpi)
    {
        var sb = new StringBuilder();
        sb.AppendLine(cpi is { } c ? FormattableString.Invariant($"# KOS Toolbox mouse test, CPI {c:0.##}") : "# KOS Toolbox mouse test");
        sb.AppendLine("xCount,yCount,Time (ms)");
        foreach (var p in s) sb.AppendLine(FormattableString.Invariant($"{p.Dx},{p.Dy},{p.Ms:0.####}"));
        return sb.ToString();
    }

    /// <summary>Reads this CSV or MouseTester's (columns xCount, yCount and Time in any order; other columns ignored).</summary>
    public static (List<MouseSample> Samples, double? Cpi) FromCsv(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        double? cpi = null;
        foreach (var l in lines.Where(l => l.StartsWith('#')))
            if (l.LastIndexOf("CPI ", StringComparison.OrdinalIgnoreCase) is var at and >= 0
                && double.TryParse(l[(at + 4)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) cpi = c;
        var header = lines.FirstOrDefault(l => l.Contains("xCount", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("No xCount, yCount, Time header: not a mouse test file.");
        var cols = header.Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int ix = cols.FindIndex(h => h.StartsWith("xcount")), iy = cols.FindIndex(h => h.StartsWith("ycount")), it = cols.FindIndex(h => h.StartsWith("time"));
        if (ix < 0 || iy < 0 || it < 0) throw new FormatException("The header needs xCount, yCount and Time columns.");
        var samples = new List<MouseSample>();
        foreach (var l in lines.Skip(lines.IndexOf(header) + 1))
        {
            if (l.StartsWith('#')) continue;
            var f = l.Split(',');
            if (f.Length <= Math.Max(ix, Math.Max(iy, it))
                || !int.TryParse(f[ix], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dx)
                || !int.TryParse(f[iy], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dy)
                || !double.TryParse(f[it], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                || !double.IsFinite(ms) || Math.Abs(ms) > 1e9) // a day is 8.6e7 ms; huge values stall the graph's axis steps
                throw new FormatException($"Line \"{l}\" isn't xCount, yCount, Time.");
            samples.Add(new MouseSample(ms, dx, dy));
        }
        return (samples, cpi);
    }
}
