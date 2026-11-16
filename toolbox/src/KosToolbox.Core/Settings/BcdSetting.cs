using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Settings;

/// <summary>
/// An element of the BCD {current} entry, read from "bcdedit /enum {current}" and written with
/// "bcdedit /set" or "/deletevalue", as in the KOS scripts. Reading the BCD requires administrator.
/// A null value = element absent (default boot loader behavior).
/// </summary>
public sealed class BcdSetting : ISetting
{
    public string Element { get; }
    public BcdSetting(string element) => Element = element;

    public string Key => $"bcd:{{current}}\\{Element}";
    public string Display => $"BCD {{current}} → {Element}";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        if (!Elevation.IsAdmin)
            throw new UnauthorizedAccessException("Reading the BCD requires administrator rights. Use \"Restart as administrator\" to see this state.");
        return Bcd.Current().GetValueOrDefault(Element.ToLowerInvariant());
    }

    public void Write(string? value)
    {
        var args = value is null ? $"/deletevalue {{current}} {Element}" : $"/set {{current}} {Element} {value}";
        var r = ProcessRunner.Run(Bcd.Exe, args);
        Bcd.Invalidate();
        // deletevalue on a nonexistent element fails, but the desired result (absent) already holds.
        if (r.ExitCode != 0 && !(value is null && Read() is null))
            throw new InvalidOperationException($"bcdedit {args} failed ({r.ExitCode}): {r.Output.Trim()}");
    }
}

static class Bcd
{
    public static string Exe => Path.Combine(Environment.SystemDirectory, "bcdedit.exe");
    static Dictionary<string, string>? _cache;
    static DateTime _at;
    static readonly object Gate = new();

    public static void Invalidate() { lock (Gate) _cache = null; }

    public static Dictionary<string, string> Current()
    {
        lock (Gate)
        {
            if (_cache is not null && DateTime.UtcNow - _at < TimeSpan.FromSeconds(3)) return _cache;
            var r = ProcessRunner.Run(Exe, "/enum {current}");
            if (r.ExitCode != 0) throw new InvalidOperationException($"bcdedit /enum failed ({r.ExitCode}): {r.Output.Trim()}");
            _cache = Parse(r.Output);
            _at = DateTime.UtcNow;
            Log.Info($"BCD read: {_cache.Count} elements.");
            return _cache;
        }
    }

    /// <summary>"element  value" lines; element names are not translated by bcdedit.</summary>
    internal static Dictionary<string, string> Parse(string output)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inBody = false; // the section title (translated) comes before the dashes line
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('-')) { inBody = true; continue; }
            if (!inBody || line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            var sep = line.IndexOf(' ');
            if (sep <= 0) continue;
            var value = line[sep..].Trim();
            if (value.Length > 0) d.TryAdd(line[..sep].ToLowerInvariant(), value);
        }
        return d;
    }
}
