using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Tools;
using Microsoft.Win32;

namespace KosToolbox.Core.KosState;

public sealed class KosSnapshotValue
{
    public string Name { get; set; } = "";
    /// <summary>As in kos-expected.json, plus none and unknown (kept in hex, never written back).</summary>
    public string Type { get; set; } = "";
    public JsonElement Data { get; set; }
}

public sealed class KosSnapshotKey
{
    public string Root { get; set; } = "";
    public string Key { get; set; } = "";
    public bool? Exists { get; set; }
    public List<KosSnapshotValue>? Values { get; set; }
    /// <summary>The key couldn't be read (access denied): nothing is compared with it.</summary>
    public bool? Unreadable { get; set; }
}

public sealed class KosWindowsBuild
{
    public string? Build { get; set; }
    public string? Ubr { get; set; }
    public string? DisplayVersion { get; set; }
}

/// <summary>
/// What the KOS keys held at one moment: the setup's baseline (KOS-W11 bin\2\Capture-KosBaseline.ps1, before the first restart)
/// or the last good state the toolbox saved. Same file format for both.
/// </summary>
public sealed class KosSnapshot
{
    public int Format { get; set; } = 1;
    public string? CapturedAt { get; set; }
    public KosWindowsBuild? Windows { get; set; }
    public string? UserSid { get; set; }
    public string? ExpectedSha256 { get; set; }
    public List<KosSnapshotKey> Keys { get; set; } = new();

    Dictionary<string, KosSnapshotKey>? _index;

    public KosSnapshotKey? Find(string root, string key) =>
        (_index ??= Keys.GroupBy(k => Id(k.Root, k.Key)).ToDictionary(g => g.Key, g => g.Last())).GetValueOrDefault(Id(root, key));

    static string Id(string root, string key) => $@"{root}\{key}".ToLowerInvariant();
}

public enum KosReferenceKind { LastGood, SetupBaseline }

/// <summary>The snapshot a part of the system (machine keys or the user's) is compared with.</summary>
public sealed record KosReference(KosReferenceKind Kind, KosSnapshot Snapshot);

public static class KosSnapshots
{
    public static string Folder => Path.Combine(AppPaths.TheKosRoot, "Baseline");
    public static string SetupBaseline => Path.Combine(Folder, "kos-baseline.json");
    /// <summary>HKLM and HKCR keys: only administrators can change it, since the elevated side writes from it.</summary>
    public static string MachineLastGood => Path.Combine(Folder, "kos-lastgood.json");
    /// <summary>HKCU keys: written back by the user's own process, so the user's own folder.</summary>
    public static string UserLastGood => Path.Combine(AppPaths.DataRoot, "kos-lastgood-user.json");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    static JsonTypeInfo<KosSnapshot> Info => (JsonTypeInfo<KosSnapshot>)Json.GetTypeInfo(typeof(KosSnapshot));

    static string? CurrentSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value;
    }

    /// <summary>Machine part: the last good state, else the setup's baseline; only from the admin-only folder.</summary>
    public static KosReference? MachineReference() =>
        LoadTrusted(MachineLastGood) is { } last ? new(KosReferenceKind.LastGood, last)
        : LoadTrusted(SetupBaseline) is { } setup ? new(KosReferenceKind.SetupBaseline, setup) : null;

    /// <summary>User part: the last good state this user saved, else the setup's baseline when this user ran the setup.</summary>
    public static KosReference? UserReference()
    {
        var sid = CurrentSid();
        // The baseline only from the admin-only folder: a standard user can create TheKOS\Baseline where the setup didn't.
        return UserPart(Load(UserLastGood), LoadTrusted(SetupBaseline), sid);
    }

    internal static KosReference? UserPart(KosSnapshot? lastGood, KosSnapshot? baseline, string? sid) =>
        lastGood is not null && lastGood.UserSid == sid ? new(KosReferenceKind.LastGood, lastGood)
        : baseline is not null && sid is not null && baseline.UserSid == sid ? new(KosReferenceKind.SetupBaseline, baseline) : null;

    /// <summary>Null when missing, unreadable, or in a folder (or file) SYSTEM or Administrators don't own.</summary>
    internal static KosSnapshot? LoadTrusted(string path)
    {
        if (!File.Exists(path)) return null;
        if ((GpuTuner.FolderProblem(AppPaths.TheKosRoot, Path.GetDirectoryName(path)!) ?? ToolDiscovery.OwnerProblem(path)) is { } problem)
        {
            Log.Warn($"KOS state: {path} ignored ({problem})");
            return null;
        }
        return Load(path);
    }

    static KosSnapshot? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), Info) : null; }
        catch (Exception ex) { Log.Warn($"KOS state: {path} is unreadable ({ex.Message})"); return null; }
    }

    /// <summary>Reads what the snapshot keys of <paramref name="machine"/> (HKLM and HKCR) or of the user (HKCU) hold now.</summary>
    public static KosSnapshot Capture(KosExpected expected, bool machine)
    {
        var snap = new KosSnapshot
        {
            CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Windows = WindowsBuild(), UserSid = CurrentSid(), ExpectedSha256 = KosExpected.EmbeddedSha256,
        };
        foreach (var k in expected.SnapshotKeys.Where(k => KosReg.Machine(k.Root) == machine)) snap.Keys.Add(CaptureKey(expected, k.Root, k.Key));
        return snap;
    }

    internal static KosSnapshotKey CaptureKey(KosExpected expected, string root, string key)
    {
        var e = new KosSnapshotKey { Root = root, Key = key };
        try
        {
            if (KosReg.Map(root, key) is not { } m) { e.Unreadable = true; return e; }
            using var b = KosReg.Base(m.Hive);
            using var k = b.OpenSubKey(m.Path);
            e.Exists = k is not null;
            if (k is null) return e;
            e.Values = k.GetValueNames().Where(n => !expected.Ignored(root, key, n)).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => Value(n, k.GetValueKind(n), k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames))).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            e.Exists = null; e.Values = null; e.Unreadable = true;
        }
        return e;
    }

    /// <summary>A value the way the setup's baseline writes it: DWORD and QWORD unsigned in decimal, bytes in lower-case hex.</summary>
    internal static KosSnapshotValue Value(string name, RegistryValueKind kind, object? raw)
    {
        static string Hex(object? v) => v is byte[] b ? Convert.ToHexString(b).ToLowerInvariant() : "";
        var (type, data) = kind switch
        {
            RegistryValueKind.DWord => ("dword", (object)unchecked((uint)Convert.ToInt32(raw)).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => ("qword", unchecked((ulong)Convert.ToInt64(raw)).ToString(CultureInfo.InvariantCulture)),
            RegistryValueKind.String => ("sz", raw as string ?? ""),
            RegistryValueKind.ExpandString => ("expand_sz", raw as string ?? ""),
            RegistryValueKind.MultiString => ("multi_sz", raw as string[] ?? Array.Empty<string>()),
            RegistryValueKind.Binary => ("binary", Hex(raw)),
            RegistryValueKind.None => ("none", Hex(raw)),
            _ => ("unknown", Hex(raw)),
        };
        return new KosSnapshotValue { Name = name, Type = type, Data = JsonSerializer.SerializeToElement(data, data.GetType()) };
    }

    /// <summary>Same type and data (bytes compared ignoring case).</summary>
    internal static bool Same(KosSnapshotValue a, KosSnapshotValue b) =>
        a.Type == b.Type && string.Equals(KosReg.Text(a.Data), KosReg.Text(b.Data),
            a.Type is "binary" or "none" or "unknown" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static KosWindowsBuild WindowsBuild()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new KosWindowsBuild
        {
            Build = k?.GetValue("CurrentBuild") as string,
            Ubr = k?.GetValue("UBR") is int ubr ? ubr.ToString(CultureInfo.InvariantCulture) : null,
            DisplayVersion = k?.GetValue("DisplayVersion") as string,
        };
    }

    /// <summary>"26200.7171": build and update number (UBR), which every cumulative update changes.</summary>
    public static string? BuildText(KosWindowsBuild? b) => b?.Build is { } build ? b.Ubr is { } ubr ? $"{build}.{ubr}" : build : null;

    /// <summary>The user's part as the new last good state.</summary>
    public static void SaveUser(KosSnapshot s) => JsonFile.Save(UserLastGood, s, Info);

    /// <summary>The machine part as the new last good state; administrator only, in the admin-only Baseline folder.</summary>
    internal static void SaveMachine(KosSnapshot s)
    {
        GpuTuner.PrepareDirectory(Folder);
        if (File.Exists(MachineLastGood) && ToolDiscovery.OwnerProblem(MachineLastGood) is { } problem) throw new InvalidOperationException(problem);
        File.Delete(MachineLastGood + ".tmp"); // JsonFile.Save writes through it: never into one someone else left
        JsonFile.Save(MachineLastGood, s, Info);
    }
}
