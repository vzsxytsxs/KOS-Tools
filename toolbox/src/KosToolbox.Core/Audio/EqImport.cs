using System.Globalization;
using System.Text;

namespace KosToolbox.Core.Audio;

public sealed record EqImportResult(EqProfile Profile, IReadOnlyList<string> Ignored);

/// <summary>Reads and writes the Equalizer APO configuration file format (AutoEQ's ParametricEQ.txt and GraphicEQ.txt use it too).</summary>
public static class EqImport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] Unsupported = { "device", "channel", "include", "stage", "copy", "delay", "convolution" };

    public static EqImportResult Parse(string text)
    {
        var profile = new EqProfile { Name = "Imported" };
        var ignored = new List<string>();
        List<(double F, double G)>? graphic = null;
        var lines = (text ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var n = i + 1;
            var colon = line.IndexOf(':');
            if (colon < 0) { ignored.Add($"Line {n}: not understood: {line}"); continue; }
            var key = line[..colon].Trim().ToLowerInvariant();
            var rest = line[(colon + 1)..].Trim();

            if (key == "preamp")
            {
                var t = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var k = 0;
                if (t.Length > 0 && ReadNum(t, ref k, "dB", out var v) && k == t.Length) profile.Preamp = v;
                else ignored.Add($"Line {n}: not understood: {line}");
            }
            else if (key == "filter" || (key.StartsWith("filter ") && IsDigits(key[7..].Trim())))
            {
                var band = ParseFilter(rest);
                if (band is null) ignored.Add($"Line {n}: not understood: {line}");
                else if (profile.Bands.Count >= 64) ignored.Add($"Line {n}: more than 64 bands: {line}");
                else profile.Bands.Add(band);
            }
            else if (key == "graphiceq")
            {
                var pts = ParseGraphic(rest);
                if (pts is null) ignored.Add($"Line {n}: not understood: {line}");
                else graphic = pts;
            }
            else if (Array.IndexOf(Unsupported, key) >= 0) ignored.Add($"Line {n}: not supported: {line}");
            else ignored.Add($"Line {n}: not understood: {line}");
        }

        if (graphic is not null)
        {
            if (profile.Bands.Count > 0) ignored.Add("The filters were dropped because a GraphicEQ line replaces them.");
            var gains = new double[31];
            for (var i = 0; i < 31; i++) gains[i] = Interpolate(graphic, Equalizer.Graphic31[i]);
            profile.Bands = Equalizer.Graphic(31, gains).Bands;
            profile.GraphicBands = 31;
            ignored.Add("The GraphicEQ curve was resampled to 31 bands.");
        }
        return new EqImportResult(profile, ignored);
    }

    public static string Export(EqProfile p)
    {
        var sb = new StringBuilder();
        sb.Append("Preamp: ").Append(F(p.Preamp)).Append(" dB\n");
        for (var i = 0; i < p.Bands.Count; i++)
        {
            var b = p.Bands[i];
            var (code, hasGain) = b.Type switch
            {
                EqFilterType.LowShelf => ("LSC", true),
                EqFilterType.HighShelf => ("HSC", true),
                EqFilterType.LowPass => ("LPQ", false),
                EqFilterType.HighPass => ("HPQ", false),
                EqFilterType.Notch => ("NO", false),
                EqFilterType.BandPass => ("BP", false),
                _ => ("PK", true),
            };
            sb.Append("Filter ").Append((i + 1).ToString(Inv)).Append(": ").Append(b.Enabled ? "ON " : "OFF ").Append(code)
              .Append(" Fc ").Append(F(b.Frequency)).Append(" Hz");
            if (hasGain) sb.Append(" Gain ").Append(F(b.Gain)).Append(" dB");
            sb.Append(" Q ").Append(F(b.Q)).Append('\n');
        }
        return sb.ToString();
    }

    static string F(double v) => v.ToString("R", Inv);

    static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>Reads a number at t[i] (a unit may be attached or be the next token); no decimal comma.</summary>
    static bool ReadNum(string[] t, ref int i, string unit, out double v)
    {
        v = 0;
        if (i >= t.Length) return false;
        var s = t[i];
        if (unit.Length > 0 && s.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) s = s[..^unit.Length];
        if (!double.TryParse(s, NumberStyles.Float, Inv, out v) || !double.IsFinite(v)) return false;
        i++;
        if (unit.Length > 0 && i < t.Length && t[i].Equals(unit, StringComparison.OrdinalIgnoreCase)) i++;
        return true;
    }

    static EqBand? ParseFilter(string s)
    {
        var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        if (t.Length < 2) return null;
        bool enabled;
        switch (t[i++].ToUpperInvariant())
        {
            case "ON": enabled = true; break;
            case "OFF": enabled = false; break;
            default: return null;
        }
        EqFilterType type;
        switch (t[i++].ToUpperInvariant())
        {
            case "PK": case "PEQ": case "MODAL": type = EqFilterType.Peak; break;
            case "LS": case "LSC": type = EqFilterType.LowShelf; break;
            case "HS": case "HSC": type = EqFilterType.HighShelf; break;
            case "LP": case "LPQ": type = EqFilterType.LowPass; break;
            case "HP": case "HPQ": type = EqFilterType.HighPass; break;
            case "NO": type = EqFilterType.Notch; break;
            case "BP": type = EqFilterType.BandPass; break;
            default: return null;
        }
        // "LS 6dB" / "HS 12dB": a slope variant, the shelf keeps its default Q
        if (i < t.Length && (t[i].Equals("6dB", StringComparison.OrdinalIgnoreCase) || t[i].Equals("12dB", StringComparison.OrdinalIgnoreCase))) i++;

        double? fc = null, gain = null, q = null;
        while (i < t.Length)
        {
            var kw = t[i++].ToUpperInvariant();
            double v;
            switch (kw)
            {
                case "FC": if (!ReadNum(t, ref i, "Hz", out v)) return null; fc = v; break;
                case "GAIN": if (!ReadNum(t, ref i, "dB", out v)) return null; gain = v; break;
                case "Q": if (!ReadNum(t, ref i, "", out v)) return null; q = v; break;
                case "BW":
                    if (i >= t.Length || !t[i].Equals("Oct", StringComparison.OrdinalIgnoreCase)) return null;
                    i++;
                    if (!ReadNum(t, ref i, "", out v) || v <= 0) return null;
                    q = Equalizer.QFromOctaves(v);
                    if (!double.IsFinite(q.Value)) return null; // a bandwidth too wide or too narrow for a Q (NaN can't be saved)
                    break;
                default: return null;
            }
        }
        if (fc is null) return null;
        var needsGain = type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf;
        if (needsGain && gain is null) return null;
        return new EqBand { Type = type, Frequency = fc.Value, Gain = gain ?? 0, Q = q ?? 0.707, Enabled = enabled };
    }

    static List<(double F, double G)>? ParseGraphic(string s)
    {
        var pts = new List<(double F, double G)>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 2 || !double.TryParse(t[0], NumberStyles.Float, Inv, out var f) || !double.TryParse(t[1], NumberStyles.Float, Inv, out var g)
                || !double.IsFinite(f) || !double.IsFinite(g) || f <= 0) return null;
            pts.Add((f, g));
        }
        if (pts.Count == 0) return null;
        pts.Sort((a, b) => a.F.CompareTo(b.F));
        return pts;
    }

    /// <summary>Linear in log-frequency, clamped at the ends.</summary>
    static double Interpolate(List<(double F, double G)> pts, double f)
    {
        if (f <= pts[0].F) return pts[0].G;
        if (f >= pts[^1].F) return pts[^1].G;
        var hi = pts.FindIndex(p => p.F >= f);
        var (f0, g0) = pts[hi - 1];
        var (f1, g1) = pts[hi];
        if (f1 == f0) return g1;
        var x = (Math.Log(f) - Math.Log(f0)) / (Math.Log(f1) - Math.Log(f0));
        return g0 + (g1 - g0) * x;
    }
}
