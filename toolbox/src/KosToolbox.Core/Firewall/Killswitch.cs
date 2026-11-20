using System.Net;

namespace KosToolbox.Core.Firewall;

/// <summary>
/// KOS Engine's network killswitch (a hotkey): while it's on, every packet in and out is dropped, open connections included,
/// except loopback and, unless KOS Firewall's option says otherwise (<see cref="FirewallConfig.KillswitchBlocksLan"/>), the local
/// network. Its filters are in a dynamic session with a sublayer of their own: they vanish when the engine closes or crashes (the
/// network can't stay cut without it), KOS Firewall's changes never touch them, and it works with KOS Firewall off.
/// </summary>
public static class Killswitch
{
    public static readonly Guid SubLayerKey = new("b3f6aa5f-a33e-44b7-bddd-c3397f834bc0");
    public const byte WeightLoopback = 15, WeightLocal = 14, WeightBlock = 0;

    // The local network: private and link-local addresses, multicast and broadcast (DHCP, discovery).
    static readonly (string Address, int Prefix)[] LocalV4 =
        [("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16), ("224.0.0.0", 4), ("255.255.255.255", 32)];
    static readonly (string Address, int Prefix)[] LocalV6 = [("fe80::", 10), ("fc00::", 7), ("ff00::", 8)];

    /// <summary>
    /// The filters, on the transport layers (every packet, so connections already open are cut too). <paramref name="scope"/> is
    /// added to each block: a live test that blocks nothing real.
    /// </summary>
    public static List<WfpFilter> Plan(bool blockLan, WfpCondition? scope = null)
    {
        var filters = new List<WfpFilter>();
        foreach (var (layers, local) in new[]
                 {
                     (new[] { Wfp.LayerOutboundTransportV4, Wfp.LayerInboundTransportV4 }, LocalV4),
                     (new[] { Wfp.LayerOutboundTransportV6, Wfp.LayerInboundTransportV6 }, LocalV6),
                 })
            foreach (var layer in layers)
            {
                filters.Add(new("KOS killswitch: loopback", layer, WfpAction.Permit, WeightLoopback, [WfpCondition.Loopback()]));
                if (!blockLan) // conditions on the same field match any of them
                    filters.Add(new("KOS killswitch: local network", layer, WfpAction.Permit, WeightLocal,
                        local.Select(n => WfpCondition.RemoteNetwork(IPAddress.Parse(n.Address), n.Prefix)).ToList()));
                filters.Add(new("KOS killswitch: everything else", layer, WfpAction.Block, WeightBlock, scope is null ? [] : [scope]));
            }
        return filters;
    }

    /// <summary>Turns it on and reads the filters back; disposing the session it returns turns it off.</summary>
    public static WfpSession On(bool blockLan, WfpCondition? scope = null)
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
