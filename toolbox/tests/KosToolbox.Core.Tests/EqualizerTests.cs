using KosToolbox.Core.Audio;
using Xunit;

namespace KosToolbox.Core.Tests;

public class EqualizerTests
{
    static EqProfile One(EqFilterType type, double f, double gain = 0, double q = 1) =>
        new() { Bands = { new EqBand { Type = type, Frequency = f, Gain = gain, Q = q } } };

    [Fact]
    public void FootstepsPresetRaisesStepsOverBassWithoutClipping()
    {
        var p = EqPresets.Footsteps();
        Assert.Null(EqSettingsFile.Problem(p));
        Assert.InRange(EqSettingsFile.Peak(p), -1, 0.5); // the preamp keeps the peak near 0 dB
        Assert.True(Equalizer.Response(p, 3000) - Equalizer.Response(p, 60) > 10); // steps well above the bass
        Assert.NotSame(EqPresets.Find(p.Name), EqPresets.Find(p.Name)); // each pick gets its own copy
        var device = new EqConfig().Device("{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}", "Headphones");
        Assert.Null(device.Playing(true)); // nothing is picked by default, gamemode or not
    }

    [Fact]
    public void PeakBoostsAtCenterOnly()
    {
        var p = One(EqFilterType.Peak, 1000, 6, 1);
        Assert.InRange(Equalizer.Response(p, 1000), 5.95, 6.05);
        Assert.InRange(Equalizer.Response(p, 20), -0.3, 0.3);
        Assert.InRange(Equalizer.Response(p, 20000), -0.3, 0.3);
    }

    [Fact]
    public void LowShelf()
    {
        var p = One(EqFilterType.LowShelf, 100, 6, 0.707);
        Assert.InRange(Equalizer.Response(p, 20), 5.7, 6.1);
        Assert.InRange(Equalizer.Response(p, 10000), -0.3, 0.3);
    }

    [Fact]
    public void HighShelf()
    {
        var p = One(EqFilterType.HighShelf, 8000, 6, 0.707);
        Assert.InRange(Equalizer.Response(p, 20000), 5.0, 6.1);
        Assert.InRange(Equalizer.Response(p, 100), -0.3, 0.3);
    }

    [Fact]
    public void HighPass()
    {
        var p = One(EqFilterType.HighPass, 100, 0, 0.707);
        Assert.InRange(Equalizer.Response(p, 100), -3.3, -2.8);
        Assert.True(Equalizer.Response(p, 20) < -20);
    }

    [Fact]
    public void LowPass()
    {
        var p = One(EqFilterType.LowPass, 1000, 0, 0.707);
        Assert.InRange(Equalizer.Response(p, 1000), -3.3, -2.8);
        Assert.True(Equalizer.Response(p, 20000) < -20);
    }

    [Fact]
    public void Notch() => Assert.True(Equalizer.Response(One(EqFilterType.Notch, 1000, 0, 5), 1000) < -30);

    [Fact]
    public void BandPassIsZeroAtCenter()
    {
        var p = One(EqFilterType.BandPass, 1000, 0, 2);
        Assert.InRange(Equalizer.Response(p, 1000), -0.05, 0.05);
        Assert.True(Equalizer.Response(p, 50) < -20);
    }

    [Fact]
    public void PeakCoefficientsMatchTheFormula()
    {
        const double fs = 48000, f = 1000, g = 6, q = 1;
        var w0 = 2 * Math.PI * f / fs;
        var alpha = Math.Sin(w0) / (2 * q);
        var a = Math.Pow(10, g / 40);
        var a0 = 1 + alpha / a;
        var bq = Equalizer.Design(new EqBand { Type = EqFilterType.Peak, Frequency = f, Gain = g, Q = q }, fs);
        Assert.Equal((1 + alpha * a) / a0, bq.B0, 9);
        Assert.Equal(-2 * Math.Cos(w0) / a0, bq.B1, 9);
        Assert.Equal((1 - alpha * a) / a0, bq.B2, 9);
        Assert.Equal(-2 * Math.Cos(w0) / a0, bq.A1, 9);
        Assert.Equal((1 - alpha / a) / a0, bq.A2, 9);
    }

    [Fact]
    public void DisabledBandsAndPreampCount()
    {
        var p = One(EqFilterType.Peak, 1000, 6);
        p.Bands[0].Enabled = false;
        p.Preamp = -2;
        Assert.Equal(-2, Equalizer.Response(p, 1000), 6);
    }

    [Fact]
    public void FrequencyAtNyquistDoesNotProduceNaN()
    {
        var bq = Equalizer.Design(new EqBand { Type = EqFilterType.Peak, Frequency = 30000, Gain = 6, Q = 1 }, 48000);
        Assert.False(double.IsNaN(bq.B0));
    }

    [Fact]
    public void CurveIsLogSpacedFrom20To20k()
    {
        var c = Equalizer.Curve(new EqProfile(), 100);
        Assert.Equal(100, c.Count);
        Assert.Equal(20, c[0].Frequency, 6);
        Assert.Equal(20000, c[^1].Frequency, 3);
    }

    [Fact]
    public void SuggestedPreampCompensatesTheBoost()
    {
        var p = One(EqFilterType.Peak, 1000, 6, 1);
        Assert.InRange(Equalizer.SuggestedPreamp(p), -6.05, -5.95);
        Assert.InRange(Equalizer.PeakGain(p), 5.9, 6.05);
        Assert.Equal(0, Equalizer.SuggestedPreamp(One(EqFilterType.Peak, 1000, -6, 1)));
    }

    [Theory]
    [InlineData(10, 1.414)]
    [InlineData(15, 2.146)]
    [InlineData(31, 4.32)]
    public void GraphicBuildsPeakBands(int count, double q)
    {
        var p = Equalizer.Graphic(count, new double[count]);
        Assert.Equal(count, p.Bands.Count);
        Assert.Equal(count, p.GraphicBands);
        Assert.All(p.Bands, b => { Assert.Equal(EqFilterType.Peak, b.Type); Assert.InRange(b.Q, q - 0.01, q + 0.01); });
    }

    [Fact]
    public void GraphicRejectsWrongCounts()
    {
        Assert.Throws<ArgumentException>(() => Equalizer.Graphic(12, new double[12]));
        Assert.Throws<ArgumentException>(() => Equalizer.Graphic(10, new double[9]));
    }

    [Fact]
    public void ProblemFindsInvalidValues()
    {
        Assert.Null(Equalizer.Problem(new EqProfile()));
        Assert.NotNull(Equalizer.Problem(new EqProfile { Name = "" }));
        Assert.NotNull(Equalizer.Problem(new EqProfile { Preamp = 31 }));
        Assert.NotNull(Equalizer.Problem(One(EqFilterType.Peak, 5)));
        Assert.NotNull(Equalizer.Problem(One(EqFilterType.Peak, 1000, 0, 50)));
        Assert.NotNull(Equalizer.Problem(One(EqFilterType.Peak, 1000, 40)));
        var many = new EqProfile();
        for (var i = 0; i < 65; i++) many.Bands.Add(new EqBand { Frequency = 1000 });
        Assert.NotNull(Equalizer.Problem(many));
    }

    const string AutoEq =
        "Preamp: -6.2 dB\r\n" +
        "Filter 1: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70\r\n" +
        "Filter 2: ON PK Fc 200 Hz Gain -2.1 dB Q 1.20\r\n" +
        "Filter 3: ON PK Fc 450 Hz Gain 1.3 dB Q 2.50\r\n" +
        "Filter 4: ON PK Fc 1200 Hz Gain -3.0 dB Q 3.00\r\n" +
        "Filter 5: ON PK Fc 2400 Hz Gain 2.2 dB Q 1.50\r\n" +
        "Filter 6: ON PK Fc 3500 Hz Gain -4.4 dB Q 4.00\r\n" +
        "Filter 7: ON PK Fc 5600 Hz Gain 3.1 dB Q 2.00\r\n" +
        "Filter 8: ON PK Fc 8000 Hz Gain -1.0 dB Q 1.00\r\n" +
        "Filter 9: ON PK Fc 12000 Hz Gain 1.5 dB Q 2.00\r\n" +
        "Filter 10: ON HSC Fc 10000 Hz Gain -2.0 dB Q 0.70\r\n";

    [Fact]
    public void ParsesAnAutoEqFile()
    {
        var r = EqImport.Parse(AutoEq);
        Assert.Empty(r.Ignored);
        Assert.Equal("Imported", r.Profile.Name);
        Assert.Equal(-6.2, r.Profile.Preamp);
        Assert.Equal(10, r.Profile.Bands.Count);
        Assert.Equal(EqFilterType.LowShelf, r.Profile.Bands[0].Type);
        Assert.Equal(105, r.Profile.Bands[0].Frequency);
        Assert.Equal(5.5, r.Profile.Bands[0].Gain);
        Assert.Equal(0.7, r.Profile.Bands[0].Q);
        Assert.Equal(EqFilterType.HighShelf, r.Profile.Bands[9].Type);
        Assert.Null(Equalizer.Problem(r.Profile));
    }

    [Fact]
    public void ParsesUnnumberedOffAndAttachedUnits()
    {
        var r = EqImport.Parse("Filter: OFF PK Fc 1000Hz Gain 3dB Q 1\nFilter: ON HP Fc 80 Hz\nFilter: ON LS Fc 100 Hz Gain 4 dB\nFilter: ON LS 12dB Fc 100 Hz Gain 4 dB");
        Assert.Empty(r.Ignored);
        Assert.False(r.Profile.Bands[0].Enabled);
        Assert.Equal(1000, r.Profile.Bands[0].Frequency);
        Assert.Equal(EqFilterType.HighPass, r.Profile.Bands[1].Type);
        Assert.Equal(0.707, r.Profile.Bands[2].Q);
        Assert.Equal(0.707, r.Profile.Bands[3].Q);
    }

    [Fact]
    public void BandwidthInOctavesBecomesQ()
    {
        var r = EqImport.Parse("Filter: ON PK Fc 1000 Hz Gain 3 dB BW Oct 1");
        Assert.Equal(Math.Sqrt(2), r.Profile.Bands[0].Q, 9);
    }

    [Fact]
    public void ReportsUnsupportedAndGarbageWithoutThrowing()
    {
        var r = EqImport.Parse("# comment\n\nDevice: Speakers\nChannel: L\nInclude: x.txt\nthis is not a filter\nFilter: ON XX Fc 100 Hz\nFilter: ON PK Gain 3 dB\nPreamp: loud\nFilter: ON PK Fc 100 Hz Gain 5,5 dB Q 1");
        Assert.Empty(r.Profile.Bands);
        Assert.Equal(0, r.Profile.Preamp);
        Assert.Equal(8, r.Ignored.Count);
        Assert.Contains("Line 3", r.Ignored[0]);
        Assert.Contains("not supported", r.Ignored[0]);
        Assert.Contains("not understood", r.Ignored[^1]);
        Assert.Contains("Line 10", r.Ignored[^1]);
        EqImport.Parse("\0\0::;;Filter:\nGraphicEQ: ;;\nFilter 1x: ON");
    }

    [Fact]
    public void GraphicEqIsResampledTo31Bands()
    {
        var pts = string.Join("; ", Enumerable.Range(0, 100).Select(i =>
        {
            var f = 20 * Math.Pow(1000, i / 99.0);
            return FormattableString.Invariant($"{f:0.##} {-2.0}");
        }));
        var r = EqImport.Parse("Preamp: -1 dB\nGraphicEQ: " + pts);
        Assert.Equal(31, r.Profile.GraphicBands);
        Assert.Equal(31, r.Profile.Bands.Count);
        Assert.All(r.Profile.Bands, b => Assert.Equal(-2, b.Gain, 3));
        Assert.Equal(-1, r.Profile.Preamp);
        Assert.Contains(r.Ignored, s => s.Contains("31 bands"));
    }

    [Fact]
    public void GraphicEqSlopeIsInterpolatedInLogFrequency()
    {
        // A tilt of -3 dB per decade from 0 dB at 20 Hz, given at 20, 200, 2000 and 20000 Hz only.
        var r = EqImport.Parse("GraphicEQ: 20 0; 200 -3; 2000 -6; 20000 -9");
        double Gain(double f) => r.Profile.Bands.Single(b => b.Frequency == f).Gain;
        Assert.Equal(0, Gain(20), 6);
        Assert.Equal(-3 * Math.Log10(1000 / 20.0), Gain(1000), 6); // between two points, on the log axis
        Assert.Equal(-3 * Math.Log10(63 / 20.0), Gain(63), 6);
        Assert.Equal(-9, Gain(20000), 6);
    }

    [Fact]
    public void ExportThenParseRoundTrips()
    {
        var p = new EqProfile { Preamp = -3.5 };
        p.Bands.Add(new EqBand { Type = EqFilterType.LowShelf, Frequency = 105, Gain = 5.5, Q = 0.7 });
        p.Bands.Add(new EqBand { Type = EqFilterType.Peak, Frequency = 1234.5, Gain = -2.25, Q = 1.4142135623730951, Enabled = false });
        p.Bands.Add(new EqBand { Type = EqFilterType.HighPass, Frequency = 40, Q = 0.707 });
        p.Bands.Add(new EqBand { Type = EqFilterType.Notch, Frequency = 50, Q = 10 });
        var r = EqImport.Parse(EqImport.Export(p));
        Assert.Empty(r.Ignored);
        Assert.Equal(p.Preamp, r.Profile.Preamp);
        Assert.Equal(p.Bands.Count, r.Profile.Bands.Count);
        for (var i = 0; i < p.Bands.Count; i++)
        {
            Assert.Equal(p.Bands[i].Type, r.Profile.Bands[i].Type);
            Assert.Equal(p.Bands[i].Frequency, r.Profile.Bands[i].Frequency);
            Assert.Equal(p.Bands[i].Gain, r.Profile.Bands[i].Gain);
            Assert.Equal(p.Bands[i].Q, r.Profile.Bands[i].Q);
            Assert.Equal(p.Bands[i].Enabled, r.Profile.Bands[i].Enabled);
        }
    }

    [Fact]
    public void ImportRefusesABandwidthWithNoQAndKeeps64Bands()
    {
        Assert.Empty(EqImport.Parse("Filter: ON PK Fc 1000 Hz Gain 3 dB BW Oct 2000").Profile.Bands);
        var many = EqImport.Parse(string.Join('\n', Enumerable.Range(0, 70).Select(_ => "Filter: ON PK Fc 1000 Hz Gain 1 dB Q 1")));
        Assert.Equal(64, many.Profile.Bands.Count);
        Assert.Equal(6, many.Ignored.Count(l => l.Contains("more than 64")));
    }
}
