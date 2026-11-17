using System.Globalization;
using System.Net.NetworkInformation;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Network;

/// <summary>A connected network adapter. <see cref="DriverKey"/> is the NNNN instance under Control\Class\{network class}.</summary>
public sealed record NetAdapter(string InstanceId, string Name, string DriverKey, bool Physical, string Status)
{
    /// <summary>Interface id (NetworkInterface.Id = NetCfgInstanceId); null without an active interface.</summary>
    public string? InterfaceId { get; init; }
}

/// <summary>An advanced property (the "Advanced" tab in Device Manager), described under Ndi\Params by the driver's INF.</summary>
public sealed record NetProperty(string Keyword, string Name, string Type, string? Default, string? Current,
    IReadOnlyList<KeyValuePair<string, string>> Options, long? Min, long? Max, long? Step, int Base, int MaxLength)
{
    public bool IsEnum => Type == "enum";
    public string Effective => Current ?? Default ?? "";
    /// <summary>Not in the driver's Ndi\Params: a known keyword (standard NDIS or vendor-specific) written straight to the registry.</summary>
    public bool Unlisted { get; init; }
}

public sealed record NetAdapterInfo(string Manufacturer, string HardwareId, string DriverVersion, string DriverFile, string RegistryKey);

public sealed class NetPropertyWrite
{
    public string Keyword { get; set; } = "";
    /// <summary>Null = delete the value (the driver goes back to the INF default).</summary>
    public string? Value { get; set; }
}

public sealed class NetAdapterJob
{
    public string InstanceId { get; set; } = "";
    public List<NetPropertyWrite> Writes { get; set; } = new();
    public bool Restart { get; set; }
    /// <summary>A <see cref="NetDns.Providers"/> key to set on the adapter's interface (only that, with nothing else).</summary>
    public string? Dns { get; set; }
    public bool DnsEncrypted { get; set; }
}

/// <summary>
/// Advanced network adapter properties, using the documented NDIS keyword layout (Ndi\Params\&lt;keyword&gt;:
/// ParamDesc, type, default, min/max/step/base, enum). Values are stored as REG_SZ under the instance key, as
/// Device Manager writes them; the driver only reads them when the adapter restarts.
/// </summary>
public static class NetAdapters
{
    public const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\" + NetClass;
    const int NcfPhysical = 0x4, NcfHidden = 0x8;
    static string Backups => Path.Combine(AppPaths.DataRoot, "network-backups.json");

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static List<NetAdapter> List(bool includeVirtual)
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var list = new List<NetAdapter>();
        using var hklm = Hklm;
        foreach (var d in PnpDevices.Enumerate(presentOnly: true).Where(d => d.Class.Equals("Net", StringComparison.OrdinalIgnoreCase)))
        {
            if (DriverKeyOf(d.InstanceId) is not { } driverKey) continue;
            using var k = hklm.OpenSubKey($@"{ClassRoot}\{driverKey}");
            if (k is null) continue;
            var flags = k.GetValue("Characteristics") as int? ?? 0;
            var physical = (flags & NcfPhysical) != 0 && (flags & NcfHidden) == 0;
            if (!physical && !includeVirtual) continue;
            var nic = nics.FirstOrDefault(n => n.Id.Equals(k.GetValue("NetCfgInstanceId") as string, StringComparison.OrdinalIgnoreCase));
            var status = nic is null ? Txt.NicNoInterface
                : nic.OperationalStatus == OperationalStatus.Up ? Txt.NicUp(nic.Speed) : Txt.NicDown;
            list.Add(new NetAdapter(d.InstanceId, d.Name, driverKey, physical, status) { InterfaceId = nic?.Id });
        }
        return list.OrderByDescending(a => a.Physical).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>"0001" from the instance's Driver value ("{class}\0001"); null if it isn't in the network class.</summary>
    internal static string? DriverKeyOf(string instanceId)
    {
        using var hklm = Hklm;
        using var k = hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        return ParseDriverKey(k?.GetValue("Driver") as string);
    }

    internal static string? ParseDriverKey(string? driver)
    {
        if (driver is null || !driver.StartsWith(NetClass + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var n = driver[(NetClass.Length + 1)..];
        return n.Length == 4 && n.All(char.IsAsciiDigit) ? n : null;
    }

    /// <summary>Adapter driver data for the page header (vendor, ids, version, file and key).</summary>
    public static NetAdapterInfo Info(NetAdapter a)
    {
        using var hklm = Hklm;
        using var k = hklm.OpenSubKey($@"{ClassRoot}\{a.DriverKey}");
        var ven = Between(a.InstanceId, "VEN_", '&');
        var dev = Between(a.InstanceId, "DEV_", '&');
        string? file = null;
        if (k?.OpenSubKey(@"Ndi") is { } ndi)
            using (ndi)
            using (var svc = ndi.GetValue("Service") is string service ? hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}") : null)
                file = (svc?.GetValue("ImagePath") as string) is { Length: > 0 } image ? Path.GetFileName(image.Trim('"')) : null;
        return new NetAdapterInfo(
            Str(k!, "ProviderName") ?? "—",
            ven is not null && dev is not null ? $"{ven}:{dev}" : a.InstanceId.Split('\\').ElementAtOrDefault(1) ?? "—",
            Str(k!, "DriverVersion") ?? "—",
            file ?? "—",
            a.DriverKey);
    }

    static string? Between(string s, string start, char end)
    {
        var i = s.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += start.Length;
        var j = s.IndexOf(end, i);
        return (j < 0 ? s[i..] : s[i..j]).ToUpperInvariant();
    }

    public static List<NetProperty> Properties(NetAdapter a)
    {
        using var hklm = Hklm;
        using var instance = hklm.OpenSubKey($@"{ClassRoot}\{a.DriverKey}");
        using var parameters = instance?.OpenSubKey(@"Ndi\Params");
        if (instance is null || parameters is null) return new();
        var list = new List<NetProperty>();
        foreach (var keyword in parameters.GetSubKeyNames())
        {
            using var p = parameters.OpenSubKey(keyword);
            if (p is null) continue;
            var options = new List<KeyValuePair<string, string>>();
            using (var e = p.OpenSubKey("enum"))
                if (e is not null)
                    foreach (var v in e.GetValueNames()) options.Add(new(v, e.GetValue(v) as string ?? v));
            list.Add(new NetProperty(keyword, Str(p, "ParamDesc") ?? keyword, (Str(p, "type") ?? "edit").ToLowerInvariant(), Str(p, "default"),
                Current(instance, keyword), options, Num(p, "min"), Num(p, "max"), Num(p, "step"),
                Str(p, "base") == "16" ? 16 : 10, (int)(Num(p, "LimitText") ?? 256)));
        }
        // Known keywords the driver doesn't list (Windows/the driver reads them from the registry anyway, if supported).
        foreach (var known in NetTweaks.Keywords)
        {
            if (list.Any(p => p.Keyword.Equals(known.Keyword, StringComparison.OrdinalIgnoreCase))) continue;
            var options = known.Options.Select(o => new KeyValuePair<string, string>(o.Value, o.Label)).ToList();
            list.Add(new NetProperty(known.Keyword, known.Name ?? known.Keyword, options.Count > 0 ? "enum" : "int", null, Current(instance, known.Keyword),
                options, known.Min, known.Max, null, 10, 16) { Unlisted = true });
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The adapter's value; a REG_DWORD as unsigned, the way it was written (HwOption masks reach 0xFFFFFFFF).</summary>
    static string? Current(RegistryKey instance, string keyword) =>
        instance.GetValue(keyword) is int i ? ((uint)i).ToString(CultureInfo.InvariantCulture) : Str(instance, keyword);
    static string? Str(RegistryKey k, string name) => k.GetValue(name)?.ToString()?.Trim();
    static long? Num(RegistryKey k, string name) => long.TryParse(Str(k, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Null when the driver's description accepts the value; otherwise the reason (shown in the UI).</summary>
    public static string? Problem(NetProperty p, string? value)
    {
        if (value is null) return null; // deleting always goes back to the driver default
        if (p.IsEnum) return p.Options.Any(o => o.Key == value) ? null : Txt.NicNotAnOption;
        if (p.Type is "int" or "long" or "word" or "dword")
        {
            var style = p.Base == 16 ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (!long.TryParse(value, style, CultureInfo.InvariantCulture, out var n)) return Txt.NicNotANumber(p.Base);
            if (p.Min is { } min && n < min || p.Max is { } max && n > max) return Txt.NicOutOfRange(p.Min, p.Max);
            if (p.Step is long step && step > 1 && (n - (p.Min ?? 0)) % step != 0) return Txt.NicStep(step);
            return null;
        }
        return value.Length > p.MaxLength || value.Any(char.IsControl) ? Txt.NicTextTooLong(p.MaxLength) : null;
    }

    /// <summary>Saves the original values (only on the first change), writes (elevated if needed), restarts and re-reads.</summary>
    public static async Task<bool> ApplyAsync(NetAdapter a, IReadOnlyList<NetPropertyWrite> writes, bool restart, CancellationToken ct = default)
    {
        var props = Properties(a);
        var all = JsonFile.Load<Dictionary<string, Dictionary<string, string?>>>(Backups);
        var saved = all.TryGetValue(a.InstanceId, out var s) ? s : all[a.InstanceId] = new();
        foreach (var w in writes)
            if (props.FirstOrDefault(p => p.Keyword == w.Keyword) is { } p) saved.TryAdd(w.Keyword, p.Current);
        JsonFile.Save(Backups, all);

        var job = new NetAdapterJob { InstanceId = a.InstanceId, Writes = writes.ToList(), Restart = restart };
        var reboot = false;
        if (Elevation.IsAdmin) reboot = await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { NetAdapter = job }, ct).ConfigureAwait(false);

        var after = Properties(a);
        foreach (var w in writes)
            if (after.FirstOrDefault(p => p.Keyword == w.Keyword)?.Current != w.Value) throw new InvalidOperationException(Txt.ChangeNotConfirmed);
        return reboot;
    }

    /// <summary>Restarts the adapter (disable then enable), writing nothing. True if Windows asks for a reboot.</summary>
    public static async Task<bool> RestartAsync(NetAdapter a, CancellationToken ct = default)
    {
        var job = new NetAdapterJob { InstanceId = a.InstanceId, Restart = true };
        if (Elevation.IsAdmin) return await Task.Run(() => Execute(job), ct).ConfigureAwait(false);
        await ElevatedRunner.RunAsync(new ElevatedJob { NetAdapter = job }, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Original values saved before each property's first change (empty if never changed).</summary>
    public static IReadOnlyDictionary<string, string?> Original(string instanceId) =>
        JsonFile.Load<Dictionary<string, Dictionary<string, string?>>>(Backups).GetValueOrDefault(instanceId) ?? new();

    /// <summary>Elevated side: connected network adapter, existing keywords and values accepted by the INF.</summary>
    internal static NetAdapter Validate(NetAdapterJob job)
    {
        if (job.Writes.Count > 200 || job.Writes.Count == 0 && !job.Restart && job.Dns is null || job.Dns is not null && (job.Writes.Count > 0 || job.Restart))
            throw new InvalidOperationException("Invalid network request.");
        if (job.Dns is not null) NetDns.Find(job.Dns);
        var adapter = List(includeVirtual: true).FirstOrDefault(x => x.InstanceId.Equals(job.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No connected network adapter has the id {job.InstanceId}.");
        var props = Properties(adapter);
        foreach (var w in job.Writes)
        {
            var p = props.FirstOrDefault(x => x.Keyword == w.Keyword) ?? throw new InvalidOperationException($"Unknown property: {w.Keyword}");
            if (Problem(p, w.Value) is { } problem) throw new InvalidOperationException($"{w.Keyword}: {problem}");
        }
        return adapter;
    }

    internal static bool Execute(NetAdapterJob job)
    {
        var adapter = Validate(job);
        if (job.Dns is not null)
        {
            NetDns.Execute(adapter.InterfaceId ?? throw new InvalidOperationException("The adapter has no active network interface."), NetDns.Find(job.Dns), job.DnsEncrypted);
            return false;
        }
        if (job.Writes.Count > 0)
        {
            using var hklm = Hklm;
            using var key = hklm.OpenSubKey($@"{ClassRoot}\{adapter.DriverKey}", writable: true)
                ?? throw new InvalidOperationException($"Driver key not found: {adapter.DriverKey}");
            foreach (var w in job.Writes)
            {
                if (w.Value is null) key.DeleteValue(w.Keyword, throwOnMissingValue: false);
                else
                {
                    // Keeps the type of an existing value (some drivers use REG_DWORD); new ones are REG_SZ, except the known DWORDs.
                    var kind = key.GetValueNames().Contains(w.Keyword, StringComparer.OrdinalIgnoreCase) ? key.GetValueKind(w.Keyword)
                        : NetTweaks.IsDword(w.Keyword) ? RegistryValueKind.DWord : RegistryValueKind.String;
                    if (kind == RegistryValueKind.DWord) key.SetValue(w.Keyword, unchecked((int)uint.Parse(w.Value, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                    else key.SetValue(w.Keyword, w.Value, RegistryValueKind.String);
                }
                Log.Info($"Network {adapter.Name}: {w.Keyword} = {w.Value ?? "(default)"}");
            }
        }
        return job.Restart && PnpDevices.Restart(adapter.InstanceId);
    }
}
