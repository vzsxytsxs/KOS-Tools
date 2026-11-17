using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KosToolbox.Core.Latency;

/// <summary>
/// What a ping run adds up to. Jitter is the average change between consecutive replies (|rtt − previous rtt|), the figure
/// most ping tools show; a lost reply is skipped, so the next reply is compared with the last one that came back.
/// </summary>
public sealed record PingStats(int Sent, int Received, double? MinMs, double? AvgMs, double? MaxMs, double? JitterMs, double? MaxJitterMs)
{
    public int Lost => Sent - Received;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
}

/// <summary>Ping (ICMP echo) at a steady pace, for the Latency page's Ping tab: one probe at a time, a missing reply counts as lost.</summary>
public static class PingTest
{
    public const int TimeoutMs = 1000;
    static readonly byte[] Payload = new byte[32];

    /// <summary>Round trips in ms, null where no reply came back in time.</summary>
    public static PingStats Compute(IReadOnlyList<double?> rtts)
    {
        var got = rtts.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (got.Count == 0) return new PingStats(rtts.Count, 0, null, null, null, null, null);
        var deltas = Jitter(rtts).Where(d => d is not null).Select(d => d!.Value).ToList();
        return new PingStats(rtts.Count, got.Count, got.Min(), got.Average(), got.Max(),
            deltas.Count == 0 ? null : deltas.Average(), deltas.Count == 0 ? null : deltas.Max());
    }

    /// <summary>Per probe: |rtt − the previous reply's rtt|; null for a lost probe and for the first reply.</summary>
    public static IEnumerable<double?> Jitter(IReadOnlyList<double?> rtts)
    {
        double? previous = null;
        foreach (var r in rtts)
        {
            if (r is not { } v) { yield return null; continue; }
            yield return previous is { } p ? Math.Abs(v - p) : null;
            previous = v;
        }
    }

    /// <summary>A host name or an IP address (what the user types); no URL, port or spaces.</summary>
    public static bool ValidHost(string host) =>
        host.Length is > 0 and <= 253 && Uri.CheckHostName(host) != UriHostNameType.Unknown;

    /// <summary>The address to ping: the IP as typed, or the host name's first IPv4 address (IPv6 if it has none).</summary>
    public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault()
            ?? throw new InvalidOperationException($"{host} has no address.");
    }

    /// <summary>
    /// One echo: the round trip in ms, or null when nothing came back in time (or the reply was an error). Windows reports whole
    /// milliseconds (a router reads 0), so the time is taken around a blocking send instead: it adds only the system call.
    /// </summary>
    public static Task<double?> ProbeAsync(Ping ping, IPAddress address) => Task.Run(() =>
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var reply = ping.Send(address, TimeoutMs, Payload);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return reply.Status == IPStatus.Success ? ms : (double?)null;
        }
        catch (PingException) { return null; }
    });

    /// <summary>
    /// Default gateways of the connected adapters (the router: the local link and Wi-Fi). The one Windows routes the internet
    /// through comes first (a VPN adapter can have a gateway too, with a worse metric), then IPv4 before IPv6.
    /// </summary>
    public static IReadOnlyList<IPAddress> Gateways()
    {
        var list = new List<(IPAddress Address, bool Best)>();
        var best = GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()), out var index) == 0 ? (int)index : -1;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = n.GetIPProperties();
                int? v4Index;
                try { v4Index = props.GetIPv4Properties()?.Index; }
                catch (NetworkInformationException) { v4Index = null; } // IPv4 not enabled on this adapter
                foreach (var g in props.GatewayAddresses)
                    if (!g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any) && list.All(l => !l.Address.Equals(g.Address)))
                        list.Add((g.Address, v4Index == best));
            }
        }
        catch (NetworkInformationException) { }
        return list.OrderBy(g => g.Best ? 0 : 1).ThenBy(g => g.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(g => g.Address).ToList();
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    static extern uint GetBestInterface(uint destination, out uint index);
}
