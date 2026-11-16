using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Bios;

/// <summary>How the toolbox handles the question: option list, number, checkbox, or display only (text, time, date, boot order).</summary>
public enum BiosKind { Options, Numeric, Checkbox, ReadOnly }

/// <summary>Option of a list: the hex value as it is in the script ("00", "FF") and the text.</summary>
public sealed record BiosOption(string Value, string Label);

/// <summary>
/// Active question of the script ("Setup Question"). <see cref="Current"/> and <see cref="Default"/> are in the script's
/// format: the option hex, the decimal of &lt;n&gt;, the checkbox digit; null when the script does not provide it.
/// </summary>
public sealed record BiosQuestion(string Name, string? Help, string Token, string Offset, string Width, BiosKind Kind,
    IReadOnlyList<BiosOption> Options, string? Current, string? Default, string? Raw)
{
    /// <summary>SCEWIN identifies the question by Token; Offset and Width disambiguate a repeated Token.</summary>
    public string Key => $"{Token}/{Offset}/{Width}";
    public bool Editable => Kind != BiosKind.ReadOnly;
    /// <summary>Lines of the block in the script and, for a list, the line of each option (in the order of <see cref="Options"/>).</summary>
    internal int First { get; init; }
    internal int Last { get; init; }
    internal int ValueLine { get; init; }
    internal IReadOnlyList<int> OptionLines { get; init; } = Array.Empty<int>();
}

/// <summary>Question whose value differs between two scripts. Missing one side = the question only exists in the other.</summary>
public sealed record BiosDiff(BiosQuestion? Old, BiosQuestion? New)
{
    public string Name => (Old ?? New)!.Name;
    public string Key => (Old ?? New)!.Key;
    /// <summary>Can go back to the old value: the question exists in both, has the same type and the old value is valid today.</summary>
    public bool Restorable => Old?.Current is { } v && New is not null && New.Kind == Old.Kind && NvramScript.Check(New, v) is null;
}

/// <summary>
/// AMI SCEWIN NVRAM script (nvram.txt): reading the questions and building the import script. The formats
/// are the ones SCEWIN 5.05 writes: options "[%02X]text" with "*" on the current one, number "&lt;%d&gt;" in decimal, checkbox
/// "%X // Enabled = 1, Disabled = 0". The file is read and written byte by byte, so that what does not change comes back identical.
/// </summary>
public sealed class NvramScript
{
    /// <summary>shortcut: Latin-1 preserves the bytes, but shows 0x80–0x9F (™, …) wrong; switch to code page 1252 if any BIOS uses it.</summary>
    public static readonly Encoding Encoding = Encoding.Latin1;

    static readonly Regex CrcLine = new(@"^HIICrc32\s*=\s*([0-9A-Fa-f]{1,8})\s*$");
    static readonly Regex VersionText = new(@"Utility\. Ver (\S+)");
    static readonly Regex LeadingHex = new(@"^\s*([0-9A-Fa-f]{1,16})");
    static readonly Regex OptionText = new(@"^\s*(\*?)\s*\[([0-9A-Fa-f]{1,16})\](.*)$");
    static readonly Regex NumberText = new(@"^\s*<(-?\d{1,20})>");
    static readonly Regex CheckboxText = new(@"^\s*([0-9A-Fa-f]{1,16})\s*//\s*Enabled = 1, Disabled = 0");
    const string MoveComment = "// Move \"*\" to the desired Option";

    readonly string[] _lines; // without the '\n'; the '\r' (sometimes two) stays at the end of the line
    readonly int _crcLine;
    readonly Dictionary<string, BiosQuestion> _byKey;

    NvramScript(string[] lines, string? crc, int crcLine, string? version, List<BiosQuestion> questions)
    {
        _lines = lines;
        _crcLine = crcLine;
        Crc = crc;
        Version = version;
        Questions = questions;
        _byKey = questions.ToDictionary(q => q.Key);
    }

    /// <summary>HIICrc32 from the header: SCEWIN refuses to import a script from another BIOS layout.</summary>
    public string? Crc { get; }
    /// <summary>Version of the SCEWIN that exported ("5.05.01.0002").</summary>
    public string? Version { get; }
    /// <summary>Active questions, in file order. Duplicates that SCEWIN itself comments out with "//" are left out.</summary>
    public IReadOnlyList<BiosQuestion> Questions { get; }

    public BiosQuestion? Find(string key) => _byKey.GetValueOrDefault(key);

    public static NvramScript Load(string path) => Parse(File.ReadAllText(path, Encoding));

    public static NvramScript Parse(string text)
    {
        var lines = text.Split('\n');
        string? crc = null, version = null;
        var crcLine = -1;
        var questions = new List<BiosQuestion>();
        var keys = new HashSet<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd('\r');
            if (crcLine < 0 && CrcLine.Match(t) is { Success: true } c) { crc = c.Groups[1].Value.ToUpperInvariant(); crcLine = i; continue; }
            if (version is null && t.StartsWith("//", StringComparison.Ordinal) && VersionText.Match(t) is { Success: true } v) version = v.Groups[1].Value;
            if (!IsQuestion(t)) continue;
            // The block runs to the next question or comment ("// FORM", commented duplicates); blank lines in the middle belong to the block.
            var last = i;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var tj = lines[j].TrimEnd('\r');
                if (tj.StartsWith("//", StringComparison.Ordinal) || IsQuestion(tj)) break;
                if (tj.Trim().Length > 0) last = j;
            }
            if (Block(lines, i, last) is { } q && keys.Add(q.Key)) questions.Add(q);
            i = last;
        }
        return new NvramScript(lines, crc, crcLine, version, questions);
    }

    static bool IsQuestion(string t) => t.StartsWith("Setup Question", StringComparison.Ordinal) && t.AsSpan(14).TrimStart().StartsWith("=");

    static BiosQuestion? Block(string[] lines, int first, int last)
    {
        var fields = new Dictionary<string, (int Line, string Value)>(StringComparer.Ordinal);
        var optionLines = new List<int>();
        string? field = null;
        for (var k = first; k <= last; k++)
        {
            var t = lines[k].TrimEnd('\r');
            if (t.Trim().Length == 0) continue;
            // Following options of the list: indented lines right after "Options".
            if (char.IsWhiteSpace(t[0])) { if (field == "Options" && OptionText.IsMatch(t)) optionLines.Add(k); continue; }
            var eq = t.IndexOf('=');
            if (eq <= 0) { field = null; continue; } // continuation of a text with a line break
            field = t[..eq].Trim();
            if (fields.TryAdd(field, (k, t[(eq + 1)..])) && field == "Options") optionLines.Add(k);
        }
        string? Get(string name) => fields.TryGetValue(name, out var f) ? f.Value.Trim() : null;
        string? Hex(string name) => Get(name) is { } s && LeadingHex.Match(s) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

        var name = Get("Setup Question");
        var (token, offset, width) = (Hex("Token"), Hex("Offset"), Hex("Width"));
        if (string.IsNullOrEmpty(name) || token is null || offset is null || width is null) return null;
        var help = Get("Help String") is { Length: > 0 } h ? h : null;

        var kind = BiosKind.ReadOnly;
        var options = new List<BiosOption>();
        var optLines = new List<int>();
        string? current = null, raw = null;
        var valueLine = -1;
        if (fields.TryGetValue("Options", out var opt))
        {
            valueLine = opt.Line;
            var starred = new List<BiosOption>();
            foreach (var k in optionLines)
            {
                if (OptionText.Match(k == opt.Line ? opt.Value : lines[k].TrimEnd('\r')) is not { Success: true } m) continue;
                var label = m.Groups[3].Value;
                if (label.IndexOf(MoveComment, StringComparison.Ordinal) is var c and >= 0) label = label[..c];
                var o = new BiosOption(m.Groups[2].Value.ToUpperInvariant(), label.Trim());
                options.Add(o);
                optLines.Add(k);
                if (m.Groups[1].Value == "*") starred.Add(o);
            }
            // More than one "*" is not a single-choice list: display only.
            if (options.Count > 0 && starred.Count <= 1) { kind = BiosKind.Options; current = starred.FirstOrDefault()?.Value; }
            else raw = string.Join(", ", starred.Select(s => s.Label));
        }
        else if (fields.TryGetValue("Value", out var val))
        {
            valueLine = val.Line;
            if (NumberText.Match(val.Value) is { Success: true } n && TryNumber(n.Groups[1].Value, out var number))
                (kind, current) = (BiosKind.Numeric, number.ToString(CultureInfo.InvariantCulture));
            else if (CheckboxText.Match(val.Value) is { Success: true } cb && TryHex(cb.Groups[1].Value, out var bit))
                (kind, current) = (BiosKind.Checkbox, bit.ToString("X", CultureInfo.InvariantCulture));
            else raw = WithoutComment(val.Value);
        }
        else raw = WithoutComment(Get("ListOrder") ?? Get("Time") ?? Get("Date"));

        var def = Get("BIOS Default");
        string? dflt = null;
        if (def is not null)
            dflt = kind switch
            {
                BiosKind.Options when OptionText.Match(def) is { Success: true } d =>
                    options.FirstOrDefault(o => SameHex(o.Value, d.Groups[2].Value))?.Value ?? d.Groups[2].Value.ToUpperInvariant(),
                BiosKind.Numeric when NumberText.Match(def) is { Success: true } d && TryNumber(d.Groups[1].Value, out var x) => x.ToString(CultureInfo.InvariantCulture),
                BiosKind.Checkbox when LeadingHex.Match(def) is { Success: true } d && TryHex(d.Groups[1].Value, out var x) => x.ToString("X", CultureInfo.InvariantCulture),
                _ => null,
            };

        return new BiosQuestion(name, help, token, offset, width, kind, options, current, dflt, raw)
        {
            First = first, Last = last, ValueLine = valueLine, OptionLines = optLines,
        };
    }

    static string? WithoutComment(string? s)
    {
        if (s is null) return null;
        var c = s.IndexOf("//", StringComparison.Ordinal);
        return (c > 0 ? s[..c] : s).Trim();
    }

    // ---------- values ----------

    static bool TryHex(string? s, out ulong v) => ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v);
    static bool SameHex(string? a, string? b) => TryHex(a, out var x) && TryHex(b, out var y) && x == y;
    static bool TryNumber(string? s, out Int128 v) => Int128.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    /// <summary>Same value in the format of the question type ("00" = "0" in a list; "007" = "7" in a number).</summary>
    public static bool SameValue(BiosQuestion q, string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        return q.Kind switch
        {
            BiosKind.Options or BiosKind.Checkbox => SameHex(a, b),
            BiosKind.Numeric => TryNumber(a, out var x) && TryNumber(b, out var y) && x == y,
            _ => a == b,
        };
    }

    /// <summary>Null if <paramref name="value"/> can be written to the question; otherwise the reason (in English).</summary>
    public static string? Check(BiosQuestion q, string? value)
    {
        switch (q.Kind)
        {
            case BiosKind.Options:
                return q.Options.Any(o => SameHex(o.Value, value)) ? null : Txt.BiosNotAnOption(q.Name);
            case BiosKind.Checkbox:
                return SameHex(value, "0") || SameHex(value, "1") ? null : Txt.BiosCheckboxValue;
            case BiosKind.Numeric:
                if (!TryNumber(value, out var n)) return Txt.BiosNotANumber;
                var (min, max) = Range(q);
                return n < min || n > max ? Txt.BiosOutOfRange(min.ToString(CultureInfo.InvariantCulture), max.ToString(CultureInfo.InvariantCulture)) : null;
            default:
                return Txt.BiosNotEditable(q.Name);
        }
    }

    /// <summary>
    /// Range that fits in the question's bytes, signed or unsigned (SCEWIN writes both). The real limits are in the BIOS:
    /// SCEWIN refuses a value outside them and re-reading shows it was not written.
    /// </summary>
    public static (Int128 Min, Int128 Max) Range(BiosQuestion q)
    {
        var bytes = TryHex(q.Width, out var w) && w is >= 1 and <= 8 ? (int)w : 8;
        return (-(Int128.One << (bytes * 8 - 1)), (Int128.One << (bytes * 8)) - 1);
    }

    /// <summary>Value text for the screen: the option name, Enabled/Disabled, the number.</summary>
    public static string Describe(BiosQuestion q, string? value)
    {
        if (q.Kind == BiosKind.ReadOnly) return q.Raw is { Length: > 0 } r ? r : "—";
        if (value is null) return Txt.BiosNotSet;
        return q.Kind switch
        {
            BiosKind.Options => q.Options.FirstOrDefault(o => SameHex(o.Value, value)) is { } o ? (o.Label.Length > 0 ? o.Label : $"[{o.Value}]") : $"[{value}]",
            BiosKind.Checkbox => SameHex(value, "1") ? Txt.Enabled : SameHex(value, "0") ? Txt.Disabled : value,
            _ => value,
        };
    }

    // ---------- import ----------

    /// <summary>
    /// Import script: the SCEWIN header (up to HIICrc32, which it checks against the BIOS) and only the blocks of the
    /// changed questions, identical to the exported ones except for the value. What is not in the script is not touched by SCEWIN.
    /// </summary>
    public string ImportScript(IReadOnlyDictionary<string, string> changes)
    {
        if (_crcLine < 0) throw new InvalidOperationException(Txt.ScewinInvalidExport);
        var sb = new StringBuilder();
        for (var i = 0; i <= _crcLine; i++) sb.Append(_lines[i]).Append('\n');
        var used = 0;
        foreach (var q in Questions)
        {
            if (!changes.TryGetValue(q.Key, out var value)) continue;
            if (Check(q, value) is { } problem) throw new InvalidOperationException(problem);
            sb.Append("\r\n");
            for (var i = q.First; i <= q.Last; i++) sb.Append(Rewrite(q, i, value)).Append('\n');
            used++;
        }
        if (used != changes.Count) throw new InvalidOperationException(Txt.BiosQuestionMissing);
        return sb.ToString();
    }

    string Rewrite(BiosQuestion q, int line, string value)
    {
        var text = _lines[line];
        switch (q.Kind)
        {
            case BiosKind.Options:
                var index = IndexOf(q.OptionLines, line);
                if (index < 0) return text;
                // The "*" goes right before the option's "[" ("Options\t=*[00]…", "         *[01]…").
                var bracket = text.IndexOf('[', line == q.ValueLine ? text.IndexOf('=') + 1 : 0);
                var start = bracket > 0 && text[bracket - 1] == '*' ? bracket - 1 : bracket;
                return text[..start] + (SameHex(q.Options[index].Value, value) ? "*" : "") + text[bracket..];
            case BiosKind.Numeric when line == q.ValueLine:
                var lt = text.IndexOf('<', text.IndexOf('=') + 1);
                var gt = text.IndexOf('>', lt + 1);
                TryNumber(value, out var number);
                return text[..(lt + 1)] + number.ToString(CultureInfo.InvariantCulture) + text[gt..];
            case BiosKind.Checkbox when line == q.ValueLine:
                var from = text.IndexOf('=') + 1;
                while (from < text.Length && text[from] is ' ' or '\t') from++;
                var to = from;
                while (to < text.Length && char.IsAsciiHexDigit(text[to])) to++;
                TryHex(value, out var bit);
                return text[..from] + bit.ToString("X", CultureInfo.InvariantCulture) + text[to..];
            default:
                return text;
        }
    }

    static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    // ---------- comparison ----------

    /// <summary>Editable questions with a different value between the two scripts (read-only ones, like time and date, are left out).</summary>
    public static List<BiosDiff> Compare(NvramScript from, NvramScript to)
    {
        var after = to.Questions.Where(q => q.Editable).ToDictionary(q => q.Key);
        var list = new List<BiosDiff>();
        foreach (var q in from.Questions.Where(q => q.Editable))
        {
            after.Remove(q.Key, out var now);
            if (now is null || now.Kind != q.Kind || !SameValue(q, q.Current, now.Current)) list.Add(new BiosDiff(q, now));
        }
        list.AddRange(to.Questions.Where(q => after.ContainsKey(q.Key)).Select(q => new BiosDiff(null, q)));
        return list;
    }
}
