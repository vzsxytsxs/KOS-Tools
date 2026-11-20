using KosToolbox.Core.Text;

namespace KosToolbox.Core.Audio;

/// <summary>
/// Built-in profiles every device can pick in KOS Equalizer, as its active or its gamemode profile; none is picked by default.
/// A preset becomes one of the device's profiles (its own copy, edited from there) when it's picked.
/// </summary>
public static class EqPresets
{
    /// <summary>
    /// Footsteps in competitive shooters (Valorant, CS2): less deep bass (explosions, music) and low-mid mud, more of the 2-5 kHz
    /// where steps and reloads are, a little 6-8 kHz for direction; the preamp keeps the curve's peak near 0 dB, so it doesn't clip.
    /// </summary>
    public static EqProfile Footsteps() => new()
    {
        Name = Txt.EqPresetFootsteps,
        Preamp = -6,
        Bands =
        {
            new EqBand { Type = EqFilterType.LowShelf, Frequency = 100, Gain = -6, Q = 0.707 },
            new EqBand { Type = EqFilterType.Peak, Frequency = 250, Gain = -3, Q = 1 },
            new EqBand { Type = EqFilterType.Peak, Frequency = 3000, Gain = 6, Q = 0.8 },
            new EqBand { Type = EqFilterType.Peak, Frequency = 7000, Gain = 2.5, Q = 1.2 },
        },
    };

    /// <summary>A new copy of each preset.</summary>
    public static IReadOnlyList<EqProfile> All() => [Footsteps()];

    public static EqProfile? Find(string name) => All().FirstOrDefault(p => p.Name == name);
}
