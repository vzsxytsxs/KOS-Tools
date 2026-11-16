using System.Globalization;
using System.Text.Json;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Network;

/// <summary>
/// A global TCP/UDP netsh parameter. Empty <see cref="Options"/> = a number (Min–Max). <see cref="Value"/> is what
/// "netsh int tcp/udp show global" currently reports (null if it couldn't be read).
/// </summary>
public sealed record NetGlobal(string Key, string Label, string Protocol, IReadOnlyList<string> Options, int Min, int Max, string? Value)
{
    public bool IsNumber => Options.Count == 0;
}

/// <summary>A protocol/service bound to the adapter (the connection's "Properties" list in Windows).</summary>
public sealed record NetBinding(string ComponentId, string DisplayName, bool Enabled);

/// <summary>
/// Global TCP/UDP parameters (netsh int tcp/udp set global) and the adapter's bindings (Enable/Disable-NetAdapterBinding).
/// Writing needs administrator (the toolbox runs elevated); the accepted values are a fixed list defined here.
/// </summary>
public static class NetGlobals
{
    static readonly string[] OnOff = { "enabled", "disabled" };

    // Exact label in netsh's English output → "set global" parameter.
    static readonly (string Key, string Label, string Protocol, string[] Options, int Min, int Max)[] Known =
    {
        ("rss", "Receive-Side Scaling State", "tcp", OnOff, 0, 0),
        ("autotuninglevel", "Receive Window Auto-Tuning Level", "tcp", new[] { "disabled", "highlyrestricted", "restricted", "normal", "experimental" }, 0, 0),
        // The supplemental "internet" template (set global's "Add-On Congestion Control Provider" no longer applies on Windows 10/11).
        ("congestion", "Congestion Control Provider", "supp", new[] { "cubic", "newreno", "ctcp", "dctcp", "bbr2", "default" }, 0, 0),
        ("ecncapability", "ECN Capability", "tcp", new[] { "enabled", "disabled", "default" }, 0, 0),
        ("timestamps", "RFC 1323 Timestamps", "tcp", new[] { "enabled", "disabled", "allowed", "default" }, 0, 0),
        ("initialrto", "Initial RTO", "tcp", Array.Empty<string>(), 300, 3000),
        ("rsc", "Receive Segment Coalescing State", "tcp", OnOff, 0, 0),
        ("nonsackrttresiliency", "Non Sack Rtt Resiliency", "tcp", OnOff, 0, 0),
        ("maxsynretransmissions", "Max SYN Retransmissions", "tcp", Array.Empty<string>(), 2, 8),
        ("fastopen", "Fast Open", "tcp", OnOff, 0, 0),
        ("fastopenfallback", "Fast Open Fallback", "tcp", OnOff, 0, 0),
        ("hystart", "HyStart", "tcp", OnOff, 0, 0),
        ("prr", "Proportional Rate Reduction", "tcp", OnOff, 0, 0),
        ("pacingprofile", "Pacing Profile", "tcp", new[] { "off", "initialwindow", "slowstart", "always", "default" }, 0, 0),
        ("uro", "Receive Offload State", "udp", OnOff, 0, 0),
        ("uso", "Send Offload State", "udp", OnOff, 0, 0),
        // Set-NetOffloadGlobalSetting (PowerShell).
        ("taskoffload", "Task Offload", "offload", OnOff, 0, 0),
        ("packetcoalescingfilter", "Packet Coalescing Filter", "offload", OnOff, 0, 0),
        ("networkdirect", "NetworkDirect (RDMA)", "offload", OnOff, 0, 0),
        // NetworkDirectAcrossIPSubnets is left out: on client Windows, Set answers "available on servers only".
    };

    /// <summary>Parameter name in Set-NetOffloadGlobalSetting.</summary>
    static readonly Dictionary<string, string> OffloadNames = new()
    {
        ["taskoffload"] = "TaskOffload", ["packetcoalescingfilter"] = "PacketCoalescingFilter", ["networkdirect"] = "NetworkDirect",
    };

    /// <summary>Only the PowerShell error message (without "At line:1 char:1" and the rest of the report).</summary>
    static string PsError(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.OrdinalIgnoreCase) && !l.StartsWith('+'))
            is { } first && first.Contains(" : ") ? first[(first.IndexOf(" : ", StringComparison.Ordinal) + 3)..] : output.Trim();

    static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");
    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>
    /// Reads everything at once, in parallel: the three "netsh … show" calls and a single PowerShell call with the global
    /// offloads and the adapter's bindings (starting PowerShell is the slowest part). On a Windows in another language
    /// the netsh labels change: the value stays null. Without <paramref name="instanceId"/>, bindings are not read.
    /// </summary>
    public static async Task<(List<NetGlobal> Globals, List<NetBinding> Bindings)> ReadAllAsync(string? instanceId)
    {
        // CIM directly instead of Get-NetAdapter/Get-NetOffloadGlobalSetting: loading the NetAdapter module costs ~1.5 s.
        // In the CIM class the offloads are numbers (0 = Disabled, 1 = Enabled).
        var bindingsScript = instanceId is null ? "" :
            $"$a = Get-CimInstance -Namespace $ns -ClassName MSFT_NetAdapter | Where-Object PnPDeviceID -eq {PsQuote(instanceId)} | Select-Object -First 1; " +
            "if ($a) { 'BINDINGS=' + (Get-CimInstance -Namespace $ns -ClassName MSFT_NetAdapterBindingSettingData | Where-Object Name -eq $a.Name | Select-Object ComponentID,DisplayName,Enabled | ConvertTo-Json -Compress) }; ";
        var psScript = "$ns = 'root/StandardCimv2'; $o = Get-CimInstance -Namespace $ns -ClassName MSFT_NetOffloadGlobalSetting; " +
                       string.Join("; ", OffloadNames.Values.Select(n => $"'{n}=' + ('Disabled','Enabled')[$o.{n}]")) + "; " + bindingsScript;
        var tasks = new[]
        {
            ProcessRunner.RunAsync(Netsh, "int tcp show global", timeout: TimeSpan.FromSeconds(15)),
            ProcessRunner.RunAsync(Netsh, "int udp show global", timeout: TimeSpan.FromSeconds(15)),
            ProcessRunner.RunAsync(Netsh, "int tcp show supplemental", timeout: TimeSpan.FromSeconds(15)), // default template: "internet"
            ProcessRunner.RunAsync(PowerShell, $"-NoProfile -NonInteractive -Command \"{psScript.Replace("\"", "\\\"")}\"", timeout: TimeSpan.FromSeconds(45)),
        };
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (protocol, r) in new[] { ("tcp", results[0]), ("udp", results[1]), ("supp", results[2]) })
            foreach (var line in r.Output.Split('\n'))
            {
                var i = line.IndexOf(':');
                if (i > 0) values[$"{protocol}|{line[..i].Trim()}"] = line[(i + 1)..].Trim();
            }
        var bindings = new List<NetBinding>();
        foreach (var line in results[3].Output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("BINDINGS=", StringComparison.Ordinal)) bindings = ParseBindings(line["BINDINGS=".Length..]);
            else if (line.Split('=', 2) is [var name, var value] && OffloadNames.FirstOrDefault(o => o.Value == name.Trim()).Key is { } key)
                values[$"offload|{Known.First(k => k.Key == key).Label}"] = value.Trim();
        }
        var globals = Known.Select(k => new NetGlobal(k.Key, k.Label, k.Protocol, k.Options, k.Min, k.Max,
            values.TryGetValue($"{k.Protocol}|{k.Label}", out var v) ? v.ToLowerInvariant() : null)).ToList();
        return (globals, bindings);
    }

    static List<NetBinding> ParseBindings(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return new();
        try
        {
            using var doc = JsonDocument.Parse(json.StartsWith('[') ? json : $"[{json}]");
            return doc.RootElement.EnumerateArray().Select(e => new NetBinding(
                    e.GetProperty("ComponentID").GetString() ?? "", e.GetProperty("DisplayName").GetString() ?? "", e.GetProperty("Enabled").GetBoolean()))
                .Where(b => b.ComponentId.Length > 0).ToList();
        }
        catch (JsonException ex) { Log.Warn($"Reading adapter bindings failed: {ex.Message}"); return new(); }
    }

    /// <summary>Null if the value is accepted for the parameter; otherwise the reason.</summary>
    public static string? Problem(string key, string value)
    {
        if (Known.FirstOrDefault(k => k.Key == key) is not { Key: not null } k) return $"Unknown parameter: {key}";
        if (k.Options.Length > 0) return k.Options.Contains(value) ? null : "Not one of the options.";
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= k.Min && n <= k.Max ? null : $"Enter a number from {k.Min} to {k.Max}.";
    }

    /// <summary>netsh int tcp|udp set global key=value (only parameters and values from the list).</summary>
    public static void Set(string key, string value)
    {
        if (Problem(key, value) is { } problem) throw new ArgumentException(problem);
        if (!Elevation.IsAdmin) throw new InvalidOperationException("Changing TCP/UDP settings needs the toolbox running as administrator.");
        var k = Known.First(x => x.Key == key);
        if (k.Protocol == "offload")
        {
            // Values from the fixed list: Enabled/Disabled/Allowed/Blocked with a capital first letter.
            var pretty = char.ToUpperInvariant(value[0]) + value[1..];
            var ps = ProcessRunner.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"Set-NetOffloadGlobalSetting -{OffloadNames[key]} {pretty} -ErrorAction Stop\"", TimeSpan.FromSeconds(60));
            if (ps.ExitCode != 0) throw new InvalidOperationException($"{k.Label}: {PsError(ps.Output)}");
            Log.Info($"Set-NetOffloadGlobalSetting -{OffloadNames[key]} {pretty}");
            return;
        }
        var args = k.Protocol == "supp" ? $"int tcp set supplemental template=internet congestionprovider={value}" : $"int {k.Protocol} set global {key}={value}";
        var r = ProcessRunner.Run(Netsh, args, TimeSpan.FromSeconds(20));
        if (r.ExitCode != 0 || !r.Output.Contains("Ok.", StringComparison.OrdinalIgnoreCase) && r.Output.Trim().Length > 0)
            throw new InvalidOperationException($"{k.Label}: {r.Output.Trim()}");
        Log.Info($"netsh {args}");
    }

    // ---------- bindings ----------

    static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Enables/disables several bindings in a single PowerShell call (each call takes ~2 s to start). Returns the errors per
    /// component (empty = all good).
    /// </summary>
    public static List<string> SetBindings(string instanceId, IReadOnlyList<(string ComponentId, bool Enabled)> changes)
    {
        if (changes.Count == 0) return new();
        if (!Elevation.IsAdmin) throw new InvalidOperationException("Changing adapter bindings needs the toolbox running as administrator.");
        foreach (var (id, _) in changes)
            if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))) throw new ArgumentException($"Invalid component: {id}");
        var steps = string.Join("; ", changes.Select(c =>
            $"try {{ {(c.Enabled ? "Enable" : "Disable")}-NetAdapterBinding -Name $a.Name -ComponentID {c.ComponentId} -ErrorAction Stop; 'OK|{c.ComponentId}' }} " +
            $"catch {{ 'ERR|{c.ComponentId}|' + $_.Exception.Message }}"));
        var script = $"$a = Get-NetAdapter | Where-Object PnPDeviceID -eq {PsQuote(instanceId)} | Select-Object -First 1; " +
                     $"if (-not $a) {{ 'ERR|*|Adapter not found'; exit 1 }}; {steps}";
        var r = ProcessRunner.Run(PowerShell, $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"", TimeSpan.FromSeconds(120));
        var errors = new List<string>();
        foreach (var line in r.Output.Split('\n').Select(l => l.Trim()))
        {
            var parts = line.Split('|', 3);
            if (parts is ["OK", var ok]) Log.Info($"Network binding {ok} on {instanceId}: {(changes.First(c => c.ComponentId == ok).Enabled ? "on" : "off")}");
            else if (parts is ["ERR", var id, var message]) errors.Add($"{id}: {message}");
        }
        if (errors.Count == 0 && r.ExitCode != 0) errors.Add(PsError(r.Output));
        return errors;
    }
}
