using System.Text.RegularExpressions;

namespace KosToolbox.Core.SystemInfo;

/// <summary>A memory timing; <see cref="FromSpd"/> = computed from the SPD, not read from the controller.</summary>
public sealed record DramTiming(string Code, string Name, string Value, bool FromSpd);

public sealed record DramTimingGroup(string Title, IReadOnlyList<DramTiming> Items);

/// <summary>
/// Timings in the ASRock Timing Configurator layout. Those CPU-Z reads from the memory controller are live; the others
/// are computed from the SPD (DDR4) using the JEDEC rules for the current speed, which is what the BIOS uses on Auto.
/// shortcut: the registers with all the timings (the controller's extended PCI space) need a kernel driver that
/// reads that space; replace the computation with a direct read if the toolbox gets such a driver.
/// </summary>
public static class DramTimings
{
    static readonly (string Group, string Code, string Name)[] Layout =
    {
        ("Primary", "tCL", "CAS# Latency"), ("Primary", "tRCD", "RAS# to CAS# Delay"), ("Primary", "tRP", "Row Precharge Time"),
        ("Primary", "tRAS", "RAS# Active Time"), ("Primary", "tRC", "Row Cycle Time"), ("Primary", "CR", "Command Rate"),
        ("Secondary", "tWR", "Write Recovery Time"), ("Secondary", "tRFC", "Refresh Cycle Time"), ("Secondary", "tRFC2", "Refresh Cycle Time (2x)"),
        ("Secondary", "tRFC4", "Refresh Cycle Time (4x)"), ("Secondary", "tRRD_S", "RAS to RAS Delay, other group"),
        ("Secondary", "tRRD_L", "RAS to RAS Delay, same group"), ("Secondary", "tWTR_S", "Write to Read Delay, other group"),
        ("Secondary", "tWTR_L", "Write to Read Delay, same group"), ("Secondary", "tRTP", "Read to Precharge"),
        ("Secondary", "tFAW", "Four Activate Window"), ("Secondary", "tCWL", "CAS Write Latency"),
        ("Tertiary", "tREFI", "Refresh Interval"), ("Tertiary", "tCKE", "CKE Minimum Pulse Width"),
        ("Tertiary", "tCCD_S", "CAS to CAS Delay, other group"), ("Tertiary", "tCCD_L", "CAS to CAS Delay, same group"),
        ("Tertiary", "tCCD_WR", "Write CAS to CAS, other group"), ("Tertiary", "tCCD_WR_L", "Write CAS to CAS, same group"),
    };

    /// <summary>A line of the CPU-Z Chipset section that is a timing (the others are clock, type, size, channels).</summary>
    public static bool IsTiming(string label) => Code(label) is not null;

    /// <param name="live">Lines of the CPU-Z Chipset section.</param>
    /// <param name="spds">SPD of each module.</param>
    /// <param name="mts">Configured rate (SMBIOS), used to compute the cycles.</param>
    public static List<DramTimingGroup> Build(IReadOnlyList<InfoRow> live, IEnumerable<byte[]> spds, uint? mts)
    {
        var values = new Dictionary<string, (string Value, bool Spd)>();
        var other = new List<DramTiming>();
        foreach (var r in live)
        {
            if (Code(r.Label) is not { } code) continue;
            var value = r.Value.EndsWith(".0") ? r.Value[..^2] : r.Value;
            if (Layout.Any(l => l.Code == code)) values[code] = (value, false);
            else other.Add(new DramTiming(code, r.Label, value, false)); // a timing the layout doesn't list: shown anyway
        }
        if (mts is > 0)
            foreach (var (code, ck) in FromSpd(spds, mts.Value)) values.TryAdd(code, (ck.ToString(), true));
        var groups = Layout.GroupBy(l => l.Group)
            .Select(g => new DramTimingGroup(g.Key, g.Where(l => values.ContainsKey(l.Code))
                .Select(l => new DramTiming(l.Code, l.Name, values[l.Code].Value, values[l.Code].Spd)).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        if (other.Count > 0) groups.Add(new DramTimingGroup("Other", other));
        return groups;
    }

    /// <summary>"CAS# latency (CL)" → tCL, "Command Rate (CR)" → CR, "tCCD" → tCCD_S; "Memory Frequency" → null.</summary>
    static string? Code(string label)
    {
        var m = Regex.Match(label, @"\(([A-Za-z][A-Za-z0-9_]*)\)\s*$");
        var code = m.Success ? m.Groups[1].Value : label.StartsWith('t') && !label.Contains(' ') ? label : null;
        return code switch { "CL" => "tCL", "tCCD" => "tCCD_S", _ => code };
    }

    /// <summary>Cycles from each module's SPD; the BIOS uses the slowest, so the largest of each timing is kept.</summary>
    internal static Dictionary<string, int> FromSpd(IEnumerable<byte[]> spds, uint mts)
    {
        var bin = Math.Round(mts / (400.0 / 3)) * (400.0 / 3); // DDR4 bins are multiples of 133.33 MT/s (2133 = 2133.33)
        var tck = 2_000_000 / bin; // ps
        var result = new Dictionary<string, int>();
        foreach (var spd in spds)
            foreach (var (code, ck) in Ddr4(spd, tck, bin))
                result[code] = Math.Max(result.GetValueOrDefault(code), ck);
        return result;
    }

    /// <summary>DDR4 (JEDEC 21-C Annex L): minimums in 125 ps MTB + fine adjustment in ps; the rest from JESD79-4.</summary>
    static Dictionary<string, int> Ddr4(byte[] s, double tck, double bin)
    {
        var d = new Dictionary<string, int>();
        if (s.Length < 128 || s[2] != 0x0C || (s[17] & 0x0F) != 0) return d; // DDR4 only, with MTB 125 ps / FTB 1 ps
        double Ps(int mtb, int fine = -1) => mtb * 125.0 + (fine < 0 ? 0 : (sbyte)s[fine]);
        int Ck(double ps, int min = 0) => Math.Max(min, (int)Math.Floor(ps / tck + 0.974)); // JEDEC rounding
        d["tCL"] = Ck(Ps(s[24], 123));
        d["tRCD"] = Ck(Ps(s[25], 122));
        d["tRP"] = Ck(Ps(s[26], 121));
        d["tRAS"] = Ck(Ps((s[27] & 0x0F) << 8 | s[28]));
        d["tRC"] = Ck(Ps((s[27] & 0xF0) << 4 | s[29], 120));
        d["tRFC"] = Ck(Ps(s[30] | s[31] << 8));
        d["tRFC2"] = Ck(Ps(s[32] | s[33] << 8));
        d["tRFC4"] = Ck(Ps(s[34] | s[35] << 8));
        d["tFAW"] = Ck(Ps((s[36] & 0x0F) << 8 | s[37]));
        d["tRRD_S"] = Ck(Ps(s[38], 119), 4);
        d["tRRD_L"] = Ck(Ps(s[39], 118), 4);
        d["tCCD_L"] = Ck(Ps(s[40], 117), 5);
        // tWR and tWTR came in a later SPD revision; zeroed = the fixed JESD79-4 values.
        var wr = (s[41] & 0x0F) << 8 | s[42];
        var wtrS = (s[43] & 0x0F) << 8 | s[44];
        var wtrL = (s[43] & 0xF0) << 4 | s[45];
        d["tWR"] = Ck(wr > 0 ? wr * 125 : 15000);
        d["tWTR_S"] = Ck(wtrS > 0 ? wtrS * 125 : 2500, 2);
        d["tWTR_L"] = Ck(wtrL > 0 ? wtrL * 125 : 7500, 4);
        d["tRTP"] = Ck(7500, 4);
        d["tCKE"] = Ck(5000, 3);
        d["tREFI"] = (int)Math.Round(7_800_000 / tck); // 7.8 µs (up to 85 °C)
        d["tCCD_S"] = 4;
        // CWL of the first set of each bin (1 tCK write preamble); above 3200 JEDEC doesn't define it.
        int? cwl = bin switch { <= 1601 => 9, <= 1867 => 10, <= 2134 => 11, <= 2401 => 12, <= 2667 => 14, <= 3201 => 16, _ => null };
        if (cwl is { } c) d["tCWL"] = c;
        return d;
    }
}
