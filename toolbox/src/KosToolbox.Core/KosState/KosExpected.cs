using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using Microsoft.Win32;

namespace KosToolbox.Core.KosState;

/// <summary>A registry key of the KOS lists. Root is HKLM, HKCU or HKCR.</summary>
public sealed class KosKey
{
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Source { get; set; }
}

/// <summary>A value every KOS install leaves, and the file line that sets it last.</summary>
public sealed class KosExpectedValue
{
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>sz, expand_sz, multi_sz (list), dword and qword (decimal), binary (hex) or absent (the KOS deletes the value).</summary>
    public string Type { get; set; } = "";
    public JsonElement Data { get; set; }
    public string Source { get; set; } = "";
    /// <summary>Only set where this key exists ("reg query X &amp;&amp; reg add" in the scripts).</summary>
    public string? IfKeyExists { get; set; }
    /// <summary>The data has %variables% that cmd expanded when the script wrote it.</summary>
    public bool ExpandData { get; set; }
}

public sealed class KosExpectedTask
{
    public string Path { get; set; } = "";
    /// <summary>disabled, enabled or absent (the KOS deletes the task).</summary>
    public string State { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>An app the KOS removes: a package name, with * as in PowerShell's -like.</summary>
public sealed class KosExpectedPackage
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>A component NTLite removes from the image. Apps are matched by package name; the others can't be seen with Windows running.</summary>
public sealed class KosExpectedComponent
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>An optional feature (on or off) or a capability (Enabled false: not installed) as the KOS image has it.</summary>
public sealed class KosExpectedFeature
{
    public string Name { get; set; } = "";
    public bool Capability { get; set; }
    public bool Enabled { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>A file or folder the KOS deletes from the image (the path as on a C: install).</summary>
public sealed class KosExpectedFile
{
    public string Path { get; set; } = "";
    /// <summary>file or folder.</summary>
    public string Kind { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>
/// What a KOS install leaves on the system, generated from the KOS files by KOS-W11's building\Generate-KosExpected.ps1 and
/// embedded here (the KOS this toolbox came with). SnapshotKeys are every key the KOS writes, the hardware and profile ones
/// included; Ignore holds "ROOT\key|name" patterns of values Windows changes by itself.
/// </summary>
public sealed class KosExpected
{
    public int Format { get; set; }
    public List<KosExpectedValue> Registry { get; set; } = new();
    public List<KosKey> KeysAbsent { get; set; } = new();
    public List<KosExpectedTask> Tasks { get; set; } = new();
    public List<KosExpectedPackage> Packages { get; set; } = new();
    public List<KosExpectedComponent> Components { get; set; } = new();
    public List<KosExpectedFeature> Features { get; set; } = new();
    public List<KosExpectedFile> Files { get; set; } = new();
    public List<KosKey> SnapshotKeys { get; set; } = new();
    public List<string> Ignore { get; set; } = new();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    HashSet<string>? _listed;

    /// <summary>"ROOT\key|name" of every value in <see cref="Registry"/>: checked against the list, never against a snapshot.</summary>
    internal bool Listed(string root, string key, string name) =>
        (_listed ??= Registry.Select(e => $@"{e.Root}\{e.Key}|{e.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase)).Contains($@"{root}\{key}|{name}");
    static readonly Lazy<byte[]> bytes = new(ReadResource);
    static readonly Lazy<KosExpected> embedded = new(() => Parse(bytes.Value));
    Regex[]? _ignore;

    public static KosExpected Embedded => embedded.Value;
    /// <summary>SHA-256 of the embedded file, as the setup's baseline records the one it used.</summary>
    public static string EmbeddedSha256 => Convert.ToHexString(SHA256.HashData(bytes.Value)).ToLowerInvariant();
    internal static byte[] EmbeddedBytes => bytes.Value;

    internal static KosExpected Parse(byte[] json) =>
        JsonSerializer.Deserialize<KosExpected>(json, Json) ?? throw new InvalidDataException("kos-expected.json is empty.");

    static byte[] ReadResource()
    {
        using var s = typeof(KosExpected).Assembly.GetManifestResourceStream("kos-expected.json")
            ?? throw new InvalidOperationException("kos-expected.json isn't embedded in the toolbox.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Whether the value matches an Ignore pattern (case-insensitive, * as in PowerShell's -like).</summary>
    public bool Ignored(string root, string key, string name)
    {
        _ignore ??= Ignore.Select(LikeRegex).ToArray();
        var id = $@"{root}\{key}|{name}";
        return _ignore.Any(r => r.IsMatch(id));
    }

    static Regex LikeRegex(string pattern) => new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>PowerShell's -like: case-insensitive, * any text, ? one character.</summary>
    internal static bool Like(string pattern, string text) => LikeRegex(pattern).IsMatch(text);
}

/// <summary>Registry access for the KOS lists.</summary>
static class KosReg
{
    /// <summary>HKCR is the machine's classes (HKLM\SOFTWARE\Classes), where reg.exe writes it when run elevated as the KOS setup does.</summary>
    public static (Hive Hive, string Path)? Map(string root, string key) => root.ToUpperInvariant() switch
    {
        "HKLM" => (Hive.LocalMachine, key),
        "HKCU" => (Hive.CurrentUser, key),
        "HKCR" => (Hive.LocalMachine, $@"SOFTWARE\Classes\{key}"),
        _ => null,
    };

    /// <summary>Written by the elevated side; HKCU always by the user's own process (an elevated process may run as another account).</summary>
    public static bool Machine(string root) => !root.Equals("HKCU", StringComparison.OrdinalIgnoreCase);

    public static RegistryValueKind? Kind(string type) => type switch
    {
        "sz" or "absent" => RegistryValueKind.String,
        "expand_sz" => RegistryValueKind.ExpandString,
        "multi_sz" => RegistryValueKind.MultiString,
        "dword" => RegistryValueKind.DWord,
        "qword" => RegistryValueKind.QWord,
        "binary" => RegistryValueKind.Binary,
        _ => null,
    };

    /// <summary>The value as a catalog setting (read, compare, write with the TrustedInstaller fallback); null for a root or type it can't write.</summary>
    public static RegistrySetting? Setting(string root, string key, string name, string type) =>
        Map(root, key) is { } m && Kind(type) is { } kind ? new RegistrySetting(m.Hive, m.Path, name, kind) : null;

    public static RegistryKey Base(Hive hive) => RegistryKey.OpenBaseKey(
        hive == Hive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public static bool KeyExists(string root, string key)
    {
        if (Map(root, key) is not { } m) return false;
        using var b = Base(m.Hive);
        using var k = b.OpenSubKey(m.Path);
        return k is not null;
    }

    /// <summary>
    /// A service or autologger the KOS configures but this installation doesn't have: writing its Start would create a broken
    /// key, so the value is neither checked nor written.
    /// </summary>
    public static bool OwnerMissing(string root, string key)
    {
        var m = Regex.Match(key, @"^(SYSTEM\\CurrentControlSet\\(?:Services|Control\\WMI\\Autologger)\\[^\\]+)", RegexOptions.IgnoreCase);
        return m.Success && root.Equals("HKLM", StringComparison.OrdinalIgnoreCase) && !KeyExists(root, m.Groups[1].Value);
    }

    /// <summary>Text of a list's data as <see cref="RegistrySetting.Read"/> gives it: a list joined with \n; null for none.</summary>
    public static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => string.Join("\n", e.EnumerateArray().Select(x => x.GetString())),
        _ => null,
    };

    public static void DeleteKey(string root, string key)
    {
        if (Map(root, key) is not { } m) throw new InvalidOperationException($"Unsupported root: {root}");
        try
        {
            using var b = Base(m.Hive);
            b.DeleteSubKeyTree(m.Path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                   && m.Hive == Hive.LocalMachine && TrustedInstaller.Available)
        {
            TrustedInstaller.RegDeleteKey(m.Path);
        }
        Log.Info($@"{root}\{key} deleted");
    }
}
