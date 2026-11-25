using KosToolbox.Core.Audio;
using Xunit;

namespace KosToolbox.Core.Tests;

public class MicMonitorTests
{
    [Fact]
    public void ThePeakIsTheLoudestSampleOfFloatAndPcm16Data()
    {
        var floats = new byte[12];
        BitConverter.GetBytes(0.25f).CopyTo(floats, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(floats, 4);
        BitConverter.GetBytes(0.1f).CopyTo(floats, 8);
        Assert.Equal(0.5f, MicMonitor.PeakOf(floats, 12, new MicMonitor.WaveFormat { BitsPerSample = 32 }));

        var pcm = new byte[6];
        BitConverter.GetBytes((short)1000).CopyTo(pcm, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(pcm, 2);
        BitConverter.GetBytes((short)5).CopyTo(pcm, 4);
        Assert.Equal(0.5f, MicMonitor.PeakOf(pcm, 6, new MicMonitor.WaveFormat { BitsPerSample = 16 }));

        BitConverter.GetBytes(3f).CopyTo(floats, 0); // clipped data still reads at most 1
        Assert.Equal(1f, MicMonitor.PeakOf(floats, 12, new MicMonitor.WaveFormat { BitsPerSample = 32 }));
        Assert.Equal(0f, MicMonitor.PeakOf(floats, 0, new MicMonitor.WaveFormat { BitsPerSample = 32 }));
    }
}
