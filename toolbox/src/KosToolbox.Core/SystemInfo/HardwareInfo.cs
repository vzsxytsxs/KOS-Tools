using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

/// <summary>A memory module from SMBIOS (type 17), which the firmware fills in from the module's SPD.</summary>
public sealed record MemoryModule(string Slot, string? Bank, ulong SizeMb, string? Type, string? Buffering, string? FormFactor,
    uint? RatedMts, uint? ConfiguredMts, string? Manufacturer, string? PartNumber, string? Serial, int? Ranks,
    ushort? DataWidth, ushort? TotalWidth, ushort? MinMv, ushort? MaxMv, ushort? ConfiguredMv)
{
    public uint? SpeedMts => ConfiguredMts ?? RatedMts;
}

/// <summary>The board's memory arrays (SMBIOS type 16): slots, maximum capacity and error correction.</summary>
public sealed record MemoryBanks(int Slots, ulong? MaxBytes, string? ErrorCorrection);

public sealed record PhysicalCore(int Index, int Package, byte EfficiencyClass, IReadOnlyList<int> Logical);
public sealed record CacheLevel(int Level, int Type, uint SizeBytes, int Ways, int Count, int SharedBy);
public sealed record CpuTopology(int Packages, int NumaNodes, IReadOnlyList<PhysicalCore> Cores, IReadOnlyList<CacheLevel> Caches);

/// <summary>Fixed processor data for the Home details, already formatted.</summary>
public sealed record CpuDetails(IReadOnlyList<InfoRow> Processor, IReadOnlyList<InfoRow> Cache, IReadOnlyList<string> Instructions,
    IReadOnlyList<InfoRow> Platform, CpuTopology Topology);

/// <summary>Processor and memory from what Windows exposes without a driver: registry, CPUID, topology and SMBIOS.</summary>
public static class HardwareInfo
{
    // ---------- memory ----------

    public static IReadOnlyList<MemoryModule> Modules(IEnumerable<SmbiosStructure> table) => table.Select(Module).OfType<MemoryModule>().ToList();

    static MemoryModule? Module(SmbiosStructure s)
    {
        if (s.Type != 17 || s.Length < 0x15 || s.Word(0x0C) is not { } size || size is 0 or 0xFFFF) return null; // empty slot
        ulong mb = size == 0x7FFF ? (s.Dword(0x1C) ?? 0) & 0x7FFFFFFF : (size & 0x8000) != 0 ? (ulong)(size & 0x7FFF) / 1024 : size;
        uint? Speed(int word, int extended) => s.Word(word) switch
        {
            null or 0 => null,
            0xFFFF => s.Dword(extended) is { } e and > 0 ? e & 0x7FFFFFFF : null,
            { } v => v,
        };
        var detail = s.Word(0x13) ?? 0;
        var buffering = (detail & 0x8000) != 0 ? "LRDIMM" : (detail & 0x2000) != 0 ? "Registered" : (detail & 0x4000) != 0 ? "Unbuffered" : null;
        ushort? Mv(int o) => s.Word(o) is { } v and > 0 ? v : null;
        ushort? Width(int o) => s.Word(o) is { } v and not (0 or 0xFFFF) ? v : null;
        return new MemoryModule(s.Text(0x10) ?? "?", s.Text(0x11), mb, TypeName(s.Byte(0x12)), buffering, FormFactor(s.Byte(0x0E)),
            Speed(0x15, 0x54), s.Length >= 0x22 ? Speed(0x20, 0x58) : null,
            Clean(s.Text(0x17)), Clean(s.Text(0x1A)), Clean(s.Text(0x18)), s.Byte(0x1B) is { } a && (a & 0xF) > 0 ? a & 0xF : null,
            Width(0x0A), Width(0x08), Mv(0x22), Mv(0x24), Mv(0x26));
    }

    public static MemoryBanks? Banks(IEnumerable<SmbiosStructure> table)
    {
        var arrays = table.Where(s => s.Type == 16 && s.Byte(0x05) == 3).ToList(); // use 3 = system memory
        if (arrays.Count == 0) return null;
        ulong? max = 0;
        foreach (var a in arrays)
            max += a.Dword(0x07) is { } kb and not 0x80000000 ? kb * 1024UL : a.Qword(0x0F);
        var ecc = arrays[0].Byte(0x06) switch { 3 => "None", 4 => "Parity", 5 => "Single-bit ECC", 6 => "Multi-bit ECC", 7 => "CRC", _ => null };
        return new MemoryBanks(arrays.Sum(a => a.Word(0x0D) ?? 0), max is 0 ? null : max, ecc);
    }

    internal static string? TypeName(byte? t) => t switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2", 0x1D => "LPDDR3",
        0x1E => "LPDDR4", 0x20 => "HBM", 0x21 => "HBM2", 0x22 => "DDR5", 0x23 => "LPDDR5", 0x24 => "HBM3", _ => null,
    };

    static string? FormFactor(byte? f) => f switch
    {
        0x09 => "DIMM", 0x0B => "Soldered", 0x0D => "SO-DIMM", 0x0F => "FB-DIMM", 0x10 => "Die", 0x11 => "CAMM", _ => null,
    };

    /// <summary>Firmware fills fields with no data with "Unknown", "Not Specified" or zeros.</summary>
    static string? Clean(string? s) => s is null || s.All(c => c is '0' or ' ')
        || s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || s.Equals("Undefined", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Not Specified", StringComparison.OrdinalIgnoreCase) ? null : s;

    // ---------- processor ----------

    public static CpuDetails Cpu()
    {
        using var reg = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var table = Smbios.Read();
        var topology = Topology();
        var socket = table.FirstOrDefault(s => s.Type == 4);
        var logical = topology.Cores.Sum(c => c.Logical.Count);

        var rows = new List<InfoRow>();
        void Add(string label, string? value) { if (value is not null) rows.Add(new InfoRow(label, value)); }
        Add("Name", (reg?.GetValue("ProcessorNameString") as string)?.Trim());
        Add("Vendor", (reg?.GetValue("VendorIdentifier") as string) switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", var v => v });
        Add("Signature", Signature());
        // 4 bytes on newer builds; with 8, Intel stores the revision in the high dword.
        Add("Microcode", reg?.GetValue("Update Revision") is byte[] { Length: >= 4 } rev
            ? $"0x{(rev.Length >= 8 && BitConverter.ToUInt32(rev, 4) is var high and > 0 ? high : BitConverter.ToUInt32(rev, 0)):X}" : null);
        Add("Socket", socket?.Text(0x04));
        Add("Packages", topology.Packages.ToString());
        Add("Cores", Cores(topology));
        Add("Logical processors", logical.ToString());
        Add("Hyper-threading", logical > topology.Cores.Count ? $"On ({logical / Math.Max(1, topology.Cores.Count)} threads per core)" : "Off");
        // No "maximum speed": Windows reports only the nominal one (no turbo); the live speed already shows the turbo.
        Add("Base speed", reg?.GetValue("~MHz") is int mhz ? $"{mhz} MHz" : null);
        Add("Bus clock", socket?.Word(0x12) is { } bus and > 0 ? $"{bus} MHz" : null);
        Add("NUMA nodes", topology.NumaNodes > 0 ? topology.NumaNodes.ToString() : null);
        Add("Virtualization", IsProcessorFeaturePresent(21) ? "Enabled in firmware" : "Disabled in firmware"); // PF_VIRT_FIRMWARE_ENABLED

        var platform = new List<InfoRow>();
        if (table.FirstOrDefault(s => s.Type == 2) is { } board)
            platform.Add(new InfoRow("Motherboard", string.Join(" ", new[] { board.Text(0x04), board.Text(0x05) }.Where(t => t is not null))));
        if (table.FirstOrDefault(s => s.Type == 0) is { } bios)
            platform.Add(new InfoRow("BIOS", string.Join(" · ", new[] { bios.Text(0x04), bios.Text(0x05), bios.Text(0x08) }.Where(t => t is not null))));

        return new CpuDetails(rows, CacheRows(topology), Instructions(), platform, topology);
    }

    static string Cores(CpuTopology t)
    {
        var classes = t.Cores.Select(c => c.EfficiencyClass).Distinct().OrderByDescending(c => c).ToList();
        if (classes.Count < 2) return t.Cores.Count.ToString();
        // Hybrid: the highest class is the performance cores.
        return $"{t.Cores.Count} ({t.Cores.Count(c => c.EfficiencyClass == classes[0])} performance + {t.Cores.Count(c => c.EfficiencyClass != classes[0])} efficient)";
    }

    /// <summary>Family, model and stepping from CPUID leaf 1 (extended family and model applied); null without CPUID.</summary>
    internal static (int Family, int Model, int Stepping, int Eax)? CpuIdSignature()
    {
        if (!X86Base.IsSupported) return null;
        var eax = X86Base.CpuId(1, 0).Eax;
        int stepping = eax & 0xF, model = (eax >> 4) & 0xF, family = (eax >> 8) & 0xF;
        if (family == 0xF) family += (eax >> 20) & 0xFF;
        if (family is 6 or >= 0xF) model += ((eax >> 16) & 0xF) << 4;
        return (family, model, stepping, eax);
    }

    static string? Signature() =>
        CpuIdSignature() is { } s ? $"Family {s.Family:X} · Model {s.Model:X} · Stepping {s.Stepping} (CPUID {s.Eax:X})" : null;

    static List<InfoRow> CacheRows(CpuTopology t) => t.Caches.OrderBy(c => c.Level).ThenByDescending(c => c.Type)
        .Select(c => new InfoRow($"L{c.Level}{c.Type switch { 1 => " instruction", 2 => " data", 3 => " trace", _ => "" }}",
            (c.Count > 1 ? $"{c.Count} × " : "") + Size(c.SizeBytes) + (c.Ways is > 0 and < 255 ? $", {c.Ways}-way" : "")
            + (c.SharedBy > 1 && c.Count == 1 ? $", shared by {c.SharedBy} logical processors" : "")))
        .ToList();

    static string Size(uint bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes / 1024} KB";

    static List<string> Instructions()
    {
        var list = new List<string>();
        void Add(bool on, string name) { if (on) list.Add(name); }
        Add(X86Base.X64.IsSupported, "x86-64");
        Add(Sse.IsSupported, "SSE"); Add(Sse2.IsSupported, "SSE2"); Add(Sse3.IsSupported, "SSE3"); Add(Ssse3.IsSupported, "SSSE3");
        Add(Sse41.IsSupported, "SSE4.1"); Add(Sse42.IsSupported, "SSE4.2"); Add(Popcnt.IsSupported, "POPCNT");
        Add(Aes.IsSupported, "AES"); Add(Pclmulqdq.IsSupported, "CLMUL");
        Add(Avx.IsSupported, "AVX"); Add(Avx2.IsSupported, "AVX2"); Add(Fma.IsSupported, "FMA3");
        Add(Bmi1.IsSupported, "BMI1"); Add(Bmi2.IsSupported, "BMI2"); Add(Lzcnt.IsSupported, "LZCNT");
        Add(Avx512F.IsSupported, "AVX-512F"); Add(Avx512BW.IsSupported, "AVX-512BW"); Add(Avx512CD.IsSupported, "AVX-512CD");
        Add(Avx512DQ.IsSupported, "AVX-512DQ"); Add(Avx512Vbmi.IsSupported, "AVX-512VBMI");
        if (!X86Base.IsSupported) return list;
        var leaf1 = X86Base.CpuId(1, 0).Ecx;
        Add((leaf1 & (1 << 29)) != 0, "F16C");
        Add((leaf1 & (1 << 30)) != 0, "RDRAND");
        if (X86Base.CpuId(0, 0).Eax >= 7)
        {
            var leaf7 = X86Base.CpuId(7, 0).Ebx;
            Add((leaf7 & (1 << 18)) != 0, "RDSEED");
            Add((leaf7 & (1 << 29)) != 0, "SHA");
        }
        Add((leaf1 & (1 << 5)) != 0, "VT-x");
        Add((X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx & (1 << 2)) != 0, "AMD-V");
        return list;
    }

    static CpuTopology Topology()
    {
        GetLogicalProcessorInformationEx(0xFFFF, null, out var length); // RelationAll
        var buffer = new byte[Math.Max(0, length)];
        return length > 0 && GetLogicalProcessorInformationEx(0xFFFF, buffer, out length) ? ParseTopology(buffer) : new CpuTopology(1, 0, Array.Empty<PhysicalCore>(), Array.Empty<CacheLevel>());
    }

    /// <summary>
    /// SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX in sequence: cores (with the efficiency class), packages, NUMA and caches.
    /// shortcut: only group 0 (up to 64 logical), like the rest of Home; with more, read each GROUP_AFFINITY.
    /// </summary>
    internal static CpuTopology ParseTopology(byte[] b)
    {
        var cores = new List<(ulong Mask, byte Class)>();
        var packages = new List<ulong>();
        var caches = new List<(int Level, int Type, uint Size, int Ways, ulong Mask)>();
        var numa = 0;
        for (var o = 0; o + 8 <= b.Length;)
        {
            var relation = BitConverter.ToInt32(b, o);
            var size = BitConverter.ToInt32(b, o + 4);
            if (size < 8 || o + size > b.Length) break;
            switch (relation)
            {
                case 0 when size >= 48: cores.Add((BitConverter.ToUInt64(b, o + 32), b[o + 9])); break; // RelationProcessorCore
                case 3 when size >= 48: packages.Add(BitConverter.ToUInt64(b, o + 32)); break;      // RelationProcessorPackage
                case 1: numa++; break;                                                              // RelationNumaNode
                case 2 when size >= 56:                                                             // RelationCache
                    caches.Add((b[o + 8], BitConverter.ToInt32(b, o + 16), BitConverter.ToUInt32(b, o + 12), b[o + 9], BitConverter.ToUInt64(b, o + 40)));
                    break;
            }
            o += size;
        }
        var list = cores.Select((c, i) => new PhysicalCore(i, Math.Max(0, packages.FindIndex(p => (p & c.Mask) != 0)), c.Class,
            Enumerable.Range(0, 64).Where(bit => (c.Mask >> bit & 1) != 0).ToList())).ToList();
        var levels = caches.GroupBy(c => (c.Level, c.Type, c.Size, c.Ways))
            .Select(g => new CacheLevel(g.Key.Level, g.Key.Type, g.Key.Size, g.Key.Ways, g.Count(), BitOperations.PopCount(g.First().Mask))).ToList();
        return new CpuTopology(Math.Max(1, packages.Count), numa, list, levels);
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relation, byte[]? buffer, out int length);
    [DllImport("kernel32.dll")] static extern bool IsProcessorFeaturePresent(int feature);
}
