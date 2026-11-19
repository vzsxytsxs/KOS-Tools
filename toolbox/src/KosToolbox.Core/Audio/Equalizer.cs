using System.Numerics;

namespace KosToolbox.Core.Audio;

public enum EqFilterType { Peak, LowShelf, HighShelf, LowPass, HighPass, Notch, BandPass }

/// <summary>One filter. Gain is in dB and ignored by the pass, notch and band-pass types.</summary>
public sealed class EqBand
{
    public EqFilterType Type { get; set; }
    public double Frequency { get; set; }
    public double Gain { get; set; }
    public double Q { get; set; } = 0.707;
    public bool Enabled { get; set; } = true;
}

public sealed class EqProfile
{
    public string Name { get; set; } = "Profile";
    public double Preamp { get; set; }
    public List<EqBand> Bands { get; set; } = new();
    /// <summary>0 = parametric; otherwise 10, 15 or 31 fixed graphic bands.</summary>
    public int GraphicBands { get; set; }
}

/// <summary>Biquad coefficients normalized so a0 = 1.</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

/// <summary>Biquad design (Audio EQ Cookbook) and frequency response of equalizer profiles.</summary>
public static class Equalizer
{
    public static readonly double[] Graphic10 = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly double[] Graphic15 = { 25, 40, 63, 100, 160, 250, 400, 630, 1000, 1600, 2500, 4000, 6300, 10000, 16000 };
    public static readonly double[] Graphic31 =
    {
        20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000,
    };

    /// <summary>Q of a peaking filter that is <paramref name="octaves"/> wide.</summary>
    public static double QFromOctaves(double octaves)
    {
        var n = Math.Pow(2, octaves);
        return Math.Sqrt(n) / (n - 1);
    }

    public static Biquad Design(EqBand band, double sampleRate)
    {
        var f = Math.Clamp(band.Frequency, 1, 0.49 * sampleRate);
        var q = Math.Clamp(band.Q, 0.1, 30);
        var gain = Math.Clamp(band.Gain, -30, 30);
        var w0 = 2 * Math.PI * f / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        var a = Math.Pow(10, gain / 40);
        var tsa = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (band.Type)
        {
            case EqFilterType.LowShelf:
                b0 = a * ((a + 1) - (a - 1) * cos + tsa);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - tsa);
                a0 = (a + 1) + (a - 1) * cos + tsa;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - tsa;
                break;
            case EqFilterType.HighShelf:
                b0 = a * ((a + 1) + (a - 1) * cos + tsa);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - tsa);
                a0 = (a + 1) - (a - 1) * cos + tsa;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - tsa;
                break;
            case EqFilterType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = b0;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case EqFilterType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = b0;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case EqFilterType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case EqFilterType.BandPass: // constant 0 dB peak gain
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            default: // Peak
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
        }
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>|H(e^jw)| in dB.</summary>
    public static double Magnitude(Biquad f, double frequency, double sampleRate)
    {
        var w = 2 * Math.PI * frequency / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var h = (f.B0 + f.B1 * z1 + f.B2 * z2) / (1 + f.A1 * z1 + f.A2 * z2);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-12));
    }

    /// <summary>Preamp plus the sum of the enabled bands, in dB.</summary>
    public static double Response(EqProfile p, double frequency, double sampleRate = 48000)
    {
        var db = p.Preamp;
        foreach (var b in p.Bands)
            if (b.Enabled) db += Magnitude(Design(b, sampleRate), frequency, sampleRate);
        return db;
    }

    /// <summary>Log-spaced 20 Hz..20 kHz response for a graph.</summary>
    public static IReadOnlyList<(double Frequency, double Db)> Curve(EqProfile p, int points = 256, double sampleRate = 48000)
    {
        points = Math.Max(points, 2);
        var filters = new List<Biquad>();
        foreach (var b in p.Bands)
            if (b.Enabled) filters.Add(Design(b, sampleRate));
        var result = new List<(double, double)>(points);
        for (var i = 0; i < points; i++)
        {
            var f = 20 * Math.Pow(1000, (double)i / (points - 1));
            var db = p.Preamp;
            foreach (var bq in filters) db += Magnitude(bq, f, sampleRate);
            result.Add((f, db));
        }
        return result;
    }

    /// <summary>The highest point of the curve in dB between 20 Hz and 20 kHz, for display (the +24 dB limit uses <see cref="EqSettingsFile.Peak"/>).</summary>
    public static double PeakGain(EqProfile p) => Curve(p, 512).Max(c => c.Db);

    /// <summary>A preamp that keeps the curve at or below 0 dB, rounded to 0.1 dB.</summary>
    public static double SuggestedPreamp(EqProfile p)
    {
        var boost = PeakGain(p) - p.Preamp;
        return boost <= 0 ? 0 : Math.Round(-boost, 1);
    }

    public static EqProfile Graphic(int bands, IReadOnlyList<double> gains)
    {
        var (freqs, octaves) = bands switch
        {
            10 => (Graphic10, 1.0),
            15 => (Graphic15, 2.0 / 3),
            31 => (Graphic31, 1.0 / 3),
            _ => throw new ArgumentException("Graphic equalizers have 10, 15 or 31 bands.", nameof(bands)),
        };
        if (gains.Count != bands) throw new ArgumentException($"Expected {bands} gains, got {gains.Count}.", nameof(gains));
        var q = QFromOctaves(octaves);
        var p = new EqProfile { Name = "Graphic", GraphicBands = bands };
        for (var i = 0; i < bands; i++)
            p.Bands.Add(new EqBand { Type = EqFilterType.Peak, Frequency = freqs[i], Gain = gains[i], Q = q });
        return p;
    }

    /// <summary>Null when the profile is valid, otherwise the reason it is not.</summary>
    public static string? Problem(EqProfile p)
    {
        if (string.IsNullOrEmpty(p.Name) || p.Name.Length > 64) return "The name must be 1 to 64 characters.";
        if (!(p.Preamp is >= -30 and <= 30)) return "The preamp must be between -30 and +30 dB.";
        if (p.GraphicBands is not (0 or 10 or 15 or 31)) return "A graphic equalizer has 10, 15 or 31 bands.";
        if (p.Bands.Count > 64) return "At most 64 bands are allowed.";
        for (var i = 0; i < p.Bands.Count; i++)
        {
            var b = p.Bands[i];
            var n = i + 1;
            if (!(b.Frequency is >= 10 and <= 24000)) return $"Band {n}: the frequency must be between 10 and 24000 Hz.";
            if (!(b.Q is >= 0.1 and <= 30)) return $"Band {n}: Q must be between 0.1 and 30.";
            if (!(b.Gain is >= -30 and <= 30)) return $"Band {n}: the gain must be between -30 and +30 dB.";
        }
        return null;
    }
}
