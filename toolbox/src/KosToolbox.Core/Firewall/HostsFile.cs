using System.Net;
using System.Net.Sockets;
using System.Text;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Firewall;

public enum HostsLineKind { Blank, Comment, Entry, Invalid }

/// <summary>
/// One line of the hosts file. Address, Hosts and Comment are filled for entries (Comment is the text after '#', or null);
/// Problem is the English reason for an Invalid line.
/// </summary>
public sealed record HostsLine(int Number, string Raw, HostsLineKind Kind, string? Address = null,
    IReadOnlyList<string>? Hosts = null, string? Comment = null, string? Problem = null);

public sealed record HostsProblem(int Line, string Message);

/// <summary>
/// The Windows hosts file: structured view, validation and a safe save. Hand-written checks (no Regex, no reflection) so the
/// engine can reach it. Windows reads the file as ANSI, so anything non-ASCII is refused (international names go in punycode).
/// </summary>
public static class HostsFile
{
    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>%ProgramData%\TheKOS\Toolbox\Hosts.</summary>
    public static string DefaultBackupDirectory => Path.Combine(AppPaths.TheKosRoot, "Toolbox", "Hosts");

    const int KeepBackups = 10;
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>One record per line (a final line break doesn't add an empty line).</summary>
    public static IReadOnlyList<HostsLine> Parse(string text)
    {
        var lines = text.Split('\n');
        int count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var list = new List<HostsLine>(count);
        for (int i = 0; i < count; i++) list.Add(ParseLine(i + 1, lines[i].TrimEnd('\r')));
        return list;
    }

    static HostsLine ParseLine(int number, string raw)
    {
        int hash = raw.IndexOf('#');
        string code = hash < 0 ? raw : raw[..hash];
        string? comment = hash < 0 ? null : raw[(hash + 1)..];
        var tokens = code.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new(number, raw, hash < 0 ? HostsLineKind.Blank : HostsLineKind.Comment);

        string? problem = AddressProblem(tokens[0]);
        if (problem == null && tokens.Length == 1) problem = "No host name after the address.";
        for (int i = 1; problem == null && i < tokens.Length; i++) problem = HostNameProblem(tokens[i]);
        return problem != null
            ? new(number, raw, HostsLineKind.Invalid, Problem: problem)
            : new(number, raw, HostsLineKind.Entry, tokens[0], tokens[1..], comment);
    }

    /// <summary>Problems that would make the file wrong or unreadable for Windows; empty = valid.</summary>
    public static IReadOnlyList<HostsProblem> Validate(string text)
    {
        var problems = new List<HostsProblem>();
        foreach (var line in Parse(text))
        {
            string? chars = CharProblem(line.Raw);
            if (chars != null) problems.Add(new(line.Number, chars));
            else if (line.Problem != null) problems.Add(new(line.Number, line.Problem));
        }
        return problems;
    }

    /// <summary>
    /// The problems of <paramref name="text"/> that <paramref name="current"/> (the file on disk) doesn't already have, by line
    /// text: a line Windows ignores today (a wildcard, a URL, a stray character) doesn't stop a save, a new mistake does.
    /// </summary>
    public static IReadOnlyList<HostsProblem> NewProblems(string text, string current)
    {
        var known = Validate(current).Select(p => p.Line).ToHashSet();
        var knownText = Parse(current).Where(l => known.Contains(l.Number)).Select(l => l.Raw).ToHashSet(StringComparer.Ordinal);
        var lines = Parse(text);
        return Validate(text).Where(p => !knownText.Contains(lines[p.Line - 1].Raw)).ToList();
    }

    static string? CharProblem(string s)
    {
        foreach (char c in s)
        {
            if (c == '\0') return "The line contains a NUL character.";
            if (c > 127) return "The line contains a non-ASCII character; Windows reads the file as ANSI, write international names in punycode (xn--).";
        }
        return null;
    }

    static string? AddressProblem(string a)
    {
        string bad = "Not a valid IPv4 or IPv6 address: " + a;
        if (!a.Contains(':'))
        {
            var parts = a.Split('.');
            bool ok = parts.Length == 4;
            foreach (var p in parts)
                ok = ok && p.Length is >= 1 and <= 3 && p.All(c => c is >= '0' and <= '9') && int.Parse(p) <= 255;
            return ok ? null : bad;
        }
        int pct = a.IndexOf('%');
        bool scopeOk = pct < 0 || pct < a.Length - 1;
        return scopeOk && IPAddress.TryParse(pct < 0 ? a : a[..pct], out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? null : bad;
    }

    static string? HostNameProblem(string h)
    {
        string bad = $"Invalid host name \"{h}\": ";
        if (h.Length > 253) return bad + "longer than 253 characters.";
        foreach (var c in h)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
                return bad + "only letters, digits, '-', '_' and '.' are allowed.";
        foreach (var label in h.Split('.'))
        {
            if (label.Length is 0 or > 63) return bad + "each label must be 1 to 63 characters.";
            if (label[0] == '-' || label[^1] == '-') return bad + "a label can't start or end with '-'.";
        }
        return null;
    }

    /// <summary>The file's text without a UTF-8 BOM; a missing file is an empty string.</summary>
    public static string Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return "";
        var bytes = File.ReadAllBytes(path);
        int skip = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        return Utf8.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>
    /// Validates (refusing only problems the file on disk doesn't already have, see <see cref="NewProblems"/>), backs up the
    /// current file, writes CRLF/UTF-8 without BOM through a temporary file that replaces the hosts file,
    /// and reads it back. Returns the backup's path (null if there was nothing to back up). Writing the real file needs
    /// administrator: the UnauthorizedAccessException is left to the caller.
    /// </summary>
    public static string? Save(string text, string? path = null, string? backupDirectory = null)
    {
        path ??= DefaultPath;
        var problems = NewProblems(text, Read(path));
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems.Take(3).Select(p => $"Line {p.Line}: {p.Message}")));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (File.Exists(Path.Combine(dir, "hosts_1.tmp")) || File.Exists(Path.Combine(dir, "hosts_gdid.tmp")))
            throw new InvalidOperationException("A hosts file swap (hosts_1.tmp or hosts_gdid.tmp) is half-way through; resolve it before saving.");

        string? backup = File.Exists(path) ? Backup(path, backupDirectory) : null;

        var bytes = Utf8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        var tmp = path + ".kos.tmp";
        var attrs = File.Exists(path) ? File.GetAttributes(path) : default;
        bool readOnly = attrs.HasFlag(FileAttributes.ReadOnly);
        try
        {
            File.WriteAllBytes(tmp, bytes);
            if (readOnly) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (readOnly && File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }

        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new IOException("The hosts file on disk is not what was written.");
        return backup;
    }

    static string Backup(string path, string? directory)
    {
        if (directory == null) Gpu.GpuTuner.PrepareDirectory(directory = DefaultBackupDirectory);
        else Directory.CreateDirectory(directory);
        var now = DateTime.Now;
        string target;
        // Two saves in the same second must not overwrite each other's backup.
        while (File.Exists(target = Path.Combine(directory, $"hosts-{now:yyyyMMdd-HHmmss}.bak"))) now = now.AddSeconds(1);
        File.Copy(path, target);
        foreach (var old in Directory.GetFiles(directory, "hosts-*.bak").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepBackups))
            File.Delete(old);
        return target;
    }
}
