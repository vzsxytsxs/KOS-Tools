using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using Xunit;

namespace KosToolbox.Core.Tests;

public class VbiosTests
{
    /// <summary>A 1 KB expansion ROM image: 55 AA, length in 512-byte blocks, PCIR at 0x20 with the vendor and device, sum fixed to 0.</summary>
    static byte[] Rom(ushort vendor = 0x10DE, ushort device = 0x2187, int offset = 0)
    {
        var rom = new byte[offset + 1024];
        rom[offset] = 0x55; rom[offset + 1] = 0xAA; rom[offset + 2] = 2;
        rom[offset + 0x18] = 0x20;
        "PCIR"u8.CopyTo(rom.AsSpan(offset + 0x20));
        BitConverter.GetBytes(vendor).CopyTo(rom, offset + 0x24);
        BitConverter.GetBytes(device).CopyTo(rom, offset + 0x26);
        byte sum = 0;
        for (var i = offset; i < offset + 1023; i++) sum += rom[i];
        rom[offset + 1023] = (byte)-sum;
        return rom;
    }

    [Fact]
    public void ReadsTheNvidiaImage()
    {
        Assert.Equal((ushort)0x2187, VbiosLoader.Inspect(Rom()).DeviceId);
        Assert.Null(VbiosLoader.Inspect(Rom()).Problem);
        // A dump with a header before the image (found at the next 512-byte boundary).
        Assert.Equal((ushort)0x1E84, VbiosLoader.Inspect(Rom(device: 0x1E84, offset: 1024)).DeviceId);
    }

    [Fact]
    public void RefusesWhatIsntOne()
    {
        var damaged = Rom();
        damaged[100] ^= 0xFF;
        Assert.Contains("checksum", VbiosLoader.Inspect(damaged).Problem);
        Assert.Contains("NVIDIA", VbiosLoader.Inspect(Rom(vendor: 0x1002)).Problem);
        Assert.NotNull(VbiosLoader.Inspect(new byte[2048]).Problem);
        Assert.NotNull(VbiosLoader.Inspect(new byte[VbiosLoader.MaxSize + 1]).Problem);
        Assert.NotNull(VbiosLoader.Inspect(Rom()[..600]).Problem); // cut short: the header says 1 KB
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2187&SUBSYS_00000000&REV_A1\4&1", (ushort)0x2187)]
    [InlineData(@"PCI\VEN_10DE&DEV_1e84", (ushort)0x1E84)]
    [InlineData(@"ROOT\DISPLAY\0000", null)]
    public void DeviceIdFromInstanceId(string id, ushort? expected) => Assert.Equal(expected, VbiosLoader.DeviceId(id));

    [Fact]
    public void ElevatedRequestNeedsAValidFile()
    {
        new ElevatedJob { Vbios = new VbiosJob() }.Validate(); // removal: nothing to check
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[64]);
            Assert.ThrowsAny<Exception>(() => new ElevatedJob { Vbios = new VbiosJob { Path = file } }.Validate());
        }
        finally { File.Delete(file); }
    }
}
