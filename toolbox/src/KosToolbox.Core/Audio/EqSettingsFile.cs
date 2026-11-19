using System.Globalization;
using System.Text;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Audio;

/// <summary>
/// The file KOS Equalizer's audio processing object (toolbox/apo) reads for one endpoint:
/// %ProgramData%\TheKOS\Audio\Eq\&lt;endpoint guid&gt;.eq, one line each: "preamp -3.5" and "peak 1000 6 1.41" (type, Hz, dB, Q).
/// The folders are created admin-only (users and the audio engine's account read them); the DLL refuses a file or folder that's
/// a link or that SYSTEM/Administrators don't own, and a curve that boosts more than <see cref="MaxBoost"/> dB.
/// </summary>
public static class EqSettingsFile
{
    /// <summary>The highest boost the DLL accepts (the curve's peak, preamp included); the same number is in KosEqApo.cpp.</summary>
    public const double MaxBoost = 24;

    /// <summary>A flat file: the effect passes the sound through unchanged.</summary>
    public const string FlatText = "# KOS Equalizer: flat\r\n";

    public static string AudioDirectory => Path.Combine(AppPaths.TheKosRoot, "Audio");
    public static string Directory => Path.Combine(AudioDirectory, "Eq");

    /// <summary>
    /// The endpoint's GUID as the DLL names its file (its ConfigPath): what follows the id's last '.', hex digits and '-' only,
    /// lower case. "{0.0.0.00000000}.{3718CD90-...}" → "3718cd90-...".
    /// </summary>
    public static string FileName(string endpointId)
    {
        var dot = endpointId.LastIndexOf('.');
        var guid = dot < 0 ? endpointId : endpointId[(dot + 1)..];
        var name = new StringBuilder();
        foreach (var c in guid)
            if (name.Length < 60 && (char.IsAsciiHexDigit(c) || c == '-')) name.Append(char.ToLowerInvariant(c));
        return name.ToString();
    }

    public static string PathFor(string endpointId) => Path.Combine(Directory, FileName(endpointId) + ".eq");

    static string TypeName(EqFilterType t) => t switch
    {
        EqFilterType.LowShelf => "lowshelf", EqFilterType.HighShelf => "highshelf", EqFilterType.LowPass => "lowpass",
        EqFilterType.HighPass => "highpass", EqFilterType.Notch => "notch", EqFilterType.BandPass => "bandpass", _ => "peak",
    };

    /// <summary>The file's text for a profile: the preamp, then each enabled band; invariant numbers, CRLF.</summary>
    public static string Format(EqProfile p)
    {
        var text = new StringBuilder();
        // The name only as a comment, printable ASCII: a line break in it can't add a line of its own.
        text.Append(CultureInfo.InvariantCulture, $"# KOS Equalizer: {new string(p.Name.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray())}\r\n");
        text.Append(CultureInfo.InvariantCulture, $"preamp {p.Preamp:0.###}\r\n");
        foreach (var b in p.Bands.Where(b => b.Enabled))
            text.Append(CultureInfo.InvariantCulture, $"{TypeName(b.Type)} {b.Frequency:0.###} {b.Gain:0.###} {b.Q:0.####}\r\n");
        return text.ToString();
    }

    /// <summary>Null when the DLL would take the profile; otherwise why not (invalid values or a boost above <see cref="MaxBoost"/>).</summary>
    public static string? Problem(EqProfile p) =>
        Equalizer.Problem(p) ?? (Peak(p) > MaxBoost ? $"The curve boosts more than +{MaxBoost:0} dB; lower the bands or the preamp." : null);

    /// <summary>
    /// The curve's highest point in dB, preamp included, as the DLL checks it: 1024 log-spaced points from 20 Hz up to where a band
    /// can sit (0.49 × the rate), plus each enabled band's own frequency, so a narrow band between two points isn't missed.
    /// </summary>
    public static double Peak(EqProfile p, double sampleRate = 48000)
    {
        var enabled = Enabled(p);
        var top = 0.49 * sampleRate;
        return Enumerable.Range(0, 1024).Select(i => 20 * Math.Pow(top / 20, i / 1023.0))
            .Concat(enabled.Bands.Select(b => Math.Clamp(b.Frequency, 1, top)))
            .Max(f => Equalizer.Response(enabled, f, sampleRate));
    }

    static EqProfile Enabled(EqProfile p) => new() { Name = p.Name, Preamp = p.Preamp, GraphicBands = p.GraphicBands, Bands = p.Bands.Where(b => b.Enabled).ToList() };

    /// <summary>
    /// Writes the endpoint's file (null: flat, an empty file) through a temporary file, and reads it back. Needs administrator:
    /// the folders are created admin-only, an existing folder that's a link or someone else's is refused.
    /// </summary>
    public static void Write(string endpointId, EqProfile? profile)
    {
        if (profile is not null && Problem(profile) is { } problem) throw new InvalidOperationException(problem);
        if (FileName(endpointId).Length == 0) throw new ArgumentException("Not an audio endpoint id.", nameof(endpointId));
        GpuTuner.PrepareDirectory(AudioDirectory);
        GpuTuner.PrepareDirectory(Directory);
        var path = PathFor(endpointId);
        var text = profile is null ? FlatText : Format(profile);
        var tmp = $"{path}.{Environment.ProcessId}.tmp"; // KOS Equalizer and KOS Engine may write at once: each its own temporary file
        File.Delete(tmp); // an old .tmp could be a hard link to another file
        File.WriteAllText(tmp, text, Encoding.ASCII);
        File.Move(tmp, path, overwrite: true);
        if (File.ReadAllText(path, Encoding.ASCII) != text) throw new IOException($"{path} doesn't hold what was written.");
    }

    /// <summary>The endpoint's file as written, or null when there's none.</summary>
    public static string? Read(string endpointId) => File.Exists(PathFor(endpointId)) ? File.ReadAllText(PathFor(endpointId), Encoding.ASCII) : null;
}
