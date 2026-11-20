namespace KosToolbox.Core.Firewall;

public enum FirewallRuleKind { App, Service, StoreApp, Custom }

public enum FirewallDirection { Out, In, Both }

/// <summary>
/// One allow or block of KOS Firewall. App: <see cref="Path"/>. Service: <see cref="Service"/> and its S-1-5-80 SID in
/// <see cref="Sid"/>. Store app: the package SID (S-1-15-2) in <see cref="Sid"/>. Custom: an optional program plus protocol,
/// ports and remote addresses. Every kind can narrow itself with those too.
/// </summary>
public sealed class FirewallRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public FirewallRuleKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Allow { get; set; } = true;
    public FirewallDirection Direction { get; set; } = FirewallDirection.Out;
    public string? Path { get; set; }
    public string? Service { get; set; }
    public string? Sid { get; set; }
    /// <summary>IP protocol number (6 TCP, 17 UDP, 1 ICMP, 58 ICMPv6); null = any.</summary>
    public int? Protocol { get; set; }
    /// <summary>"80, 443, 27000-27050"; null or empty = any.</summary>
    public string? RemotePorts { get; set; }
    public string? LocalPorts { get; set; }
    /// <summary>"1.1.1.1, 10.0.0.0/8, fe80::/10"; null or empty = any.</summary>
    public string? RemoteAddresses { get; set; }
    public DateTime Added { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// An allow until Windows restarts: its filters aren't persistent, so a restart removes them, and the rule is dropped at the
    /// next change once none of them is left (see <see cref="FirewallPolicy.DropExpired"/>). It outranks a block rule meanwhile.
    /// </summary>
    public bool UntilRestart { get; set; }
}

/// <summary>KOS Firewall's settings, in %ProgramData%\TheKOS\Firewall\config.json (admin-only); the toolbox and KOS Engine both write it.</summary>
public sealed class FirewallConfig
{
    public bool Enabled { get; set; }
    /// <summary>KOS Engine asks (Allow / Block) when a program without a rule is blocked.</summary>
    public bool Popups { get; set; } = true;
    /// <summary>KOS Engine's killswitch (<see cref="Killswitch"/>) cuts the local network too, not only the internet.</summary>
    public bool KillswitchBlocksLan { get; set; }
    /// <summary>Windows Firewall profiles that were on when KOS Firewall was turned on; turned on again when it's turned off.</summary>
    public List<string> RestoreProfiles { get; set; } = new();
    public List<FirewallRule> Rules { get; set; } = new();
}
