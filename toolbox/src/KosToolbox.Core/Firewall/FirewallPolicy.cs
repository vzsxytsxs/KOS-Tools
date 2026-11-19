using System.Net;
using System.Net.Sockets;

namespace KosToolbox.Core.Firewall;

/// <summary>The filters to install and the rules left out (with the reason).</summary>
public sealed record FirewallPlan(IReadOnlyList<WfpFilter> Filters, IReadOnlyList<string> Skipped);

/// <summary>
/// Turns <see cref="FirewallConfig"/> into WFP filters (no system calls: tested as is). Allow-list: everything is blocked at the
/// lowest weight of KOS's sublayer; the built-in allows (loopback, DHCP, DNS, DNS over HTTPS/TLS for the DNS client service,
/// ICMPv6, the KOS programs) sit at the top, so no
/// rule can lock the PC out of its network; a rule's block outranks a rule's allow. Every filter is added twice: persistent
/// (held by the filter engine with no KOS process running) and boot-time (enforced while Windows starts, before the engine is up).
/// </summary>
public static class FirewallPolicy
{
    public const byte WeightBuiltIn = 15, WeightUntilRestart = 13, WeightBlock = 12, WeightAllow = 10, WeightDefault = 0;
    public const string NamePrefix = "KOS Firewall: ", DefaultBlockName = NamePrefix + "block everything else";
    const byte Tcp = Wfp.ProtocolTcp, Udp = Wfp.ProtocolUdp;
    const int MaxEntries = 64;

    [Flags]
    enum Layers
    {
        ConnectV4 = 1, ConnectV6 = 2, AcceptV4 = 4, AcceptV6 = 8,
        Connect = ConnectV4 | ConnectV6, Accept = AcceptV4 | AcceptV6, All = 15, V4 = ConnectV4 | AcceptV4, V6 = ConnectV6 | AcceptV6,
    }

    static readonly (Layers Layer, Guid Key)[] LayerKeys =
        [(Layers.ConnectV4, Wfp.LayerConnectV4), (Layers.ConnectV6, Wfp.LayerConnectV6), (Layers.AcceptV4, Wfp.LayerAcceptV4), (Layers.AcceptV6, Wfp.LayerAcceptV6)];

    /// <param name="dnsClientSid">The DNS client service's SID (Dnscache): with DNS over HTTPS or TLS on, names go out on 443/853.</param>
    public static FirewallPlan Build(FirewallConfig config, IEnumerable<string> kosPrograms, Func<string, bool> exists, string? dnsClientSid = null)
    {
        var filters = new List<WfpFilter>();
        var skipped = new List<string>();
        void Add(string name, Layers layers, WfpAction action, byte weight, List<WfpCondition> conditions, bool untilRestart = false)
        {
            foreach (var (layer, key) in LayerKeys)
                if ((layers & layer) != 0)
                    if (untilRestart) filters.Add(new WfpFilter(name, key, action, weight, conditions, UntilRestart: true)); // gone at a restart: no boot-time copy
                    else
                        foreach (var boot in new[] { false, true })
                            filters.Add(new WfpFilter(NamePrefix + name, key, action, weight, conditions, boot));
        }

        // Built-in allows: the PC keeps its addresses, names and local traffic whatever the rules say.
        Add("loopback", Layers.All, WfpAction.Permit, WeightBuiltIn, [WfpCondition.Loopback()]);
        Add("DHCP", Layers.V4, WfpAction.Permit, WeightBuiltIn, [WfpCondition.Protocol(Udp), WfpCondition.LocalPort(68), WfpCondition.RemotePort(67)]);
        Add("DHCPv6", Layers.V6, WfpAction.Permit, WeightBuiltIn, [WfpCondition.Protocol(Udp), WfpCondition.LocalPort(546), WfpCondition.RemotePort(547)]);
        Add("DNS", Layers.Connect, WfpAction.Permit, WeightBuiltIn, [WfpCondition.Protocol(Udp), WfpCondition.Protocol(Tcp), WfpCondition.RemotePort(53)]);
        if (IsSid(dnsClientSid, "S-1-5-80-"))
            Add("DNS over HTTPS/TLS", Layers.Connect, WfpAction.Permit, WeightBuiltIn,
                [WfpCondition.UserSid(dnsClientSid!), WfpCondition.Protocol(Tcp), WfpCondition.RemotePort(443), WfpCondition.RemotePort(853)]);
        Add("ICMPv6 (neighbor discovery)", Layers.V6, WfpAction.Permit, WeightBuiltIn, [WfpCondition.Protocol(Wfp.ProtocolIcmpV6)]);
        foreach (var program in kosPrograms.Where(exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Add(Path.GetFileName(program), Layers.Connect, WfpAction.Permit, WeightBuiltIn, [WfpCondition.App(program)]);

        foreach (var rule in config.Rules.Where(r => r.Enabled))
        {
            if (Problem(rule) is { } problem) { skipped.Add($"{Label(rule)}: {problem}"); continue; }
            if (rule.Path is { Length: > 0 } path && !exists(path)) { skipped.Add($"{Label(rule)}: {path} doesn't exist."); continue; }

            var identity = new List<WfpCondition>();
            if (rule.Path is { Length: > 0 }) identity.Add(WfpCondition.App(rule.Path));
            if (rule.Kind == FirewallRuleKind.Service) identity.Add(WfpCondition.UserSid(rule.Sid!));
            if (rule.Kind == FirewallRuleKind.StoreApp) identity.Add(WfpCondition.Package(rule.Sid!));
            if (rule.Protocol is { } protocol) identity.Add(WfpCondition.Protocol((byte)protocol));
            foreach (var (from, to) in Ports(rule.RemotePorts)!)
                identity.Add(from == to ? WfpCondition.RemotePort(from) : WfpCondition.RemotePorts(from, to));
            foreach (var (from, to) in Ports(rule.LocalPorts)!)
                identity.Add(from == to ? WfpCondition.LocalPort(from) : WfpCondition.LocalPorts(from, to));

            var layers = rule.Direction switch { FirewallDirection.Out => Layers.Connect, FirewallDirection.In => Layers.Accept, _ => Layers.All };
            if (rule.Protocol == Wfp.ProtocolIcmp) layers &= Layers.V4;
            if (rule.Protocol == Wfp.ProtocolIcmpV6) layers &= Layers.V6;
            var addresses = Addresses(rule.RemoteAddresses)!;
            var v4 = addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).ToList();
            // An address condition only fits the layers of its IP version: each version gets its own filters.
            if (addresses.Count > 0 && v4.Count == 0) layers &= Layers.V6;
            if (addresses.Count > 0 && v6.Count == 0) layers &= Layers.V4;
            if (layers == 0) { skipped.Add($"{Label(rule)}: the protocol and the addresses are of different IP versions."); continue; }

            // A rule until restart only allows, above the block rules.
            var allow = rule.Allow || rule.UntilRestart;
            var action = allow ? WfpAction.Permit : WfpAction.Block;
            var weight = rule.UntilRestart ? WeightUntilRestart : allow ? WeightAllow : WeightBlock;
            var name = rule.UntilRestart ? TemporaryName(rule) : (allow ? "allow " : "block ") + Label(rule);
            if ((layers & Layers.V4) != 0)
                Add(name, layers & Layers.V4, action, weight, [.. identity, .. v4.Select(a => WfpCondition.RemoteNetwork(a.Address, a.Prefix))], rule.UntilRestart);
            if ((layers & Layers.V6) != 0)
                Add(name, layers & Layers.V6, action, weight, [.. identity, .. v6.Select(a => WfpCondition.RemoteNetwork(a.Address, a.Prefix))], rule.UntilRestart);
        }

        Add("block everything else", Layers.All, WfpAction.Block, WeightDefault, []);
        return new FirewallPlan(filters, skipped);
    }

    /// <summary>The filter name of a rule until restart: its id in it, so the filters left in the engine tell which ones still hold.</summary>
    public static string TemporaryName(FirewallRule rule) => $"{NamePrefix}allow {Label(rule)} until restart [{rule.Id}]";

    /// <summary>
    /// Whether a rule until restart still holds: one of its filters is in the filter engine (<paramref name="filterNames"/>, the KOS
    /// filters there now). After a restart none is, whatever the clock says.
    /// </summary>
    public static bool Holds(FirewallRule rule, IReadOnlySet<string> filterNames) => rule.UntilRestart && filterNames.Contains(TemporaryName(rule));

    /// <summary>
    /// Drops the rules until restart that no longer hold, except <paramref name="added"/> (rule ids just added by the change, whose
    /// filters don't exist yet). Returns how many were dropped.
    /// </summary>
    public static int DropExpired(FirewallConfig config, IReadOnlySet<string> filterNames, IReadOnlySet<string> added) =>
        config.Rules.RemoveAll(r => r.UntilRestart && !added.Contains(r.Id) && !Holds(r, filterNames));

    public static string Label(FirewallRule rule) =>
        rule.Name is { Length: > 0 } n ? n : rule.Path is { Length: > 0 } p ? Path.GetFileName(p) : rule.Service ?? rule.Sid ?? "rule";

    /// <summary>Null if the rule can be turned into filters; otherwise the reason (English).</summary>
    public static string? Problem(FirewallRule rule)
    {
        if (rule.Path is { Length: > 0 } path && !Path.IsPathFullyQualified(path)) return "the program's path isn't a full path.";
        switch (rule.Kind)
        {
            case FirewallRuleKind.App when rule.Path is not { Length: > 0 }: return "no program.";
            case FirewallRuleKind.Service when !IsSid(rule.Sid, "S-1-5-80-"): return "no valid service SID.";
            case FirewallRuleKind.StoreApp when !IsSid(rule.Sid, "S-1-15-2-"): return "no valid package SID.";
        }
        if (rule.Protocol is < 0 or > 255) return "the protocol must be 0 to 255.";
        if (Ports(rule.RemotePorts) is not { } remote) return "the remote ports aren't valid (e.g. 80, 443, 27000-27050).";
        if (Ports(rule.LocalPorts) is not { } local) return "the local ports aren't valid (e.g. 80, 443, 27000-27050).";
        if ((remote.Count > 0 || local.Count > 0) && rule.Protocol is not (Tcp or Udp)) return "ports need TCP or UDP.";
        if (Addresses(rule.RemoteAddresses) is null) return "the remote addresses aren't valid (e.g. 1.1.1.1, 10.0.0.0/8, fe80::/10).";
        return null;
    }

    /// <summary>"S-1-..." made of digits and dashes only, starting with <paramref name="prefix"/> (it goes into an SDDL string).</summary>
    public static bool IsSid(string? sid, string prefix) =>
        sid is { Length: > 0 and <= 200 } && sid.StartsWith(prefix, StringComparison.Ordinal) && sid.Length > prefix.Length
        && sid[2..].All(c => c is >= '0' and <= '9' or '-') && !sid.EndsWith('-') && !sid.Contains("--", StringComparison.Ordinal);

    /// <summary>"80, 443, 1000-2000" as ranges; empty for null/blank; null if anything is invalid.</summary>
    public static List<(ushort From, ushort To)>? Ports(string? text)
    {
        var list = new List<(ushort, ushort)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            var (a, b) = dash < 0 ? (part, part) : (part[..dash].Trim(), part[(dash + 1)..].Trim());
            if (!ushort.TryParse(a, out var from) || !ushort.TryParse(b, out var to) || from == 0 || to < from) return null;
            list.Add((from, to));
        }
        return list.Count is > 0 and <= MaxEntries ? list : null;
    }

    /// <summary>"1.1.1.1, 10.0.0.0/8, fe80::/10" as networks; empty for null/blank; null if anything is invalid.</summary>
    public static List<(IPAddress Address, int Prefix)>? Addresses(string? text)
    {
        var list = new List<(IPAddress, int)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = part.IndexOf('/');
            if (!IPAddress.TryParse(slash < 0 ? part : part[..slash], out var address)) return null;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return null; // "fe80::1%5": no zone ids
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return null;
            // IPAddress also takes old short forms ("10.1" = 10.0.0.1): an IPv4 address here must have its four parts.
            if (address.AddressFamily == AddressFamily.InterNetwork && (slash < 0 ? part : part[..slash]).Count(c => c == '.') != 3) return null;
            var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = max;
            if (slash >= 0 && (!int.TryParse(part[(slash + 1)..], out prefix) || prefix < 0 || prefix > max)) return null;
            list.Add((address, prefix));
        }
        return list.Count is > 0 and <= MaxEntries ? list : null;
    }
}
