namespace KosToolbox.Core.Audio;

/// <summary>What the audio settings panel offers and how it checks a read-back. Pure, tested.</summary>
public static class AudioSettings
{
    /// <summary>A volume level (0–1) as the whole percent the panel shows and compares.</summary>
    public static int Percent(float level) => (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    /// <summary><see cref="AudioDevices.SetExclusive"/> stores priority only while exclusive mode is allowed.</summary>
    public static bool ExclusiveTook(AudioDevice read, bool allow, bool priority) =>
        read.ExclusiveAllowed == allow && read.ExclusivePriority == (allow && priority);

    /// <summary>
    /// One choice per bit depth and rate (the candidates hold 24 bit in 24 and in 32 bits, which read the same), the current format
    /// always among them with its own container, ordered by bit depth then rate.
    /// </summary>
    public static List<AudioFormat> FormatChoices(IEnumerable<AudioFormat> supported, AudioFormat? current)
    {
        var list = new List<AudioFormat>();
        foreach (var f in current is null ? supported : supported.Prepend(current))
            if (!list.Any(x => x.Bits == f.Bits && x.SampleRate == f.SampleRate)) list.Add(f);
        return list.OrderBy(f => f.Bits).ThenBy(f => f.SampleRate).ToList();
    }
}
