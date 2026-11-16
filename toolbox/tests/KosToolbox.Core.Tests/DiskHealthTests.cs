using System.Buffers.Binary;
using KosToolbox.Core.Storage;
using Xunit;

namespace KosToolbox.Core.Tests;

// Values read from this PC's drives (Kingston NV-series NVMe and A400 SATA SSD) on 2026-11-15.
public class DiskHealthTests
{
    static string Row(DiskHealthReport r, string label) => r.Rows.Single(x => x.Label == label).Value;

    [Fact]
    public void ReadsTheNvmeHealthLog()
    {
        var log = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 306);
        log[3] = 100; log[4] = 10; log[5] = 72;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), 140_565_859);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(112), 4623);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 15213);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 1276);
        var r = DiskHealth.Nvme(log);
        Assert.False(r.Warning);
        Assert.Equal("Good", r.Summary);
        Assert.Equal("33 °C", Row(r, "Temperature"));
        Assert.Equal("71.97 TB", Row(r, "Data written"));
        Assert.Equal("15,213 h", Row(r, "Power-on hours").Replace('.', ','));
        Assert.Equal("72 % of the rated endurance used", Row(r, "Wear"));

        log[0] = 0b100; // reliability degraded
        Assert.Contains("reliability degraded", DiskHealth.Nvme(log).Summary);
        Assert.True(DiskHealth.Nvme(log).Warning);
    }

    static byte[] Smart(params (byte Id, byte Value, ulong Raw)[] attributes)
    {
        var data = new byte[512];
        for (var i = 0; i < attributes.Length; i++)
        {
            var o = 2 + i * 12;
            data[o] = attributes[i].Id;
            data[o + 3] = attributes[i].Value;
            data[o + 4] = attributes[i].Value;
            for (var b = 0; b < 6; b++) data[o + 5 + b] = (byte)(attributes[i].Raw >> (8 * b));
        }
        return data;
    }

    [Fact]
    public void ReadsTheAtaSmartTable()
    {
        var data = Smart((0x09, 100, 16841), (0x0C, 100, 6045), (0xC0, 100, 1647), (0xC2, 42, 81_609_293_866), (0xE7, 75, 75), (0x05, 100, 0));
        var r = DiskHealth.Ata(false, data);
        Assert.False(r.Warning);
        Assert.Equal("42 °C", Row(r, "Temperature"));
        Assert.Equal("75 % (the drive's estimate)", Row(r, "Life left"));
        Assert.Equal("1,647", Row(r, "Unsafe shutdowns").Replace('.', ','));
        Assert.Equal(6, r.Attributes.Count);
        Assert.StartsWith("C2 Temperature", r.Attributes[3].Label);
    }

    [Fact]
    public void BadSectorsAndTheDrivesOwnVerdictAreWarnings()
    {
        Assert.Contains("8 reallocated sectors", DiskHealth.Ata(false, Smart((0x05, 100, 8))).Summary);
        Assert.True(DiskHealth.Ata(true, Smart((0x09, 100, 1))).Warning);
        Assert.Contains("5 % life left", DiskHealth.Ata(false, Smart((0xE7, 5, 5))).Summary);
    }

    [Fact]
    public void ReadsTheBypassIoAnswer()
    {
        // As this PC's SATA drive answered: STATUS_NOT_SUPPORTED from storahci.sys.
        var o = new byte[352];
        BitConverter.GetBytes(0xC00000BBu).CopyTo(o, 24);
        BitConverter.GetBytes((ushort)12).CopyTo(o, 28);
        System.Text.Encoding.Unicode.GetBytes("storahci.sys").CopyTo(o, 30);
        BitConverter.GetBytes((ushort)22).CopyTo(o, 94);
        System.Text.Encoding.Unicode.GetBytes("Bypass IO not supported").CopyTo(o, 96);
        var s = BypassIo.Parse(o);
        Assert.False(s.Supported);
        Assert.Equal("storahci.sys", s.Driver);
        Assert.Equal("Bypass IO not supporte", s.Reason); // the length field wins over the text
        Assert.Equal(new BypassIoState(true, null, null), BypassIo.Parse(new byte[352]));
    }
}
