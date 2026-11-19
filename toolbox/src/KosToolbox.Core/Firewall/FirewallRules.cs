using KosToolbox.Core.Text;

namespace KosToolbox.Core.Firewall;

/// <summary>The edits the Firewall page makes to the rule list, and its presets (pure: tested).</summary>
public static class FirewallRules
{
    /// <summary>Services Windows Update needs out: the update agent, background transfers, delivery optimization and the orchestrator.</summary>
    public static readonly string[] WindowsUpdateServices = ["wuauserv", "BITS", "DoSvc", "UsoSvc"];

    /// <summary>
    /// The same program, service or Store app in the same direction (custom rules are never the same: each is its own). The page
    /// keeps one such rule per target, so choosing again changes it instead of adding another.
    /// </summary>
    public static bool Same(FirewallRule a, FirewallRule b) =>
        a.Kind == b.Kind && a.Kind != FirewallRuleKind.Custom && a.Direction == b.Direction && a.Kind switch
        {
            FirewallRuleKind.App => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
            FirewallRuleKind.Service => string.Equals(a.Service, b.Service, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(a.Sid, b.Sid, StringComparison.Ordinal),
        };

    /// <summary>The rule for <paramref name="target"/>: none (null) removes it; allow or block adds it or changes the one there (and turns it on).</summary>
    public static void Choose(FirewallConfig config, FirewallRule target, bool? allow)
    {
        if (allow is not { } a) { config.Rules.RemoveAll(r => Same(r, target)); return; }
        var existing = config.Rules.Where(r => Same(r, target)).ToList();
        foreach (var r in existing) (r.Allow, r.Enabled) = (a, true);
        if (existing.Count == 0) { target.Allow = a; target.Enabled = true; config.Rules.Add(target); }
    }

    /// <summary>What the target's rule is now: allow, block, or null with none.</summary>
    public static bool? Current(FirewallConfig config, FirewallRule target) => config.Rules.FirstOrDefault(r => Same(r, target))?.Allow;

    /// <summary>Adds the custom rules no custom rule has the name of yet; returns how many were added.</summary>
    public static int AddMissing(FirewallConfig config, IEnumerable<FirewallRule> rules)
    {
        var added = 0;
        foreach (var rule in rules)
        {
            if (config.Rules.Any(r => r.Kind == FirewallRuleKind.Custom && string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase))) continue;
            config.Rules.Add(rule);
            added++;
        }
        return added;
    }

    /// <summary>Time sync: UDP out to port 123.</summary>
    public static FirewallRule[] Ntp() =>
        [new() { Kind = FirewallRuleKind.Custom, Name = Txt.FwPresetNtpRule, Protocol = Wfp.ProtocolUdp, RemotePorts = "123" }];

    /// <summary>Finding and being found on the local network (NetBIOS, SSDP/UPnP, WS-Discovery, mDNS, LLMNR, file sharing): inbound, by local port.</summary>
    public static FirewallRule[] NetworkDiscovery() =>
    [
        new() { Kind = FirewallRuleKind.Custom, Name = Txt.FwPresetDiscoveryUdp, Direction = FirewallDirection.In, Protocol = Wfp.ProtocolUdp, LocalPorts = "137, 138, 1900, 3702, 5353, 5355" },
        new() { Kind = FirewallRuleKind.Custom, Name = Txt.FwPresetDiscoveryTcp, Direction = FirewallDirection.In, Protocol = Wfp.ProtocolTcp, LocalPorts = "139, 445, 2869, 5357, 5358" },
    ];
}
