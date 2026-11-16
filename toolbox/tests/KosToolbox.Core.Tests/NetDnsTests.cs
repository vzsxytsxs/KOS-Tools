using KosToolbox.Core.Network;
using Xunit;

namespace KosToolbox.Core.Tests;

public class NetDnsTests
{
    [Fact]
    public void ProvidersAreMatchedByTheirServers()
    {
        Assert.Equal("cloudflare", NetDns.Match(new DnsState(new[] { "1.1.1.1", "1.0.0.1" }, Array.Empty<string>(), true, false, Array.Empty<string>()))?.Key);
        Assert.Equal("quad9", NetDns.Match(new DnsState(new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }, false, false, Array.Empty<string>()))?.Key);
        Assert.Equal(NetDns.Automatic, NetDns.Match(new DnsState(Array.Empty<string>(), Array.Empty<string>(), false, false, new[] { "192.168.0.1" }))?.Key);
        Assert.Null(NetDns.Match(new DnsState(new[] { "1.1.1.1", "8.8.8.8" }, Array.Empty<string>(), false, false, Array.Empty<string>())));
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, NetDns.Split(" 1.1.1.1, 8.8.8.8 "));
    }

    [Fact]
    public void RefusesUnknownChoices() => Assert.Throws<InvalidOperationException>(() => NetDns.Find("1.2.3.4"));

    /// <summary>Reads both stacks of every interface on this PC (read-only).</summary>
    [Fact]
    public void ReadsThisPc()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211))
        {
            var s = NetDns.Read(nic.Id);
            if (Environment.GetEnvironmentVariable("KOS_TEST_REPORT") is not null)
                Console.WriteLine($"{nic.Name}: v4 [{string.Join(",", s.V4)}] enc={s.V4Encrypted} v6 [{string.Join(",", s.V6)}] enc={s.V6Encrypted} in use [{string.Join(",", s.InUse)}] → {NetDns.Match(s)?.Name ?? "custom"}");
        }
    }
}
