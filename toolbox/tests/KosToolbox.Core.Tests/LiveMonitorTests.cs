using KosToolbox.Core.SystemInfo;
using Xunit;

namespace KosToolbox.Core.Tests;

public class LiveMonitorTests
{
    /// <summary>SMBIOS type 17 structure (0x28 bytes) followed by the end of the strings.</summary>
    static IEnumerable<byte> MemoryDevice(ushort sizeMb, byte type, ushort speed, ushort configured)
    {
        var s = new byte[0x28];
        s[0] = 17;
        s[1] = 0x28;
        BitConverter.GetBytes(sizeMb).CopyTo(s, 0x0C);
        s[0x12] = type;
        BitConverter.GetBytes(speed).CopyTo(s, 0x15);
        BitConverter.GetBytes(configured).CopyTo(s, 0x20);
        return s.Concat(new byte[] { 0, 0 });
    }

    [Fact]
    public void ReadsInstalledMemoryModules()
    {
        var table = MemoryDevice(8192, 0x1A, 2400, 2133)
            .Concat(MemoryDevice(0, 0x02, 0, 0))          // empty slot: doesn't count
            .Concat(MemoryDevice(8192, 0x1A, 2400, 0))     // no configured speed: uses the maximum
            .Concat(new byte[] { 127, 4, 0, 0, 0, 0 }).ToArray();
        var raw = new byte[] { 0, 3, 4, 0 }.Concat(BitConverter.GetBytes(table.Length)).Concat(table).ToArray();

        var (modules, speed, type) = LiveMonitor.ParseSmbiosMemory(raw);
        Assert.Equal(2, modules);
        Assert.Equal(2400u, speed);
        Assert.Equal("DDR4", type);
    }

    [Fact]
    public void IgnoresTruncatedTables() => Assert.Equal((0, null, null), LiveMonitor.ParseSmbiosMemory(new byte[] { 0, 3, 4, 0, 50, 0, 0, 0, 17, 0x28 }));
}
