using KosToolbox.Core.Audio;
using Xunit;

namespace KosToolbox.Core.Tests;

public class AudioSettingsTests
{
    static AudioDevice Device(bool? allow, bool? priority) =>
        new("id", "Speakers", AudioFlow.Playback, true, true, 0.5f, false, null, allow, priority, null);

    [Fact]
    public void PercentRoundsAndClamps()
    {
        Assert.Equal(64, AudioSettings.Percent(0.64f));   // 0.64f * 100 is 64.0000x
        Assert.Equal(65, AudioSettings.Percent(0.646f));
        Assert.Equal(64, AudioSettings.Percent(0.644f));
        Assert.Equal(100, AudioSettings.Percent(1f));
        Assert.Equal(0, AudioSettings.Percent(-0.2f));
        Assert.Equal(100, AudioSettings.Percent(1.3f));
    }

    [Fact]
    public void ExclusivePriorityCountsOnlyWhileAllowed()
    {
        Assert.True(AudioSettings.ExclusiveTook(Device(true, true), true, true));
        Assert.True(AudioSettings.ExclusiveTook(Device(true, false), true, false));
        // Turning exclusive mode off stores priority off too, whatever was asked.
        Assert.True(AudioSettings.ExclusiveTook(Device(false, false), false, true));
        Assert.False(AudioSettings.ExclusiveTook(Device(false, true), false, true));
        Assert.False(AudioSettings.ExclusiveTook(Device(true, true), false, false));
        Assert.False(AudioSettings.ExclusiveTook(Device(null, null), true, true)); // nothing read back
    }

    [Fact]
    public void FormatChoicesOnePerDepthAndRateKeepingTheCurrentContainer()
    {
        var supported = AudioFormatBlob.Candidates(2).Where(f => f.SampleRate is 44100 or 48000).ToList();
        var current = new AudioFormat(48000, 24, 32, 2);
        var choices = AudioSettings.FormatChoices(supported, current);
        Assert.Equal(6, choices.Count); // 16, 24 and 32 bit at two rates; 24-in-24 and 24-in-32 are one choice
        Assert.Contains(current, choices);
        Assert.DoesNotContain(new AudioFormat(48000, 24, 24, 2), choices);
        Assert.Contains(new AudioFormat(44100, 24, 24, 2), choices); // the first candidate where the current isn't involved
        Assert.Equal(choices.OrderBy(f => f.Bits).ThenBy(f => f.SampleRate), choices);
    }

    [Fact]
    public void FormatChoicesAlwaysHoldTheCurrentOne()
    {
        var current = new AudioFormat(32000, 16, 16, 2);
        Assert.Equal(new[] { current }, AudioSettings.FormatChoices(Array.Empty<AudioFormat>(), current));
        Assert.Empty(AudioSettings.FormatChoices(Array.Empty<AudioFormat>(), null));
        var list = AudioSettings.FormatChoices(new[] { new AudioFormat(48000, 16, 16, 2) }, current);
        Assert.Equal(new[] { current, new AudioFormat(48000, 16, 16, 2) }, list);
    }
}
