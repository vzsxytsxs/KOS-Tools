using System.Net;
using System.Runtime.InteropServices;
using KosToolbox.Core.Firewall;
using Xunit;

namespace KosToolbox.Core.Tests;

public class FirewallPolicyTests
{
    static readonly string[] Kos = [@"C:\Program Files\KOS\toolbox\KosToolbox.exe", @"C:\Program Files\KOS\engine\KosEngine.exe"];

    static FirewallPlan Build(params FirewallRule[] rules) => FirewallPolicy.Build(new FirewallConfig { Rules = rules.ToList() }, Kos, _ => true);

    static List<WfpFilter> Named(FirewallPlan plan, string name) => plan.Filters.Where(f => f.Name == FirewallPolicy.NamePrefix + name).ToList();

    [Fact]
    public void BuiltInsAndDefaultBlockAreAlwaysThere()
    {
        var plan = Build();
        var block = Named(plan, "block everything else");
        Assert.Equal(8, block.Count); // 4 layers, persistent + boot-time
        Assert.All(block, f => { Assert.Equal(WfpAction.Block, f.Action); Assert.Equal(FirewallPolicy.WeightDefault, f.Weight); Assert.Empty(f.Conditions); });
        Assert.Equal(4, block.Count(f => f.BootTime));
        Assert.Equal(8, Named(plan, "loopback").Count);
        Assert.Equal(new[] { Wfp.LayerConnectV4, Wfp.LayerAcceptV4 }, Named(plan, "DHCP").Select(f => f.Layer).Distinct());
        Assert.Equal(new[] { Wfp.LayerConnectV6, Wfp.LayerAcceptV6 }, Named(plan, "ICMPv6 (neighbor discovery)").Select(f => f.Layer).Distinct());
        var dns = Named(plan, "DNS");
        Assert.Equal(new[] { Wfp.LayerConnectV4, Wfp.LayerConnectV6 }, dns.Select(f => f.Layer).Distinct());
        Assert.Equal(2, dns[0].Conditions.Count(c => c.Field == Wfp.ConditionProtocol)); // TCP or UDP
        Assert.Equal(4, Named(plan, "KosToolbox.exe").Count);
        Assert.All(plan.Filters.Where(f => f.Action == WfpAction.Permit), f => Assert.Equal(FirewallPolicy.WeightBuiltIn, f.Weight));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void AnAllowUntilRestartIsNotPersistentAndOutranksABlock()
    {
        var temp = new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\Games\a.exe", UntilRestart = true, Allow = false }; // allows anyway
        var plan = Build(new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\Games\a.exe", Allow = false }, temp);
        var filters = plan.Filters.Where(f => f.Name == FirewallPolicy.TemporaryName(temp)).ToList();
        Assert.Equal(new[] { Wfp.LayerConnectV4, Wfp.LayerConnectV6 }, filters.Select(f => f.Layer)); // one per layer: no boot-time copy
        Assert.All(filters, f =>
        {
            Assert.True(f.UntilRestart);
            Assert.False(f.BootTime);
            Assert.Equal(WfpAction.Permit, f.Action);
            Assert.True(f.Weight > FirewallPolicy.WeightBlock && f.Weight < FirewallPolicy.WeightBuiltIn);
        });
        Assert.Contains(temp.Id, FirewallPolicy.TemporaryName(temp));
        Assert.All(plan.Filters.Where(f => f.Name != FirewallPolicy.TemporaryName(temp)), f => Assert.False(f.UntilRestart)); // everything else stays persistent
    }

    [Fact]
    public void RulesUntilRestartHoldOnlyWhileTheirFiltersAreInTheEngine()
    {
        var alive = new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\a.exe", UntilRestart = true };
        var restarted = new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\b.exe", UntilRestart = true };
        var justAdded = new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\c.exe", UntilRestart = true };
        var permanent = new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\d.exe" };
        var config = new FirewallConfig { Rules = [alive, restarted, justAdded, permanent] };
        var engine = new HashSet<string> { FirewallPolicy.TemporaryName(alive), FirewallPolicy.NamePrefix + "allow d.exe" };

        Assert.True(FirewallPolicy.Holds(alive, engine));
        Assert.False(FirewallPolicy.Holds(restarted, engine));
        Assert.False(FirewallPolicy.Holds(permanent, engine)); // not a rule until restart at all
        Assert.Equal(1, FirewallPolicy.DropExpired(config, engine, new HashSet<string> { justAdded.Id }));
        Assert.Equal([alive, justAdded, permanent], config.Rules);
    }

    [Fact]
    public void DnsClientGetsEncryptedDnsWhenItsSidIsKnown()
    {
        const string sid = "S-1-5-80-859482183-879914841-863379149-1145462774-2388618682";
        var plan = FirewallPolicy.Build(new FirewallConfig(), Kos, _ => true, sid);
        var doh = Named(plan, "DNS over HTTPS/TLS");
        Assert.Equal(new[] { Wfp.LayerConnectV4, Wfp.LayerConnectV6 }, doh.Select(f => f.Layer).Distinct());
        Assert.Equal(sid, Assert.Single(doh[0].Conditions, c => c.Field == Wfp.ConditionUserId).Value);
        Assert.Equal(2, doh[0].Conditions.Count(c => c.Field == Wfp.ConditionRemotePort)); // 443 or 853
        Assert.Empty(Named(FirewallPolicy.Build(new FirewallConfig(), Kos, _ => true, "not a sid"), "DNS over HTTPS/TLS"));
    }

    [Fact]
    public void MissingKosProgramIsLeftOut()
    {
        var plan = FirewallPolicy.Build(new FirewallConfig(), Kos, p => !p.EndsWith("KosEngine.exe"));
        Assert.Empty(Named(plan, "KosEngine.exe"));
        Assert.NotEmpty(Named(plan, "KosToolbox.exe"));
    }

    [Fact]
    public void AppAllowIsOutboundOnBothVersionsAndBlockOutranksAllow()
    {
        var plan = Build(
            new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\Games\game.exe", Name = "Game" },
            new FirewallRule { Kind = FirewallRuleKind.App, Path = @"C:\Bad\bad.exe", Name = "Bad", Allow = false, Direction = FirewallDirection.Both });
        var allow = Named(plan, "allow Game");
        Assert.Equal(new[] { Wfp.LayerConnectV4, Wfp.LayerConnectV6 }, allow.Select(f => f.Layer).Distinct());
        Assert.All(allow, f => { Assert.Equal(WfpAction.Permit, f.Action); Assert.Equal(FirewallPolicy.WeightAllow, f.Weight); });
        Assert.Equal(@"C:\Games\game.exe", Assert.Single(allow[0].Conditions).Value);
        var block = Named(plan, "block Bad");
        Assert.Equal(8, block.Count);
        Assert.All(block, f => Assert.Equal(FirewallPolicy.WeightBlock, f.Weight));
        Assert.True(FirewallPolicy.WeightBuiltIn > FirewallPolicy.WeightBlock && FirewallPolicy.WeightBlock > FirewallPolicy.WeightAllow
            && FirewallPolicy.WeightAllow > FirewallPolicy.WeightDefault);
    }

    [Fact]
    public void DisabledInvalidAndMissingRulesAreLeftOut()
    {
        var plan = FirewallPolicy.Build(new FirewallConfig
        {
            Rules =
            [
                new() { Kind = FirewallRuleKind.App, Path = @"C:\a\off.exe", Name = "Off", Enabled = false },
                new() { Kind = FirewallRuleKind.App, Path = "relative.exe", Name = "Relative" },
                new() { Kind = FirewallRuleKind.App, Path = @"C:\gone\gone.exe", Name = "Gone" },
                new() { Kind = FirewallRuleKind.Custom, Name = "Ports without protocol", RemotePorts = "80" },
                new() { Kind = FirewallRuleKind.Service, Name = "Svc", Service = "x", Sid = "S-1-5-80-1;(A;;GA;;;WD)" },
            ],
        }, Kos, p => !p.Contains("gone"));
        Assert.DoesNotContain(plan.Filters, f => f.Name.Contains("Off"));
        Assert.Equal(4, plan.Skipped.Count);
        Assert.Contains(plan.Skipped, s => s.StartsWith("Gone:"));
        Assert.Contains(plan.Skipped, s => s.StartsWith("Svc:"));
    }

    [Fact]
    public void CustomRuleSplitsAddressesByVersion()
    {
        var plan = Build(new FirewallRule
        {
            Kind = FirewallRuleKind.Custom, Name = "Mixed", Protocol = 17, RemotePorts = "53, 1000-2000",
            RemoteAddresses = "10.0.0.0/8, fe80::/10", Direction = FirewallDirection.Both,
        });
        var filters = Named(plan, "allow Mixed");
        Assert.Equal(8, filters.Count);
        var v4 = filters.First(f => f.Layer == Wfp.LayerConnectV4);
        Assert.Equal(Wfp.TypeV4AddrMask, Assert.Single(v4.Conditions, c => c.Field == Wfp.ConditionRemoteAddress).Type);
        Assert.Equal(2, v4.Conditions.Count(c => c.Field == Wfp.ConditionRemotePort));
        Assert.Contains(v4.Conditions, c => c.Match == Wfp.MatchRange);
        var v6 = filters.First(f => f.Layer == Wfp.LayerAcceptV6);
        Assert.Equal(Wfp.TypeV6AddrMask, Assert.Single(v6.Conditions, c => c.Field == Wfp.ConditionRemoteAddress).Type);
    }

    [Fact]
    public void OnlyV4AddressesMeansOnlyV4LayersAndIcmpPicksItsVersion()
    {
        var plan = Build(
            new FirewallRule { Kind = FirewallRuleKind.Custom, Name = "V4", RemoteAddresses = "1.1.1.1" },
            new FirewallRule { Kind = FirewallRuleKind.Custom, Name = "Ping", Protocol = 1 },
            new FirewallRule { Kind = FirewallRuleKind.Custom, Name = "Clash", Protocol = 58, RemoteAddresses = "1.1.1.1" });
        Assert.Equal(new[] { Wfp.LayerConnectV4 }, Named(plan, "allow V4").Select(f => f.Layer).Distinct());
        Assert.Equal(new[] { Wfp.LayerConnectV4 }, Named(plan, "allow Ping").Select(f => f.Layer).Distinct());
        Assert.Empty(Named(plan, "allow Clash"));
        Assert.Contains(plan.Skipped, s => s.StartsWith("Clash:"));
    }

    [Fact]
    public void ServiceAndStoreAppUseTheirSids()
    {
        var plan = Build(
            new FirewallRule { Kind = FirewallRuleKind.Service, Name = "Windows Update", Service = "wuauserv", Sid = "S-1-5-80-1014140700-3308905587-3330345912-272242898-93311788" },
            new FirewallRule { Kind = FirewallRuleKind.StoreApp, Name = "Store", Sid = "S-1-15-2-1-2-3-4-5-6-7" });
        Assert.Equal(Wfp.TypeSecurityDescriptor, Assert.Single(Named(plan, "allow Windows Update")[0].Conditions).Type);
        Assert.Equal(Wfp.ConditionPackageId, Assert.Single(Named(plan, "allow Store")[0].Conditions).Field);
    }

    [Theory]
    [InlineData("80", 1)]
    [InlineData(" 80 , 443,27000-27050 ", 3)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void PortsParse(string? text, int count) => Assert.Equal(count, FirewallPolicy.Ports(text)!.Count);

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("100-50")]
    [InlineData("80,abc")]
    [InlineData("-")]
    public void BadPortsAreRefused(string text) => Assert.Null(FirewallPolicy.Ports(text));

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("10.0.0.0/8, fe80::/10, ::1", true)]
    [InlineData("10.1", false)]
    [InlineData("1.1.1.1/33", false)]
    [InlineData("fe80::1%5", false)]
    [InlineData("example.com", false)]
    public void AddressesParse(string text, bool ok) => Assert.Equal(ok, FirewallPolicy.Addresses(text) is not null);

    [Theory]
    [InlineData("S-1-5-80-1-2-3", "S-1-5-80-", true)]
    [InlineData("S-1-5-80-", "S-1-5-80-", false)]
    [InlineData("S-1-5-80-1)(A;;GA;;;WD", "S-1-5-80-", false)]
    [InlineData("S-1-5-80-1--2", "S-1-5-80-", false)]
    [InlineData("S-1-15-2-1", "S-1-5-80-", false)]
    public void SidsAreChecked(string sid, string prefix, bool ok) => Assert.Equal(ok, FirewallPolicy.IsSid(sid, prefix));
}

/// <summary>Reading the config: a decision is never taken on a default made up from a file that's there but can't be read.</summary>
public class KosFirewallConfigTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("kos-fw-").FullName;
    string Config => Path.Combine(dir, "config.json");
    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void NoFileIsOffWithNoRules()
    {
        var c = KosFirewall.Read(Config, () => null);
        Assert.False(c.Enabled);
        Assert.Empty(c.Rules);
    }

    [Fact]
    public void AGoodFileIsRead()
    {
        File.WriteAllText(Config, """{"Enabled":true,"Popups":false,"RestoreProfiles":["Public"],"Rules":[{"Kind":0,"Path":"C:\\a.exe","Allow":true}]}""");
        var c = KosFirewall.Read(Config, () => null);
        Assert.True(c.Enabled);
        Assert.Equal(["Public"], c.RestoreProfiles);
        Assert.Equal(@"C:\a.exe", Assert.Single(c.Rules).Path);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("")]
    public void ABrokenFileThrows(string text)
    {
        File.WriteAllText(Config, text);
        Assert.Throws<InvalidOperationException>(() => KosFirewall.Read(Config, () => null));
    }

    [Fact]
    public void AFileHeldOpenByAnotherProcessThrows()
    {
        File.WriteAllText(Config, """{"Enabled":true}""");
        using var held = new FileStream(Config, FileMode.Open, FileAccess.Read, FileShare.None);
        var ex = Assert.Throws<InvalidOperationException>(() => KosFirewall.Read(Config, () => null));
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public void ARefusedFolderOrFileThrows()
    {
        File.WriteAllText(Config, """{"Enabled":false}""");
        var ex = Assert.Throws<InvalidOperationException>(() => KosFirewall.Read(Config, () => "not owned by Administrators"));
        Assert.Equal("not owned by Administrators", ex.Message);
    }
}

public class FirewallIdentityTests
{
    [Fact]
    public void ServiceAndPackageSidsAreDerived()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.StartsWith("S-1-5-80-", FirewallIdentity.ServiceSid("Dnscache"));
        Assert.Null(FirewallIdentity.ServiceSid("no-such-service-kos"));
        Assert.Null(FirewallIdentity.ServiceSid(@"x\y"));
        // A package SID is derived from the family name alone (the app doesn't have to be installed).
        Assert.StartsWith("S-1-15-2-", FirewallIdentity.PackageSid("Microsoft.WindowsCalculator_8wekyb3d8bbwe"));
    }

    [Fact]
    public void DevicePathsBecomeDrivePaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(FirewallIdentity.DosPath("System"));
        Assert.Null(FirewallIdentity.DosPath(@"\device\nosuchvolume\a.exe"));
        // The system drive's own device name, as a net event would carry it.
        var system = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var device = AppIdDevice(Path.Combine(Environment.SystemDirectory, "curl.exe"));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "curl.exe"), FirewallIdentity.DosPath(device), ignoreCase: true);
        Assert.StartsWith(system, FirewallIdentity.DosPath(device)!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the filter engine calls the file (its app id is the lowercase device path, NUL-terminated).</summary>
    static string AppIdDevice(string path)
    {
        Assert.Equal(0u, Wfp.FwpmGetAppIdFromFileName0(path, out var blob));
        try
        {
            var size = Marshal.ReadInt32(blob);
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(blob, 8), size / 2).TrimEnd('\0');
        }
        finally { Wfp.FwpmFreeMemory0(ref blob); }
    }
}

public class WfpLayoutTests
{
    // Printed from the Windows SDK 10.0.26100 headers (x64) by a C program; the interop must match them byte for byte.
    [Fact]
    public void StructureSizesMatchTheSdk()
    {
        Assert.Equal(16, Marshal.SizeOf<Wfp.Value>());
        Assert.Equal(72, Marshal.SizeOf<Wfp.Session>());
        Assert.Equal(64, Marshal.SizeOf<Wfp.Provider>());
        Assert.Equal(72, Marshal.SizeOf<Wfp.SubLayer>());
        Assert.Equal(40, Marshal.SizeOf<Wfp.Condition>());
        Assert.Equal(200, Marshal.SizeOf<Wfp.Filter>());
        Assert.Equal(32, Marshal.SizeOf<Wfp.NetEventSubscription>());
        Assert.Equal(176, (int)Marshal.OffsetOf<Wfp.Filter>(nameof(Wfp.Filter.FilterId)));
        Assert.Equal(120, (int)Marshal.OffsetOf<Wfp.Filter>(nameof(Wfp.Filter.Conditions)));
        Assert.Equal(64, (int)Marshal.OffsetOf<Wfp.SubLayer>(nameof(Wfp.SubLayer.Weight)));
    }

    [Fact]
    public void NetEventTwoIsReadFromItsOffsets()
    {
        var e = Marshal.AllocHGlobal(120);
        var drop = Marshal.AllocHGlobal(56);
        var app = Marshal.StringToHGlobalUni(@"\device\harddiskvolume3\x\game.exe" + "\0");
        try
        {
            for (var i = 0; i < 120; i++) Marshal.WriteByte(e, i, 0);
            for (var i = 0; i < 56; i++) Marshal.WriteByte(drop, i, 0);
            Marshal.WriteInt64(e, 0, new DateTime(2026, 11, 19, 5, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
            Marshal.WriteInt32(e, Wfp.EventFlags, (int)(Wfp.NetEventFlagProtocol | Wfp.NetEventFlagLocalAddr | Wfp.NetEventFlagRemoteAddr
                | Wfp.NetEventFlagLocalPort | Wfp.NetEventFlagRemotePort | Wfp.NetEventFlagAppId | Wfp.NetEventFlagIpVersion));
            Marshal.WriteByte(e, Wfp.EventProtocol, 6);
            Marshal.WriteInt32(e, Wfp.EventLocalAddr, unchecked((int)0xC0A86465)); // 192.168.100.101
            Marshal.WriteInt32(e, Wfp.EventRemoteAddr, 0x01010101);
            Marshal.WriteInt16(e, Wfp.EventLocalPort, 50000 - 65536);
            Marshal.WriteInt16(e, Wfp.EventRemotePort, 443);
            Marshal.WriteInt32(e, Wfp.EventAppIdSize, (@"\device\harddiskvolume3\x\game.exe".Length + 1) * 2);
            Marshal.WriteIntPtr(e, Wfp.EventAppIdData, app);
            Marshal.WriteInt32(e, Wfp.EventType, (int)Wfp.NetEventClassifyDrop);
            Marshal.WriteIntPtr(e, Wfp.EventDetails, drop);
            Marshal.WriteInt64(drop, Wfp.DropFilterId, 72318);
            Marshal.WriteInt32(drop, Wfp.DropDirection, 0x3901);

            var ev = Wfp.ReadNetEvent(e)!;
            Assert.True(ev.Dropped);
            Assert.Equal(@"\device\harddiskvolume3\x\game.exe", ev.AppDevicePath);
            Assert.Equal(IPAddress.Parse("1.1.1.1"), ev.Remote);
            Assert.Equal(IPAddress.Parse("192.168.100.101"), ev.Local);
            Assert.Equal(443, ev.RemotePort);
            Assert.Equal(50000, ev.LocalPort);
            Assert.Equal(6, ev.Protocol);
            Assert.Equal(72318UL, ev.FilterId);
            Assert.False(ev.Inbound);
            Assert.Equal(5, ev.Time.Hour);

            Marshal.WriteInt32(drop, Wfp.DropDirection, Wfp.DirectionIn);
            Assert.True(Wfp.ReadNetEvent(e)!.Inbound);
            Marshal.WriteInt32(e, Wfp.EventType, 1); // an IKE event: not ours to read
            Assert.Null(Wfp.ReadNetEvent(e));
        }
        finally { Marshal.FreeHGlobal(e); Marshal.FreeHGlobal(drop); Marshal.FreeHGlobal(app); }
    }
}

public class FirewallRulesTests
{
    static FirewallRule App(string path, FirewallDirection direction = FirewallDirection.Out) => new() { Kind = FirewallRuleKind.App, Path = path, Direction = direction };

    [Fact]
    public void AllowUntilRestartSitsBesideThePermanentRule()
    {
        var config = new FirewallConfig();
        FirewallRules.Choose(config, App(@"C:\Games\a.exe"), allow: false);
        FirewallRules.AllowUntilRestart(config, App(@"C:\Games\a.exe"));
        FirewallRules.AllowUntilRestart(config, App(@"c:\games\A.exe")); // again: still one
        Assert.Equal(2, config.Rules.Count);
        Assert.Equal(false, FirewallRules.Current(config, App(@"C:\Games\a.exe"))); // the permanent block is untouched
        var temp = Assert.Single(config.Rules, r => r.UntilRestart);
        Assert.True(temp.Allow && temp.Enabled);

        FirewallRules.Choose(config, App(@"C:\Games\a.exe"), allow: null); // removing the permanent rule leaves the one until restart
        Assert.Same(temp, Assert.Single(config.Rules));
    }

    [Fact]
    public void ChooseKeepsOneRulePerTargetAndDirection()
    {
        var config = new FirewallConfig();
        FirewallRules.Choose(config, App(@"C:\Games\a.exe"), allow: true);
        FirewallRules.Choose(config, App(@"c:\games\A.EXE"), allow: false); // same program, another case: changed, not added
        var rule = Assert.Single(config.Rules);
        Assert.False(rule.Allow);
        Assert.Equal(false, FirewallRules.Current(config, App(@"C:\Games\a.exe")));

        rule.Enabled = false;
        FirewallRules.Choose(config, App(@"C:\Games\a.exe"), allow: true);
        Assert.True(rule.Allow && rule.Enabled); // choosing again turns it back on

        FirewallRules.Choose(config, App(@"C:\Games\a.exe", FirewallDirection.In), allow: true); // the other direction is its own rule
        Assert.Equal(2, config.Rules.Count);

        FirewallRules.Choose(config, App(@"C:\Games\a.exe"), allow: null);
        Assert.Equal(FirewallDirection.In, Assert.Single(config.Rules).Direction);
        Assert.Null(FirewallRules.Current(config, App(@"C:\Games\a.exe")));
    }

    [Fact]
    public void ServicesStoreAppsAndCustomRulesMatch()
    {
        var service = new FirewallRule { Kind = FirewallRuleKind.Service, Service = "BITS", Sid = "S-1-5-80-1" };
        Assert.True(FirewallRules.Same(service, new FirewallRule { Kind = FirewallRuleKind.Service, Service = "bits" })); // by name: the SID is looked up only to add
        var app = new FirewallRule { Kind = FirewallRuleKind.StoreApp, Sid = "S-1-15-2-1" };
        Assert.False(FirewallRules.Same(app, new FirewallRule { Kind = FirewallRuleKind.StoreApp, Sid = "S-1-15-2-2" }));
        var custom = new FirewallRule { Kind = FirewallRuleKind.Custom, Name = "x" };
        Assert.False(FirewallRules.Same(custom, custom)); // each custom rule is its own
    }

    [Fact]
    public void PresetsAreValidAndAddedOnce()
    {
        var presets = FirewallRules.Ntp().Concat(FirewallRules.NetworkDiscovery()).ToList();
        Assert.All(presets, r => Assert.Null(FirewallPolicy.Problem(r)));
        Assert.Equal(17, presets[0].Protocol);
        Assert.All(FirewallRules.NetworkDiscovery(), r => Assert.Equal(FirewallDirection.In, r.Direction));
        Assert.Equal(11, FirewallRules.NetworkDiscovery().Sum(r => FirewallPolicy.Ports(r.LocalPorts)!.Count));

        var config = new FirewallConfig();
        Assert.Equal(3, FirewallRules.AddMissing(config, presets));
        Assert.Equal(0, FirewallRules.AddMissing(config, FirewallRules.Ntp().Concat(FirewallRules.NetworkDiscovery())));
        Assert.Equal(3, config.Rules.Count);
        // They build into filters: none is left out.
        Assert.Empty(FirewallPolicy.Build(config, [], _ => true).Skipped);
    }
}
