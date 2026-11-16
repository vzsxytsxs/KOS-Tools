using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Tools;
using Xunit;

namespace KosToolbox.Core.Tests;

public class HardwareInfoTests
{
    [Fact]
    public void ReadsModuleStringsFromSmbios()
    {
        var s = new byte[0x28];
        s[0] = 17;
        s[1] = 0x28;
        BitConverter.GetBytes((ushort)8192).CopyTo(s, 0x0C);
        s[0x10] = 1;    // Device Locator → string 1
        s[0x12] = 0x1A; // DDR4
        s[0x17] = 2;    // Manufacturer → string 2 ("Undefined" is discarded)
        s[0x1A] = 3;    // Part Number → string 3
        s[0x1B] = 2;    // 2 ranks
        BitConverter.GetBytes((ushort)1200).CopyTo(s, 0x26);
        var strings = System.Text.Encoding.ASCII.GetBytes("DIMM_B1\0Undefined\0VMA47UG\0\0");
        var table = s.Concat(strings).Concat(new byte[] { 127, 4, 0, 0, 0, 0 }).ToArray();
        var raw = new byte[] { 0, 3, 4, 0 }.Concat(BitConverter.GetBytes(table.Length)).Concat(table).ToArray();

        var m = Assert.Single(HardwareInfo.Modules(Smbios.Parse(raw)));
        Assert.Equal("DIMM_B1", m.Slot);
        Assert.Null(m.Manufacturer);
        Assert.Equal("VMA47UG", m.PartNumber);
        Assert.Equal(8192UL, m.SizeMb);
        Assert.Equal("DDR4", m.Type);
        Assert.Equal(2, m.Ranks);
        Assert.Equal((ushort)1200, m.ConfiguredMv);
    }

    [Fact]
    public void GroupsLogicalProcessorsIntoCores()
    {
        // Two cores with SMT (CPUs 0-1 and 2-3) in one package, and an L3 shared by all four.
        static byte[] Processor(int relation, ulong mask, byte efficiency)
        {
            var b = new byte[48];
            BitConverter.GetBytes(relation).CopyTo(b, 0);
            BitConverter.GetBytes(48).CopyTo(b, 4);
            b[9] = efficiency;
            BitConverter.GetBytes((ushort)1).CopyTo(b, 30);
            BitConverter.GetBytes(mask).CopyTo(b, 32);
            return b;
        }
        var cache = new byte[56];
        BitConverter.GetBytes(2).CopyTo(cache, 0);
        BitConverter.GetBytes(56).CopyTo(cache, 4);
        cache[8] = 3;
        cache[9] = 16;
        BitConverter.GetBytes(8u << 20).CopyTo(cache, 12);
        BitConverter.GetBytes(0xFUL).CopyTo(cache, 40);
        var data = Processor(0, 0b0011, 0).Concat(Processor(0, 0b1100, 0)).Concat(Processor(3, 0b1111, 0)).Concat(cache).ToArray();

        var t = HardwareInfo.ParseTopology(data);
        Assert.Equal(1, t.Packages);
        Assert.Equal(new[] { 0, 1 }, t.Cores[0].Logical);
        Assert.Equal(new[] { 2, 3 }, t.Cores[1].Logical);
        var l3 = Assert.Single(t.Caches);
        Assert.Equal(4, l3.SharedBy);
        Assert.Equal(8u << 20, l3.SizeBytes);
    }

    [Fact]
    public void ExplainsPostSetupWarnings()
    {
        var lines = new[]
        {
            "[Sun 09/27/2026 19:41:23.09] event=runonce status=START native_exit=0 kos_code= detail=Post-setup started",
            "[Sun 09/27/2026 19:42:09.62] event=lightshot status=WARN native_exit=1 kos_code=205 detail=Lightshot installer",
            "[Sun 09/27/2026 19:44:09.14] event=reboot-request status=FAIL native_exit=5 kos_code=503 detail=Windows rejected restart request",
        };
        var r = PostSetupLog.Parse("COMPLETED_WITH_WARNINGS warnings=1 errors=1", lines, @"C:\x\RunOnce-20260927-194122-998");

        Assert.Equal("COMPLETED_WITH_WARNINGS", r.Status);
        Assert.Equal((1, 1), (r.Warnings, r.Errors));
        Assert.Equal(new DateTime(2026, 9, 27, 19, 41, 22), r.Started);
        Assert.Equal(2, r.Issues.Count);
        Assert.False(r.Issues[0].IsError);
        Assert.Contains("Lightshot installer", r.Issues[0].Explanation);
        Assert.Contains("Exit code 1", r.Issues[0].Explanation);
        Assert.True(r.Issues[1].IsError);
        Assert.Contains("restart request", r.Issues[1].Explanation);
        Assert.True(r.HasProblems);
    }

    [Fact]
    public void CalculatesTheMissingTimingsFromDdr4Spd()
    {
        // SPD of a DDR4-2133 module (VMA47UG) and what CPU-Z read from the controller: 15-15-15-35, tRFC 374, 2T.
        var spd = new byte[128];
        new byte[] { 0x23, 0x11, 0x0C, 0x02, 0x85, 0x21, 0, 0, 0, 0, 0, 0x03, 0x01, 0x03, 0, 0,
                     0, 0, 0x08, 0x0C, 0x80, 0x03, 0, 0, 0x71, 0x71, 0x71, 0x11, 0x06, 0x79, 0xF0, 0x0A,
                     0x20, 0x08, 0, 0x05, 0, 0xA8, 0x1E, 0x2B, 0x2B }.CopyTo(spd, 0);
        new byte[] { 0x83, 0xB5, 0xCE, 0xC0, 0xC0, 0xC0, 0xC0, 0x00, 0xC2, 0x51, 0x19 }.CopyTo(spd, 0x75);
        var fromSpd = DramTimings.FromSpd(new[] { spd }, 2133);
        var expected = new Dictionary<string, int>
        {
            ["tCL"] = 15, ["tRCD"] = 15, ["tRP"] = 15, ["tRAS"] = 35, ["tRC"] = 51, ["tRFC"] = 374, ["tRFC2"] = 278, ["tRFC4"] = 171,
            ["tFAW"] = 23, ["tRRD_S"] = 4, ["tRRD_L"] = 6, ["tCCD_L"] = 6, ["tWR"] = 16, ["tWTR_S"] = 3, ["tWTR_L"] = 8, ["tRTP"] = 8,
            ["tCKE"] = 6, ["tREFI"] = 8320, ["tCWL"] = 11, ["tCCD_S"] = 4,
        };
        foreach (var (code, ck) in expected) Assert.True(fromSpd[code] == ck, $"{code}: {fromSpd[code]}, expected {ck}");

        var live = new[] { new InfoRow("Memory Frequency", "1064.8 MHz (1:16)"), new InfoRow("CAS# latency (CL)", "15.0"), new InfoRow("Command Rate (CR)", "2T"), new InfoRow("tRDRD_sg", "6") };
        var groups = DramTimings.Build(live, new[] { spd }, 2133);
        var all = groups.SelectMany(g => g.Items).ToList();
        Assert.Equal(("15", false), (all.Single(t => t.Code == "tCL").Value, all.Single(t => t.Code == "tCL").FromSpd)); // the live reading beats the SPD
        Assert.True(all.Single(t => t.Code == "tWR").FromSpd);
        Assert.Equal("Other", groups[^1].Title); // a timing outside the layout doesn't disappear
        Assert.False(DramTimings.IsTiming("Memory Frequency"));
    }

    [Fact]
    public void ReadsTimingsAndSpdFromTheCpuzReport()
    {
        var lines = new[]
        {
            "Chipset", "-------------------------------------------------------------------------", "",
            "Northbridge\t\t\tIntel Haswell-E rev. 02",
            "Memory Type\t\t\tDDR4",
            "Memory Frequency\t\t1064.8 MHz (1:16)",
            "CAS# latency (CL)\t\t15.0",
            "Command Rate (CR)\t\t2T",
            "Host Bridge\t\t\t0x2F00", "", "",
            "Memory SPD", "-------------------------------------------------------------------------", "",
            "DIMM #\t\t\t\t1",
            "\tSMBus address\t\t0x54",
            "\tSize\t\t\t8192 MBytes",
            "\tMax bandwidth\t\tDDR4-2133 (1066 MHz)",
            "\tPart number\t\t",
            "JEDEC timings table\t\tCL-tRCD-tRP-tRAS-tRC @ frequency",
            "\tJEDEC #1\t\t14.0-15-15-33-48 @ 1000 MHz", "",
            "DIMM #\t\t\t\t1",
            "SPD registers\t",
            "\t\t00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F ", "",
            "\t00\t23 11 0C 02 85 21 00 00 00 00 00 03 01 03 00 00 ",
            "\t1F0\t00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ", "",
            "Monitoring", "-------------------------------------------------------------------------",
        };
        var m = Cpuz.Parse(lines);

        Assert.Equal("DDR4", m.Timings[0].Value); // starts at Memory Type (without the northbridge)
        Assert.Contains(m.Timings, r => r.Label == "Data rate" && r.Value == "2130 MT/s");
        Assert.Contains(m.Timings, r => r.Label == "CAS latency in ns" && r.Value == "14.09 ns");
        Assert.DoesNotContain(m.Timings, r => r.Label == "Host Bridge");
        var dimm = Assert.Single(m.Dimms);
        Assert.Equal("DIMM 1 · 8192 MBytes · DDR4-2133 (1066 MHz)", dimm.Title);
        Assert.Contains(dimm.Rows, r => r.Label == "JEDEC #1" && r.Value == "14.0-15-15-33-48 @ 1000 MHz");
        Assert.Contains(dimm.Rows, r => r.Label == "Part number" && r.Value == "—");
        Assert.Equal("000: 23 11 0C 02 85 21 00 00 00 00 00 03 01 03 00 00\n1F0: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
            dimm.Registers!.Replace("\r\n", "\n"));
        Assert.Equal(32, dimm.Spd.Length);
        Assert.Equal(0x0C, dimm.Spd[2]);
    }
}
