using KosToolbox.Core.Audio;
using Xunit;

namespace KosToolbox.Core.Tests;

public class AudioFormatTests
{
    [Fact]
    public void BuildThenParseRoundTrips()
    {
        var f = new AudioFormat(96000, 24, 32, 2);
        var blob = AudioFormatBlob.Build(f, 0x3);
        Assert.Equal(40, blob.Length);
        Assert.Equal(f, AudioFormatBlob.Parse(blob));
        Assert.Equal(0x3u, AudioFormatBlob.ChannelMask(blob));
        Assert.Equal(96000 * 8, BitConverter.ToInt32(blob, 8));  // bytes per second: 2 channels x 4 bytes
        Assert.Equal(8, BitConverter.ToUInt16(blob, 12));        // block align
        Assert.Equal(AudioFormatBlob.Pcm, new Guid(blob.AsSpan(24, 16)));
        Assert.Equal(AudioFormatBlob.Float, new Guid(AudioFormatBlob.Build(f with { Bits = 32 }, 0x3, isFloat: true).AsSpan(24, 16)));
        Assert.Equal("24 bit, 96000 Hz", f.ToString());
    }

    [Fact]
    public void ParsesWhatWindowsStores()
    {
        // A Voicemeeter endpoint's PKEY_AudioEngine_DeviceFormat on the dev PC: extensible, stereo, 48 kHz, 24 in 24, PCM.
        var blob = Convert.FromHexString("FEFF020080BB0000808C0400060018001600180003000000" + "0100000000001000800000AA00389B71");
        Assert.Equal(new AudioFormat(48000, 24, 24, 2), AudioFormatBlob.Parse(blob));
        // Plain WAVEFORMATEX (16 bytes): no valid-bits field, the container is the depth.
        var plain = Convert.FromHexString("0100020044AC000010B10200040010000000");
        Assert.Equal(new AudioFormat(44100, 16, 16, 2), AudioFormatBlob.Parse(plain));
        Assert.Equal(0x3u, AudioFormatBlob.ChannelMask(plain));
        Assert.Null(AudioFormatBlob.Parse(new byte[8]));
        Assert.Null(AudioFormatBlob.Parse(new byte[40]));
    }

    [Fact]
    public void CandidatesCoverTheControlPanelChoices()
    {
        var all = AudioFormatBlob.Candidates(2).ToList();
        Assert.Contains(new AudioFormat(48000, 24, 24, 2), all);
        Assert.Contains(new AudioFormat(192000, 24, 32, 2), all);
        Assert.Contains(new AudioFormat(44100, 16, 16, 2), all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
