using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Services;

/// <summary>Startup configuration of a service: Start, DelayedAutoStart and the StartOverride DWORDs.</summary>
public sealed record ServiceState(string Name, int Start, int? DelayedAutoStart, IReadOnlyDictionary<string, int> StartOverride);

public sealed record ServiceSnapshot(string Name, DateTimeOffset? CapturedAt, string? Computer, IReadOnlyList<ServiceState> Services);

/// <summary>A snapshot in the folder. <see cref="Problem"/> is set when the file isn't a valid snapshot.</summary>
public sealed record SnapshotInfo(string Name, DateTime LastWrite, DateTimeOffset? CapturedAt, string? Computer, int? ServiceCount, bool HasFullExport, string? Problem);

/// <summary>Difference between two states. No state on one side = absent from the snapshot or service removed.</summary>
public sealed record ServiceDiff(string Name, string Before, string After, ServiceState? Old, ServiceState? New)
{
    public bool Restorable => Old is not null && New is not null;
}

public sealed record CaptureResult(string Snapshot, int Count, bool KeptExistingBaseline, int SkippedKeys);

public sealed record ServiceRestoreItem(string Name, bool Ok, string? Error);

public sealed record RestoreResult(string SafetySnapshot, IReadOnlyList<ServiceRestoreItem> Items)
{
    public bool AllOk => Items.All(i => i.Ok);
}

/// <summary>
/// C# port of the KOS ServiceBackup.ps1 (Capture, Compare, Restore, Organize), in the same file format
/// (SchemaVersion 1): .json with the state, reference .reg (Start only), optional .full.reg and .previous
/// copies. Old snapshots stay readable and new ones stay readable by the original script.
/// It is neither a system image nor a restore point: it only covers the services' startup configuration.
/// </summary>
public static class ServiceSnapshots
{
    public const string Baseline = "Windows.Default.Services", KosSnapshot = "TheKOS.services", Current = "Current";
    public const int SchemaVersion = 1, MinimumServices = 20;
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    static readonly Regex NamePattern = new("^[A-Za-z0-9_.-]+$");
    static readonly Regex LegacyFile = new(@"\.(json|reg|csv)(\.previous)?$", RegexOptions.IgnoreCase);

    /// <summary>Folder of the original script (Setup\3-Support\Services); snapshots live in Backups.</summary>
    public static string ScriptDirectory => Path.Combine(AppPaths.TheKosRoot, "Setup", "3-Support", "Services");
    public static string Directory => Path.Combine(ScriptDirectory, "Backups");

    public static bool IsValidName(string? name) => name is { Length: > 0 } && NamePattern.IsMatch(name);

    static string FilePath(string name, string extension) => Path.Combine(Directory, name + extension);

    // ---------- reading the system ----------

    /// <summary>Read-ServiceState: all services with Start. Rejects a result with fewer than 20 services.</summary>
    public static List<ServiceState> ReadLive(IProgress<(int Done, int Total)>? progress = null)
    {
        var items = new List<ServiceState>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = root.OpenSubKey(ServicesKey) ?? throw new InvalidOperationException(Txt.ServicesKeyMissing);
        var names = services.GetSubKeyNames();
        for (var i = 0; i < names.Length; i++)
        {
            if (i % 50 == 0) progress?.Report((i, names.Length));
            var name = names[i];
            try
            {
                using var key = services.OpenSubKey(name) ?? throw new InvalidOperationException(Txt.KeyNotOpened);
                var start = key.GetValue("Start");
                if (start is null) continue;
                if (key.GetValueKind("Start") != RegistryValueKind.DWord) throw new InvalidOperationException("Start is not REG_DWORD");
                var delayed = key.GetValue("DelayedAutoStart");
                if (delayed is not null && key.GetValueKind("DelayedAutoStart") != RegistryValueKind.DWord)
                    throw new InvalidOperationException("DelayedAutoStart is not REG_DWORD");
                var overrides = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                using (var ov = key.OpenSubKey("StartOverride"))
                    if (ov is not null)
                        foreach (var v in ov.GetValueNames())
                            if (ov.GetValueKind(v) == RegistryValueKind.DWord) overrides[v] = (int)ov.GetValue(v)!;
                items.Add(new ServiceState(name, (int)start, (int?)delayed, overrides));
            }
            catch (Exception ex) { throw new InvalidOperationException(Txt.ServiceUnreadable(name, ex.Message), ex); }
        }
        progress?.Report((names.Length, names.Length));
        if (items.Count < MinimumServices) throw new InvalidOperationException(Txt.TooFewServices(items.Count));
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.InvariantCultureIgnoreCase));
        return items;
    }

    public static ServiceState? ReadLive(string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey($@"{ServicesKey}\{name}");
        if (key?.GetValue("Start") is not int start) return null;
        var overrides = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var ov = key.OpenSubKey("StartOverride"))
            if (ov is not null)
                foreach (var v in ov.GetValueNames())
                    if (ov.GetValueKind(v) == RegistryValueKind.DWord) overrides[v] = (int)ov.GetValue(v)!;
        return new ServiceState(name, start, key.GetValue("DelayedAutoStart") as int?, overrides);
    }

    // ---------- files ----------

    /// <summary>Read-Snapshot. "Current" is the system's current state.</summary>
    public static ServiceSnapshot Load(string name)
    {
        if (name == Current) return new ServiceSnapshot(Current, DateTimeOffset.Now, Environment.MachineName, ReadLive());
        if (!IsValidName(name)) throw new InvalidOperationException(Txt.InvalidSnapshotName);
        var path = FilePath(name, ".json");
        if (!File.Exists(path)) throw new FileNotFoundException(Txt.SnapshotMissing(path), path);
        return Parse(name, File.ReadAllText(path), path);
    }

    /// <summary>Accepts PowerShell 5.1 JSON (BOM, empty StartOverride as {} or "", null DelayedAutoStart).</summary>
    internal static ServiceSnapshot Parse(string name, string json, string path)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json.TrimStart('﻿')); }
        catch (JsonException ex) { throw new InvalidOperationException(Txt.SnapshotInvalid(path, ex.Message), ex); }
        if (root is not JsonObject obj) throw new InvalidOperationException(Txt.SnapshotInvalid(path, "not a JSON object"));
        var version = Int(obj["SchemaVersion"]);
        if (version > SchemaVersion) throw new InvalidOperationException(Txt.SnapshotNewer(path, version.Value));
        if (version != SchemaVersion) throw new InvalidOperationException(Txt.SnapshotInvalid(path, $"SchemaVersion {version?.ToString() ?? "missing"}"));
        if (obj["Services"] is not JsonArray array) throw new InvalidOperationException(Txt.SnapshotInvalid(path, "no Services list"));
        var list = new List<ServiceState>();
        foreach (var node in array)
        {
            if (node is not JsonObject s || s["Name"]?.GetValue<string>() is not { Length: > 0 } svc || Int(s["Start"]) is not { } start)
                throw new InvalidOperationException(Txt.SnapshotInvalid(path, "service entry without Name or Start"));
            var overrides = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (s["StartOverride"] is JsonObject ov)
                foreach (var (k, v) in ov)
                    if (Int(v) is { } n) overrides[k] = n;
            list.Add(new ServiceState(svc, start, Int(s["DelayedAutoStart"]), overrides));
        }
        if (list.Count < MinimumServices || Int(obj["ServiceCount"]) != list.Count)
            throw new InvalidOperationException(Txt.SnapshotIncomplete(path));
        DateTimeOffset? captured = obj["CapturedAt"] is JsonValue c && c.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
        var computer = obj["Computer"] is JsonValue pc && pc.TryGetValue<string>(out var pcName) ? pcName : null;
        return new ServiceSnapshot(name, captured, computer, list);
    }

    static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (v.TryGetValue<double>(out var dbl) && dbl == Math.Floor(dbl)) return (int)dbl;
        return null;
    }

    /// <summary>Snapshots in the Backups folder, newest to oldest (like the original UI).</summary>
    public static List<SnapshotInfo> List()
    {
        var list = new List<SnapshotInfo>();
        if (!System.IO.Directory.Exists(Directory)) return list;
        foreach (var file in new DirectoryInfo(Directory).GetFiles("*.json").Where(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => f.LastWriteTime))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var full = File.Exists(FilePath(name, ".full.reg"));
            try
            {
                var snap = Parse(name, File.ReadAllText(file.FullName), file.FullName);
                list.Add(new SnapshotInfo(name, file.LastWriteTime, snap.CapturedAt, snap.Computer, snap.Services.Count, full, null));
            }
            catch (Exception ex) { list.Add(new SnapshotInfo(name, file.LastWriteTime, null, null, null, full, ex.Message)); }
        }
        return list;
    }

    /// <summary>Files from old versions left in the script folder, not yet moved to Backups.</summary>
    public static List<string> LegacyFiles()
    {
        if (!System.IO.Directory.Exists(ScriptDirectory)) return new();
        return System.IO.Directory.GetFiles(ScriptDirectory)
            .Where(f => LegacyFile.IsMatch(Path.GetFileName(f)) && !File.Exists(Path.Combine(Directory, Path.GetFileName(f))))
            .ToList();
    }

    /// <summary>Organize: moves the old files to Backups without replacing anything already there.</summary>
    public static int Organize()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var moved = 0;
        foreach (var f in LegacyFiles())
        {
            File.Move(f, Path.Combine(Directory, Path.GetFileName(f)));
            moved++;
        }
        Log.Info($"Service snapshots: moved {moved} legacy files into {Directory}");
        return moved;
    }

    // ---------- comparison ----------

    /// <summary>The script's Format-State: "Start=N;Delayed=x|-;Override=a=1;b=2", or "missing".</summary>
    public static string FormatState(ServiceState? s)
    {
        if (s is null) return "missing";
        var overrides = string.Join(";", s.StartOverride.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => $"{o.Key}={o.Value}"));
        return $"Start={s.Start};Delayed={(s.DelayedAutoStart?.ToString() ?? "-")};Override={overrides}";
    }

    /// <summary>Show-State from the original UI, in English.</summary>
    public static string Describe(ServiceState? s)
    {
        if (s is null) return Txt.ServiceMissingState;
        var label = Txt.StartName(s.Start, false, false);
        var extra = new List<string>();
        if (s.DelayedAutoStart is { } d) extra.Add($"Delayed={d}");
        if (s.StartOverride.Count > 0) extra.Add("Override=" + string.Join(";", s.StartOverride.Select(o => $"{o.Key}={o.Value}")));
        return extra.Count == 0 ? label : $"{label} ({string.Join("; ", extra)})";
    }

    public static List<ServiceDiff> Compare(ServiceSnapshot from, ServiceSnapshot to)
    {
        var before = from.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var after = to.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var names = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.InvariantCultureIgnoreCase);
        var list = new List<ServiceDiff>();
        foreach (var name in names)
        {
            var old = before.GetValueOrDefault(name);
            var now = after.GetValueOrDefault(name);
            var a = FormatState(old); var b = FormatState(now);
            if (a != b) list.Add(new ServiceDiff(old?.Name ?? now!.Name, a, b, old, now));
        }
        return list;
    }

    /// <summary>PowerShell's Export-Csv: quotes on every field, UTF-8 with BOM.</summary>
    public static void ExportCsv(IEnumerable<ServiceDiff> diffs, string path)
    {
        var sb = new StringBuilder("\"Name\",\"Before\",\"After\"\r\n");
        foreach (var d in diffs) sb.Append(Csv(d.Name)).Append(',').Append(Csv(d.Before)).Append(',').Append(Csv(d.After)).Append("\r\n");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
        File.Move(tmp, path, overwrite: true);
        static string Csv(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Report name as in the script: TheKOS.Services.Diff.csv for the default comparison.</summary>
    public static string DiffFileName(string from, string to) =>
        from == Baseline && to == KosSnapshot ? "TheKOS.Services.Diff.csv" : $"Diff.{from}.vs.{to}.csv";

    // ---------- capture ----------

    /// <summary>
    /// Capture/Save-Snapshot. The Windows.Default.Services snapshot isn't replaced without <paramref name="force"/>.
    /// Writes to .tmp, validates and only then swaps the files, keeping the previous version as .previous.
    /// </summary>
    public static CaptureResult Capture(string name, bool full, bool force, IProgress<(int Done, int Total)>? progress = null)
    {
        if (!IsValidName(name) || name == Current) throw new InvalidOperationException(Txt.InvalidSnapshotName);
        System.IO.Directory.CreateDirectory(Directory);
        if (name == Baseline && !force && File.Exists(FilePath(name, ".json")))
        {
            Log.Info($"Service snapshot: baseline {name} already exists; kept.");
            return new CaptureResult(name, 0, true, 0);
        }
        var entries = ReadLive(progress);
        foreach (var e in entries)
            if (e.Start is < 0 or > 4) throw new InvalidOperationException(Txt.UnexpectedStart(e.Name));

        var jsonPath = FilePath(name, ".json");
        var regPath = FilePath(name, ".reg");
        var pairs = new List<(string Temp, string Final)> { (jsonPath + ".tmp", jsonPath), (regPath + ".tmp", regPath) };
        File.WriteAllText(jsonPath + ".tmp", ToJson(name, DateTimeOffset.Now, Environment.MachineName, entries), new UTF8Encoding(true));
        File.WriteAllText(regPath + ".tmp", ToReg(entries), new UnicodeEncoding(false, true));
        if (Parse(name, File.ReadAllText(jsonPath + ".tmp"), jsonPath).Services.Count != entries.Count)
            throw new InvalidOperationException(Txt.SnapshotValidationFailed);
        if (File.ReadAllLines(regPath + ".tmp").Count(l => l.StartsWith("\"Start\"=dword:", StringComparison.Ordinal)) != entries.Count)
            throw new InvalidOperationException(Txt.RegValidationFailed);

        var skipped = 0;
        if (full)
        {
            // The full export of the Services key is the only way to recover services that KOS removes.
            var fullPath = FilePath(name, ".full.reg");
            using (var w = new StreamWriter(fullPath + ".tmp", false, new UnicodeEncoding(false, true)))
                skipped = RegExport.Export(RegistryHive.LocalMachine, ServicesKey, w);
            pairs.Add((fullPath + ".tmp", fullPath));
        }
        foreach (var (temp, final) in pairs)
        {
            if (File.Exists(final)) File.Copy(final, final + ".previous", overwrite: true);
            File.Move(temp, final, overwrite: true);
        }
        Log.Info($"Service snapshot captured: {name} ({entries.Count} services{(full ? $", full export, {skipped} keys skipped" : "")})");
        return new CaptureResult(name, entries.Count, false, skipped);
    }

    /// <summary>Same format as the script's ConvertTo-Json (property order and types).</summary>
    internal static string ToJson(string name, DateTimeOffset capturedAt, string computer, IReadOnlyList<ServiceState> entries)
    {
        var services = new JsonArray();
        foreach (var e in entries)
        {
            var ov = new JsonObject();
            foreach (var (k, v) in e.StartOverride) ov[k] = v;
            services.Add(new JsonObject
            {
                ["Name"] = e.Name,
                ["Start"] = e.Start,
                ["DelayedAutoStart"] = e.DelayedAutoStart,
                ["StartOverride"] = ov,
            });
        }
        var root = new JsonObject
        {
            ["SchemaVersion"] = SchemaVersion,
            ["Snapshot"] = name,
            ["CapturedAt"] = capturedAt.ToString("o", CultureInfo.InvariantCulture),
            ["Computer"] = computer,
            ["ServiceCount"] = entries.Count,
            ["Services"] = services,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reference .reg (Start only), UTF-16 like the script's Set-Content -Encoding Unicode.</summary>
    internal static string ToReg(IEnumerable<ServiceState> entries)
    {
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var e in entries)
            sb.Append("\r\n[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\").Append(e.Name).Append("]\r\n")
              .Append("\"Start\"=dword:").Append(e.Start.ToString("x8")).Append("\r\n");
        return sb.ToString();
    }

    // ---------- restore ----------

    /// <summary>Restore checks before any write (also used by the elevated process).</summary>
    public static (ServiceSnapshot Snapshot, List<string> Names) PrepareRestore(string snapshot, IEnumerable<string> services)
    {
        var names = services.SelectMany(s => s.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!IsValidName(snapshot) || names.Count == 0) throw new InvalidOperationException(Txt.RestoreNeedsNames);
        if (snapshot == Current) throw new InvalidOperationException(Txt.RestoreFromCurrent);
        var data = Load(snapshot);
        var map = data.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var n in names)
        {
            if (!IsValidName(n)) throw new InvalidOperationException(Txt.InvalidServiceName(n));
            if (!map.ContainsKey(n)) throw new InvalidOperationException(Txt.ServiceNotInSnapshot(n));
            using var key = root.OpenSubKey($@"{ServicesKey}\{n}");
            if (key is null) throw new InvalidOperationException(Txt.ServiceDeleted(n));
        }
        return (data, names);
    }

    /// <summary>
    /// Restore: saves PreRestore.&lt;date&gt; and restores Start, DelayedAutoStart and StartOverride only for the named
    /// services. Removed services are never recreated. A failure on one service doesn't stop the others.
    /// </summary>
    public static RestoreResult Restore(string snapshot, IReadOnlyList<string> services)
    {
        var (data, names) = PrepareRestore(snapshot, services);
        var map = data.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var safety = "PreRestore." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Capture(safety, full: false, force: false);
        Log.Info($"Service restore from {snapshot}: safety snapshot {safety} saved");
        var results = new List<ServiceRestoreItem>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var n in names)
        {
            var item = map[n];
            try
            {
                try { RestoreDirect(root, n, item); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Protected key: same values through RegistrySetting, which uses the KOS PowerRun (TrustedInstaller).
                    Log.Warn($"Service restore: access denied on {n}; writing through TrustedInstaller.");
                    RestoreViaSettings(n, item);
                }
                results.Add(new ServiceRestoreItem(n, true, null));
                Log.Info($"Service restore: {n} restored to {FormatState(item)}");
            }
            catch (Exception ex)
            {
                results.Add(new ServiceRestoreItem(n, false, ex.Message));
                Log.Error($"Service restore: {n} failed", ex);
            }
        }
        return new RestoreResult(safety, results);
    }

    static void RestoreDirect(RegistryKey root, string n, ServiceState item)
    {
        using var key = root.OpenSubKey($@"{ServicesKey}\{n}", writable: true) ?? throw new InvalidOperationException(Txt.ServiceDeleted(n));
        key.SetValue("Start", item.Start, RegistryValueKind.DWord);
        if (item.DelayedAutoStart is { } d) key.SetValue("DelayedAutoStart", d, RegistryValueKind.DWord);
        else if (key.GetValue("DelayedAutoStart") is not null) key.DeleteValue("DelayedAutoStart");
        var existing = key.OpenSubKey("StartOverride", writable: true);
        if (existing is null && item.StartOverride.Count == 0) return;
        using var ov = existing ?? key.CreateSubKey("StartOverride", writable: true);
        foreach (var v in ov.GetValueNames())
            if (ov.GetValueKind(v) == RegistryValueKind.DWord && !item.StartOverride.ContainsKey(v)) ov.DeleteValue(v);
        foreach (var (k, v) in item.StartOverride) ov.SetValue(k, v, RegistryValueKind.DWord);
    }

    static void RestoreViaSettings(string n, ServiceState item)
    {
        var path = $@"{ServicesKey}\{n}";
        new Settings.RegistrySetting(Settings.Hive.LocalMachine, path, "Start", RegistryValueKind.DWord).Write(item.Start.ToString());
        new Settings.RegistrySetting(Settings.Hive.LocalMachine, path, "DelayedAutoStart", RegistryValueKind.DWord).Write(item.DelayedAutoStart?.ToString());
        var current = ReadLive(n)?.StartOverride ?? new Dictionary<string, int>();
        foreach (var k in current.Keys.Where(k => !item.StartOverride.ContainsKey(k)))
            new Settings.RegistrySetting(Settings.Hive.LocalMachine, path + @"\StartOverride", k, RegistryValueKind.DWord).Write(null);
        foreach (var (k, v) in item.StartOverride)
            new Settings.RegistrySetting(Settings.Hive.LocalMachine, path + @"\StartOverride", k, RegistryValueKind.DWord).Write(v.ToString());
    }

    /// <summary>Confirms by re-reading the registry: does each service's state match the snapshot's?</summary>
    public static List<ServiceRestoreItem> Verify(ServiceSnapshot snapshot, IEnumerable<string> names)
    {
        var map = snapshot.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        return names.Select(n =>
        {
            try
            {
                var live = ReadLive(n);
                if (live is null) return new ServiceRestoreItem(n, false, Txt.ServiceDeleted(n));
                var expected = FormatState(map[n]);
                var actual = FormatState(live);
                return expected == actual ? new ServiceRestoreItem(n, true, null) : new ServiceRestoreItem(n, false, Txt.RestoreMismatch(Describe(live), Describe(map[n])));
            }
            catch (Exception ex) { return new ServiceRestoreItem(n, false, ex.Message); }
        }).ToList();
    }
}
