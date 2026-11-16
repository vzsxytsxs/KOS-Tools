using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosToolbox.Core.Tests;

// Everything that depends on Cpus.Count stays in this class (tests within a class don't run in parallel).
public class DeviceAndProcessTests
{
    public DeviceAndProcessTests() => Cpus.Count = 8;

    [Theory]
    [InlineData(4, null, true)]   // specified processors
    [InlineData(3, null, true)]
    [InlineData(0, null, false)]  // machine default
    [InlineData(null, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(null, new byte[] { 4, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(null, null, false)]
    public void GhostAffinityDetected(int? policy, byte[]? mask, bool set) => Assert.Equal(set, GhostDevices.AffinitySet(policy, mask));

    [Theory]
    [InlineData("0-3,6", 0b0100_1111UL)]
    [InlineData("7", 0b1000_0000UL)]
    [InlineData(" 1 , 2-2 ", 0b0110UL)]
    public void ParsesCpuLists(string text, ulong mask)
    {
        Assert.Equal(mask, Cpus.Parse(text, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3-1")]
    [InlineData("a")]
    [InlineData("1-2-3")]
    [InlineData("8")] // outside the 8 processors
    public void RejectsBadCpuLists(string text)
    {
        Assert.Null(Cpus.Parse(text, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void FormatsRanges() => Assert.Equal("0-3,6", Cpus.Format(0b0100_1111UL));

    [Fact]
    public void AffinityMaskHex()
    {
        Assert.Equal(3UL, Interrupts.MaskFromHex("0300000000000000"));
        Assert.Equal(0x0F0FUL, Interrupts.MaskFromHex("0F0F")); // tools that write fewer than 8 bytes
        Assert.Null(Interrupts.MaskFromHex(""));
        Assert.Null(Interrupts.MaskFromHex("000000000000000000"));
        Assert.Equal("0300000000000000", Interrupts.MaskToHex(3));
    }

    [Fact]
    public void InterruptValuesAreRangeChecked()
    {
        const string id = @"PCI\VEN_10DE&DEV_2206&SUBSYS_00000000&REV_A1\4&1234&0&0008";
        Interrupts.Validate(new InterruptConfig { InstanceId = id, Msi = 1, Limit = 16, Policy = 4, Priority = 3, Mask = 0b1100 });
        Interrupts.Validate(new InterruptConfig { InstanceId = id }); // everything absent = driver default

        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = @"USB\VID_1&PID_2\1", Msi = 1 }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id + @"\..\x" }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Msi = 2 }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Limit = 0 }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Policy = 6 }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Priority = 4 }));
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Policy = 4 }));            // no processors
        Assert.ThrowsAny<Exception>(() => Interrupts.Validate(new InterruptConfig { InstanceId = id, Policy = 4, Mask = 0x100 })); // CPU 8 doesn't exist
    }

    [Fact]
    public void ElevatedDeviceRequests()
    {
        new ElevatedJob { RemoveDevices = { @"USB\VID_046D&PID_C52B\5&2AB1&0&3" } }.Validate();
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { RemoveDevices = { @"USB\..\x" } }.Validate());
        // The elevated process only accepts interrupts of a connected PCI device.
        Assert.ThrowsAny<Exception>(() => new ElevatedJob { Interrupts = new InterruptConfig { InstanceId = @"PCI\VEN_FFFF&DEV_FFFF\0", Msi = 1 } }.Validate());
        Assert.ThrowsAny<Exception>(() => new ElevatedJob
        {
            RemoveDevices = { @"USB\VID_1&PID_2\1" },
            Interrupts = new InterruptConfig { InstanceId = @"PCI\VEN_FFFF&DEV_FFFF\0" },
        }.Validate());
    }
}
