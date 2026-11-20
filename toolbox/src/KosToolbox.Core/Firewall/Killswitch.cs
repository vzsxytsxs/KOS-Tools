using System.Net;

namespace KosToolbox.Core.Firewall;

/// <summary>
/// KOS Engine's network killswitch (a hotkey): while it's on, every packet in and out is dropped, open connections included, and so
/// is what this PC forwards for WSL, Hyper-V, Docker or a hotspot; loopback stays, and, unless KOS Firewall's option says otherwise
/// (<see cref="FirewallConfig.KillswitchBlocksLan"/>), the local network, except DNS (port 53 and 853 to the router would still reach
/// the internet). Its filters are in a dynamic session with a sublayer of their own: they vanish when the engine closes or crashes
/// (the network can't stay cut without it), KOS Firewall's changes never touch them, and it works with KOS Firewall off.
/// </summary>
public static class Killswitch
{
    public static readonly Guid SubLayerKey = new("b3f6aa5f-a33e-44b7-bddd-c3397f834bc0");
    public const byte WeightLoopback = 15, WeightDns = 14, WeightLocal = 13, WeightBlock = 0;

    /// <summary>A condition added to each block of one kind (per IP version where it's an address), for a live test that blocks nothing real.</summary>
    public sealed record Scope(WfpCondition Transport, WfpCondition ForwardV4, WfpCondition ForwardV6, WfpCondition DnsV4, WfpCondition DnsV6);

    // The local network: private and link-local addresses, multicast and broadcast (DHCP, discovery).
    static readonly (string Address, int Prefix)[] LocalV4 =
        [("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16), ("224.0.0.0", 4), ("255.255.255.255", 32)];
    static readonly (string Address, int Prefix)[] LocalV6 = [("fe80::", 10), ("fc00::", 7), ("ff00::", 8)];

    /// <summary>The filters: the transport layers (every packet, so connections already open are cut too) and the IP forward layers.</summary>
    public static List<WfpFilter> Plan(bool blockLan, Scope? scope = null)
    {
        var filters = new List<WfpFilter>();
        IReadOnlyList<WfpCondition> With(WfpCondition? scoped, params WfpCondition[] conditions) => scoped is null ? conditions : [.. conditions, scoped];
        foreach (var (transport, forward, local, v6) in new[]
                 {
                     (new[] { Wfp.LayerOutboundTransportV4, Wfp.LayerInboundTransportV4 }, Wfp.LayerIpForwardV4, LocalV4, false),
                     (new[] { Wfp.LayerOutboundTransportV6, Wfp.LayerInboundTransportV6 }, Wfp.LayerIpForwardV6, LocalV6, true),
                 })
        {
            var networks = local.Select(n => (IPAddress.Parse(n.Address), n.Prefix)).ToList();
            foreach (var layer in transport)
            {
                filters.Add(new("KOS killswitch: loopback", layer, WfpAction.Permit, WeightLoopback, [WfpCondition.Loopback()]));
                if (!blockLan)
                {
                    // conditions on the same field match any of them
                    filters.Add(new("KOS killswitch: DNS", layer, WfpAction.Block, WeightDns,
                        With(v6 ? scope?.DnsV6 : scope?.DnsV4, WfpCondition.RemotePort(53), WfpCondition.RemotePort(853))));
                    filters.Add(new("KOS killswitch: local network", layer, WfpAction.Permit, WeightLocal,
                        networks.Select(n => WfpCondition.RemoteNetwork(n.Item1, n.Prefix)).ToList()));
                }
                filters.Add(new("KOS killswitch: everything else", layer, WfpAction.Block, WeightBlock, With(scope?.Transport)));
            }
            if (!blockLan)
                filters.Add(new("KOS killswitch: forwarded to the local network", forward, WfpAction.Permit, WeightLocal,
                    networks.Select(n => WfpCondition.DestinationNetwork(n.Item1, n.Prefix)).ToList()));
            filters.Add(new("KOS killswitch: everything forwarded", forward, WfpAction.Block, WeightBlock, With(v6 ? scope?.ForwardV6 : scope?.ForwardV4)));
        }
        return filters;
    }

    /// <summary>Turns it on and reads the filters back; disposing the session it returns turns it off.</summary>
    public static WfpSession On(bool blockLan, Scope? scope = null)
    {
        var plan = Plan(blockLan, scope);
        var s = WfpSession.OpenSubLayer(SubLayerKey, "KOS killswitch");
        try
        {
            s.Transaction(() => { foreach (var f in plan) s.Add(f); });
            var count = s.Filters().Count;
            if (count != plan.Count) throw new InvalidOperationException($"The filter engine holds {count} killswitch filters; {plan.Count} were added.");
            return s;
        }
        catch { s.Dispose(); throw; }
    }
}
