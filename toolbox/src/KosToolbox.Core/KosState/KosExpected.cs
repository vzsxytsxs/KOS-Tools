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

    /// <summary>
    /// The 64-bit key a WOW6432Node key is shared with, or null. Windows 11 shares most Policies keys (a registry link: the kernel
    /// names the 64-bit key) and a few Explorer keys (the same key: same last write time and values) between the two views, so
    /// checking both would give one value two expectations and an apply on one would undo the other (checked in a KOS VM).
    /// </summary>
    public static string? SharedNative(string root, string key)
    {
        var i = key.IndexOf(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || Map(root, key) is not { Hive: Hive.LocalMachine } m) return null;
        var native = key.Remove(i, @"\WOW6432Node".Length);
        var nativePath = Map(root, native)!.Value.Path;
        try
        {
            using var b = Base(Hive.LocalMachine);
            using var wow = b.OpenSubKey(m.Path);
            using var nat = b.OpenSubKey(nativePath);
            if (wow is null || nat is null) return null;
            if (KernelName(wow)?.EndsWith(@"\" + nativePath, StringComparison.OrdinalIgnoreCase) == true) return native;
            return LastWrite(wow) is { } w && w == LastWrite(nat) && SameValues(wow, nat) ? native : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    static bool SameValues(RegistryKey a, RegistryKey b)
    {
        var names = a.GetValueNames();
        if (names.Length != b.ValueCount) return false;
        foreach (var n in names)
        {
            if (a.GetValueKind(n) != b.GetValueKind(n)) return false;
            object? x = a.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames), y = b.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var same = x switch
            {
                byte[] xb => y is byte[] yb && xb.AsSpan().SequenceEqual(yb),
                string[] xs => y is string[] ys && xs.SequenceEqual(ys),
                _ => Equals(x, y),
            };
            if (!same) return false;
        }
        return true;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int NtQueryKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, int infoClass, byte[] buffer, int length, out int result);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegQueryInfoKeyW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr cls, IntPtr clsLen, IntPtr reserved, IntPtr subKeys,
        IntPtr maxSubKey, IntPtr maxCls, IntPtr values, IntPtr maxValueName, IntPtr maxValue, IntPtr security, out long lastWrite);

    /// <summary>The key's name as the kernel resolved it (KeyNameInformation), e.g. \REGISTRY\MACHINE\SOFTWARE\...</summary>
    static string? KernelName(RegistryKey k)
    {
        var buffer = new byte[4096];
        if (NtQueryKey(k.Handle, 3, buffer, buffer.Length, out _) != 0) return null;
        var length = BitConverter.ToInt32(buffer, 0);
        return length is > 0 and <= 4092 ? System.Text.Encoding.Unicode.GetString(buffer, 4, length) : null;
    }

    static long? LastWrite(RegistryKey k) =>
        RegQueryInfoKeyW(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var t) == 0 ? t : null;

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
