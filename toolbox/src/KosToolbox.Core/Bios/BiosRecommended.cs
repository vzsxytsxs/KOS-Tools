using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.SystemInfo;
using Microsoft.Win32;

namespace KosToolbox.Core.Bios;

/// <summary>Processor as the recommended lists need it: vendor string and CPUID family/model.</summary>
public sealed record BiosCpu(string? Vendor, int Family, int Model)
{
    public bool IsAmd => Vendor == "AuthenticAMD";
    public bool IsIntel => Vendor == "GenuineIntel";
    /// <summary>Zen 4 and later (Ryzen 7000/8000/9000): family 19h models 10h–1Fh and 60h up, and every family from 1Ah.</summary>
    public bool IsNewerRyzen => IsAmd && (Family >= 0x1A || Family == 0x19 && (Model is >= 0x10 and <= 0x1F || Model >= 0x60));

    public static BiosCpu Current()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var sig = HardwareInfo.CpuIdSignature();
        return new BiosCpu(k?.GetValue("VendorIdentifier") as string, sig?.Family ?? 0, sig?.Model ?? 0);
    }
}

/// <summary>
/// The value KOS recommends for one question of this BIOS. <see cref="Optional"/>: not marked by the Recommended button
/// (it has a drawback the note explains). <see cref="NewerRyzen"/>: the value is the one for Zen 4 and later, not the general one.
/// </summary>
public sealed record BiosRecommendation(string Value, string Label, string? Note, bool Optional, bool NewerRyzen);

/// <summary>
/// KOS recommended BIOS settings. Boards name the same setting differently ("EIST", "CPU EIST Function", "Enhanced Intel SpeedStep") and
/// number the options differently (Disabled is 0 on one BIOS and 1 on another), so the table keeps names (several per setting) and what the
/// value means (disabled, enabled, auto, an option's text, a number); the value to write is found in each BIOS's own option list. A setting
/// whose recommended option the BIOS doesn't offer gets no recommendation. Table: <see cref="Table"/>.
/// </summary>
public static partial class BiosRecommended
{
    enum Kind { Off, On, Auto, Labels, Number, MaxGen }

    sealed record Target(Kind Kind, string[] Labels, string Number)
    {
        public static readonly Target Off = new(Kind.Off, ["Disabled", "Disable", "Off"], ""), On = new(Kind.On, ["Enabled", "Enable", "On"], ""),
            Auto = new(Kind.Auto, ["Auto"], "");
    }

    /// <summary>One line of the table: <paramref name="Vendor"/> is "A" (AMD), "I" (Intel) or "*" (both).</summary>
    sealed record Row(char Vendor, Target Target, Target? NewerRyzen, bool Optional, string[] Names, string? Note);

    sealed record NameIndex(Dictionary<string, Row> ByName, List<(string Prefix, Row Row)> Prefixes);

    static readonly Lazy<(NameIndex Amd, NameIndex Intel)> Indexes = new(() => Build(Parse(Table)));
    static readonly Regex Watts = new(@"\[w\]|\(w\)|\bwatts?\b", RegexOptions.IgnoreCase);

    // ---------- names ----------

    /// <summary>
    /// Name without case, punctuation, "(R)"/"(TM)" and plurals ("Global C-States Control" = "Global C-state Control"),
    /// so a name differs only when the words do.
    /// </summary>
    internal static string Normalize(string name)
    {
        var s = Regex.Replace(name.Replace("®", "").Replace("™", ""), @"\((r|tm)\)", "", RegexOptions.IgnoreCase);
        var sb = new StringBuilder();
        foreach (var t in Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+"))
            sb.Append(t.Length > 3 && t.EndsWith('s') && !t.EndsWith("ss") ? t[..^1] : t);
        return sb.ToString();
    }

    // ---------- table ----------

    static List<Row> Parse(string table)
    {
        var rows = new List<Row>();
        foreach (var raw in table.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('|', 4);
            if (f.Length < 3 || f[0] is not ("A" or "I" or "*")) throw new FormatException($"Invalid BIOS recommendation line: {line}");
            var spec = f[1].Trim();
            var optional = spec.Contains('?');
            var parts = spec.Replace("?", "").Split('>');
            var names = f[2].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0 || parts.Length > 2) throw new FormatException($"Invalid BIOS recommendation line: {line}");
            rows.Add(new Row(f[0][0], ParseTarget(parts[0]), parts.Length == 2 ? ParseTarget(parts[1]) : null, optional, names, f.Length > 3 ? f[3].Trim() : null));
        }
        return rows;
    }

    static Target ParseTarget(string s)
    {
        s = s.Trim();
        return s switch
        {
            "off" => Target.Off,
            "on" => Target.On,
            "auto" => Target.Auto,
            "maxgen" => new Target(Kind.MaxGen, [], ""),
            _ when s.StartsWith('=') => new Target(Kind.Labels, s[1..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), ""),
            _ when s.StartsWith('#') && long.TryParse(s[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) => new Target(Kind.Number, [], n.ToString(CultureInfo.InvariantCulture)),
            _ => throw new FormatException($"Invalid BIOS recommendation value: {s}"),
        };
    }

    static (NameIndex Amd, NameIndex Intel) Build(List<Row> rows)
    {
        var amd = new NameIndex(new(), new());
        var intel = new NameIndex(new(), new());
        foreach (var row in rows)
            foreach (var name in row.Names)
            {
                var prefix = name.EndsWith('*');
                var key = Normalize(prefix ? name[..^1] : name);
                foreach (var index in new NameIndex?[] { row.Vendor != 'I' ? amd : null, row.Vendor != 'A' ? intel : null })
                {
                    if (index is null) continue;
                    if (prefix) index.Prefixes.Add((key, row));
                    else index.ByName.TryAdd(key, row);
                }
            }
        return (amd, intel);
    }

    /// <summary>(vendor, normalized name) of every table name that appears twice for the same vendor, for the tests.</summary>
    internal static IEnumerable<string> DuplicateNames() =>
        Parse(Table).SelectMany(r => r.Names.Select(n => (Row: r, Key: Normalize(n.TrimEnd('*')))))
            .SelectMany(x => (x.Row.Vendor == '*' ? new[] { 'A', 'I' } : new[] { x.Row.Vendor }).Select(v => $"{v}:{x.Key}"))
            .GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key);

    // ---------- resolution ----------

    /// <summary>
    /// The recommendations that apply to <paramref name="script"/> on this processor, by question key. Intel and AMD have their own lists;
    /// another vendor (a virtual machine, say) has none.
    /// </summary>
    public static Dictionary<string, BiosRecommendation> For(NvramScript script, BiosCpu cpu)
    {
        var result = new Dictionary<string, BiosRecommendation>();
        if (!cpu.IsAmd && !cpu.IsIntel) return result;
        var index = cpu.IsAmd ? Indexes.Value.Amd : Indexes.Value.Intel;
        foreach (var q in script.Questions.Where(q => q.Editable))
        {
            var key = Normalize(q.Name);
            if (!index.ByName.TryGetValue(key, out var row)) row = index.Prefixes.FirstOrDefault(p => key.StartsWith(p.Prefix, StringComparison.Ordinal)).Row;
            if (row is null) continue;
            var newer = row.NewerRyzen is not null && cpu.IsNewerRyzen;
            var target = newer ? row.NewerRyzen! : row.Target;
            if (Resolve(q, target) is { } value && NvramScript.Check(q, value) is null)
                result[q.Key] = new BiosRecommendation(value, NvramScript.Describe(q, value), row.Note, row.Optional, newer);
        }
        return result;
    }

    /// <summary>The value (in the script's format) that means <paramref name="t"/> in this question; null if the BIOS offers nothing like it.</summary>
    static string? Resolve(BiosQuestion q, Target t)
    {
        switch (q.Kind)
        {
            case BiosKind.Checkbox:
                return t.Kind switch
                {
                    Kind.Off => "0",
                    Kind.On => "1",
                    Kind.Labels => t.Labels.Select(l => BoolWord(l)).FirstOrDefault(v => v is not null),
                    _ => null,
                };
            case BiosKind.Numeric:
                // The table's numbers are watts; some AMD menus take milliwatts, so the BIOS has to say it's watts.
                return t.Kind == Kind.Number && Watts.IsMatch(q.Name + " " + q.Help) ? t.Number : null;
            case BiosKind.Options:
                if (t.Kind == Kind.MaxGen) return MaxGen(q);
                var labels = t.Kind == Kind.Number ? new[] { t.Number, t.Number + "W" } : t.Labels;
                foreach (var l in labels)
                {
                    var loose = l.StartsWith('~');
                    var want = Normalize(loose ? l[1..] : l);
                    var hit = q.Options.FirstOrDefault(o => Normalize(o.Label) is var have && (loose ? have.Contains(want, StringComparison.Ordinal) : have == want));
                    if (hit is not null) return hit.Value;
                }
                return null;
            default:
                return null;
        }
    }

    static string? BoolWord(string label) => Normalize(label) switch
    {
        "enabled" or "enable" or "on" or "yes" or "1" => "1",
        "disabled" or "disable" or "off" or "no" or "0" => "0",
        _ => null,
    };

    /// <summary>The fastest generation of a "Gen1 … GenN" list.</summary>
    static string? MaxGen(BiosQuestion q) => q.Options
        .Select(o => (o.Value, Gen: Regex.Match(o.Label, @"^\s*gen\s*(\d+)\s*$", RegexOptions.IgnoreCase)))
        .Where(x => x.Gen.Success).OrderByDescending(x => int.Parse(x.Gen.Groups[1].Value, CultureInfo.InvariantCulture))
        .Select(x => x.Value).FirstOrDefault();
}
