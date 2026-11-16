using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Network;

/// <summary>A DNS choice: its servers (empty = the ones DHCP gives) for IPv4 and IPv6.</summary>
public sealed record DnsProvider(string Key, string Name, string[] V4, string[] V6);

/// <summary>An adapter's static DNS servers, whether each is encrypted (DoH), and the servers Windows is using.</summary>
public sealed record DnsState(IReadOnlyList<string> V4, IReadOnlyList<string> V6, bool V4Encrypted, bool V6Encrypted, IReadOnlyList<string> InUse)
{
    public bool Static => V4.Count + V6.Count > 0;
}

/// <summary>
/// DNS servers and DNS over HTTPS per adapter, as Settings › Network sets them (SetInterfaceDnsSettings with DNS_INTERFACE_SETTINGS3).
/// Encryption uses the URI templates Windows already knows for these servers (DohWellKnownServers), with no fallback to plain DNS.
/// </summary>
public static class NetDns
{
    public const string Automatic = "auto";

    public static readonly DnsProvider[] Providers =
    {
        new(Automatic, "Automatic (DHCP)", Array.Empty<string>(), Array.Empty<string>()),
        new("cloudflare", "Cloudflare", new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }),
        new("google", "Google", new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }),
        new("quad9", "Quad9", new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }),
    };

    /// <summary>The provider these static servers are (Automatic when there are none); null for other servers.</summary>
    public static DnsProvider? Match(DnsState s) =>
        Providers.FirstOrDefault(p => p.V4.SequenceEqual(s.V4) && (s.V6.Count == 0 || p.V6.SequenceEqual(s.V6)));

    public static DnsState Read(string interfaceId)
    {
        var id = Guid.Parse(interfaceId);
        var (v4, e4) = Get(id, ipv6: false);
        var (v6, e6) = Get(id, ipv6: true);
        var inUse = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase))
            ?.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6SiteLocal).Select(a => a.ToString()).ToList() ?? new List<string>();
        return new DnsState(v4, v6, v4.Count > 0 && e4, v6.Count > 0 && e6, inUse);
    }

    /// <summary>Sets the provider (and encryption) on the adapter, elevated if needed, and confirms by reading it back.</summary>
    public static async Task ApplyAsync(NetAdapter a, string provider, bool encrypted, CancellationToken ct = default)
    {
        if (a.InterfaceId is null) throw new InvalidOperationException("The adapter has no active network interface.");
        var p = Find(provider);
        if (Elevation.IsAdmin) await Task.Run(() => Execute(a.InterfaceId, p, encrypted), ct).ConfigureAwait(false);
        else await ElevatedRunner.RunAsync(new ElevatedJob { NetAdapter = new NetAdapterJob { InstanceId = a.InstanceId, Dns = provider, DnsEncrypted = encrypted } }, ct).ConfigureAwait(false);
        var now = Read(a.InterfaceId);
        var wantEncrypted = encrypted && p.Key != Automatic;
        if (Match(now) != p || now.V4Encrypted != wantEncrypted || p.V6.Length > 0 && now.V6.Count > 0 && now.V6Encrypted != wantEncrypted)
            throw new InvalidOperationException(Text.Txt.ChangeNotConfirmed);
    }

    public static DnsProvider Find(string? key) => Providers.FirstOrDefault(p => p.Key == key) ?? throw new InvalidOperationException($"Unknown DNS choice: {key}");

    /// <summary>Writes both stacks. Encryption is cleared first (it belongs to the servers listed), then the servers are set.</summary>
    internal static void Execute(string interfaceId, DnsProvider p, bool encrypted)
    {
        var id = Guid.Parse(interfaceId);
        foreach (var ipv6 in new[] { false, true })
        {
            var servers = ipv6 ? p.V6 : p.V4;
            try
            {
                var (current, _) = Get(id, ipv6);
                if (current.Count > 0) Set(id, ipv6, current, doh: false); // drops the DoH properties of the servers being replaced
                Set(id, ipv6, servers, doh: encrypted && servers.Length > 0);
            }
            // IPv6 unbound from the adapter: IPv4 alone is still the choice (the read-back only checks IPv6 when it has servers).
            catch (InvalidOperationException ex) when (ipv6) { Log.Warn($"Network: IPv6 DNS of {interfaceId} not set: {ex.Message}"); }
        }
        Log.Info($"Network: DNS of {interfaceId} set to {p.Name}{(encrypted && p.Key != Automatic ? " over HTTPS" : "")}.");
    }

    // ---------- iphlpapi ----------
    const uint Version3 = 3;
    const ulong SettingIpv6 = 0x1, SettingNameServer = 0x2, SettingDoh = 0x1000;
    const ulong DohEnableAuto = 0x1, DohEnable = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct DnsInterfaceSettings3
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain, NameServer, SearchList;
        public uint RegistrationEnabled, RegisterAdapterName, EnableLlmnr, QueryAdapterName;
        public IntPtr ProfileNameServer;
        public uint DisableUnconstrainedQueries;
        public IntPtr SupplementalSearchList;
        public uint ServerPropertyCount;
        public IntPtr ServerProperties;
        public uint ProfileServerPropertyCount;
        public IntPtr ProfileServerProperties;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DnsServerProperty { public uint Version, ServerIndex, Type; public IntPtr DohSettings; }

    [StructLayout(LayoutKind.Sequential)]
    struct DnsDohServerSettings { public IntPtr Template; public ulong Flags; }

    [DllImport("iphlpapi.dll")] static extern uint GetInterfaceDnsSettings(Guid iface, ref DnsInterfaceSettings3 settings);
    [DllImport("iphlpapi.dll")] static extern uint SetInterfaceDnsSettings(Guid iface, ref DnsInterfaceSettings3 settings);
    [DllImport("iphlpapi.dll")] static extern void FreeInterfaceDnsSettings(ref DnsInterfaceSettings3 settings);

    /// <summary>Static servers of one stack and whether every one of them has DoH on.</summary>
    static (List<string> Servers, bool Encrypted) Get(Guid id, bool ipv6)
    {
        var s = new DnsInterfaceSettings3 { Version = Version3, Flags = ipv6 ? SettingIpv6 : 0 };
        var code = GetInterfaceDnsSettings(id, ref s);
        if (code != 0) throw new InvalidOperationException($"GetInterfaceDnsSettings failed ({code}).");
        try
        {
            var servers = Split(Marshal.PtrToStringUni(s.NameServer));
            var encrypted = new HashSet<uint>();
            var size = Marshal.SizeOf<DnsServerProperty>();
            for (var i = 0; i < s.ServerPropertyCount && s.ServerProperties != IntPtr.Zero; i++)
            {
                var prop = Marshal.PtrToStructure<DnsServerProperty>(s.ServerProperties + i * size);
                if (prop.Type != 1 || prop.DohSettings == IntPtr.Zero) continue;
                var doh = Marshal.PtrToStructure<DnsDohServerSettings>(prop.DohSettings);
                if ((doh.Flags & (DohEnableAuto | DohEnable)) != 0) encrypted.Add(prop.ServerIndex);
            }
            return (servers, servers.Count > 0 && Enumerable.Range(0, servers.Count).All(i => encrypted.Contains((uint)i)));
        }
        finally { FreeInterfaceDnsSettings(ref s); }
    }

    static void Set(Guid id, bool ipv6, IReadOnlyList<string> servers, bool doh)
    {
        foreach (var server in servers)
            if (!IPAddress.TryParse(server, out var ip) || ip.AddressFamily != (ipv6 ? System.Net.Sockets.AddressFamily.InterNetworkV6 : System.Net.Sockets.AddressFamily.InterNetwork))
                throw new ArgumentException($"Not an IPv{(ipv6 ? 6 : 4)} address: {server}");
        var names = Marshal.StringToHGlobalUni(string.Join(",", servers));
        var size = Marshal.SizeOf<DnsServerProperty>();
        var dohSettings = doh ? Marshal.AllocHGlobal(Marshal.SizeOf<DnsDohServerSettings>()) : IntPtr.Zero;
        var props = doh ? Marshal.AllocHGlobal(size * servers.Count) : IntPtr.Zero;
        try
        {
            var s = new DnsInterfaceSettings3 { Version = Version3, Flags = SettingNameServer | (ipv6 ? SettingIpv6 : 0), NameServer = names };
            if (doh)
            {
                // Template from Windows' own list of known DoH servers, no fallback to plain DNS.
                Marshal.StructureToPtr(new DnsDohServerSettings { Template = IntPtr.Zero, Flags = DohEnableAuto }, dohSettings, false);
                for (var i = 0; i < servers.Count; i++)
                    Marshal.StructureToPtr(new DnsServerProperty { Version = 1, ServerIndex = (uint)i, Type = 1, DohSettings = dohSettings }, props + i * size, false);
                s.Flags |= SettingDoh;
                s.ServerPropertyCount = (uint)servers.Count;
                s.ServerProperties = props;
            }
            else if (servers.Count > 0) s.Flags |= SettingDoh; // same servers, no properties: turns encryption off for them
            var code = SetInterfaceDnsSettings(id, ref s);
            if (code != 0) throw new InvalidOperationException($"SetInterfaceDnsSettings failed ({code}{(code == 5 ? ": needs administrator" : "")}).");
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (dohSettings != IntPtr.Zero) Marshal.FreeHGlobal(dohSettings);
        }
    }

    internal static List<string> Split(string? list) =>
        (list ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
