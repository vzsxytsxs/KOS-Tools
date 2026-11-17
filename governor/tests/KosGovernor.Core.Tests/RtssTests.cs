using System.Buffers.Binary;
using System.Text;
using KosGovernor.Core;
using KosToolbox.Core.Gpu;
using Xunit;

namespace KosGovernor.Core.Tests;

public class RtssTests
{
    const int Offset = 64, EntrySize = 4608 + 16, Slots = 8;

    // An RTSS shared memory header (v2.14: extended text and the busy bit) with 8 OSD slots after it.
    static byte[] Memory(uint version = 0x0002000E, uint entrySize = EntrySize, uint offset = Offset, uint slots = Slots, int length = Offset + EntrySize * Slots)
    {
        var mem = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(mem, 0x52545353);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(20), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(24), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(28), slots);
        return mem;
    }

    static string Text(byte[] mem, int slot, int at, int size) => Encoding.Latin1.GetString(mem, Offset + slot * EntrySize + at, size).Split('\0')[0];
    static string Owner(byte[] mem, int slot) => Text(mem, slot, 256, 256);
    static uint Frame(byte[] mem) => BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(32));

    [Fact]
    public void ClaimsTheFirstFreeSlotAfterThePrimaryOneAndKeepsIt()
    {
        var mem = Memory();
        Encoding.ASCII.GetBytes("Other").CopyTo(mem, Offset + 1 * EntrySize + 256); // slot 1 is taken
        Assert.Equal(Rtss.OsdResult.Written, Rtss.Write(mem, "FPS <FR>\n60°C"));
        Assert.Equal("", Owner(mem, 0));
        Assert.Equal("Other", Owner(mem, 1));
        Assert.Equal("KOS Governor", Owner(mem, 2));
        Assert.Equal("FPS <FR>\n60°C", Text(mem, 2, 512, 4096)); // the extended text field, Latin-1
        Assert.Equal(1u, Frame(mem));
        Assert.Equal(0, mem[36] & 1); // busy bit released

        Assert.Equal(Rtss.OsdResult.Written, Rtss.Write(mem, "short"));
        Assert.Equal("short", Text(mem, 2, 512, 4096)); // same slot, old text cleared
        Assert.Equal("", Owner(mem, 3));
        Assert.Equal(2u, Frame(mem));

        Assert.Equal(1, Rtss.Release(mem));
        Assert.Equal("", Owner(mem, 2));
        Assert.Equal("Other", Owner(mem, 1)); // another program's slot stays
        Assert.Equal(0, Rtss.Release(mem));
    }

    [Fact]
    public void SkipsTheUpdateWhileRtssRenders()
    {
        var mem = Memory();
        mem[36] = 1; // RTSS holds the busy bit
        Assert.Equal(Rtss.OsdResult.Busy, Rtss.Write(mem, "x"));
        Assert.Equal("", Text(mem, 1, 512, 4096));
        Assert.Equal(1, mem[36]); // not ours to clear
    }

    [Fact]
    public void OldLayoutsUseTheShortTextField()
    {
        var mem = Memory(version: 0x00020005);
        Assert.Equal(Rtss.OsdResult.Written, Rtss.Write(mem, new string('a', 300)));
        Assert.Equal(new string('a', 255), Text(mem, 1, 0, 256)); // cut to the field, ending with a zero
        Assert.Equal("", Text(mem, 1, 512, 4096));
    }

    [Fact]
    public void RefusesAHeaderThatPointsOutsideTheMemory()
    {
        // Any program in the session can create the mapping first: none of these may write anywhere.
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(Memory(slots: 9), "x")); // one slot past the end
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(Memory(offset: 0xFFFFFF00), "x"));
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(Memory(entrySize: 100), "x")); // smaller than text + owner
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(Memory(slots: 0x40000000, entrySize: 0x40000000), "x"));
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(Memory(version: 0x00010002), "x"));
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(new byte[16], "x"));
        var noSignature = Memory();
        noSignature[0] = 0;
        Assert.Equal(Rtss.OsdResult.Invalid, Rtss.Write(noSignature, "x"));
        Assert.Equal(0, Rtss.Release(Memory(slots: 9)));
    }

    [Fact]
    public void NoFreeSlotWritesNothing()
    {
        var mem = Memory(slots: 2, length: Offset + EntrySize * 2);
        Encoding.ASCII.GetBytes("Other").CopyTo(mem, Offset + EntrySize + 256);
        Assert.Equal(Rtss.OsdResult.NoSlot, Rtss.Write(mem, "x"));
        Assert.Equal(0u, Frame(mem));
    }

    // RTSS's app array: entries of 0x3080 bytes (process id, name[260], flags) after the header.
    static byte[] Apps(uint entrySize = 0x3080, uint offset = Offset, uint count = 3, int length = Offset + 0x3080 * 3)
    {
        var mem = Memory(length: length);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(8), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(12), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(16), count);
        return mem;
    }

    [Fact]
    public void ProfileUpdateFlagsOnlyHookedApps()
    {
        var mem = Apps();
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(Offset), 1234);              // entry 0: a game
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(Offset + 0x3080 + 264), 5); // entry 1: free, stale flags
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(Offset + 2 * 0x3080), 99);   // entry 2: another game
        BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(Offset + 2 * 0x3080 + 264), 3);
        Assert.Equal(2, Rtss.RequestProfileUpdate(mem));
        Assert.Equal(0x10000000u, BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(Offset + 264)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(Offset + 0x3080 + 264)));
        Assert.Equal(0x10000003u, BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(Offset + 2 * 0x3080 + 264)));
    }

    [Fact]
    public void ProfileUpdateRefusesAnAppArrayOutsideTheMemory()
    {
        var full = new byte[Offset + 0x3080 * 3];
        Array.Fill(full, (byte)1); // every entry would count as a game
        foreach (var mem in new[] { Apps(count: 4), Apps(offset: 0xFFFFFF00), Apps(entrySize: 200), Apps(entrySize: 0x3081), Apps(offset: 66), Apps(count: 0x40000000, entrySize: 0x40000000) })
        {
            full.AsSpan(40).CopyTo(mem.AsSpan(40));
            var before = mem.ToArray();
            Assert.Equal(0, Rtss.RequestProfileUpdate(mem));
            Assert.Equal(before, mem);
        }
        Assert.Equal(0, Rtss.RequestProfileUpdate(new byte[16]));
    }

    [Fact]
    public void OsdTextShowsTheReadingsAndRtssTags()
    {
        var gpu = new GpuSensors { GpuLoad = 97, TemperatureC = 66, GraphicsMhz = 1935, PowerMw = 212_400, VramUsed = 6UL << 30, VramTotal = 8UL << 30 };
        Assert.Equal("FPS  <FR>  <FT> ms\nGPU  97%  66°C  1935 MHz  212 W  VRAM 6.0/8.0 GB\nCPU  41%  RAM 9.5/15.9 GB",
            Rtss.OsdText(41.2, (ulong)(9.5 * (1 << 30)), (ulong)(15.9 * (1 << 30)), gpu));
        Assert.Equal("FPS  <FR>  <FT> ms\nCPU  5%  RAM 1.0/2.0 GB", Rtss.OsdText(5, 1UL << 30, 2UL << 30, null));
    }

    [Fact]
    public void FrameLimitReadsAsRtssStoresIt()
    {
        Assert.Equal("off", new Rtss.FrameLimit(0, 1).ToString());
        Assert.Equal("141 FPS", new Rtss.FrameLimit(141, 1).ToString());
        Assert.Equal("59.94 FPS", new Rtss.FrameLimit(5994, 100).ToString());
    }
}
