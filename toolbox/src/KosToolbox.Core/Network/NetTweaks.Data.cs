namespace KosToolbox.Core.Network;

/// <summary>
/// Keyword and recommended-value data. Sources (KOS Setup\4-Tweaking\Network):
/// recommended adapter properties = the network tweaks script in Basic\ (it only writes values that already exist in the
/// driver) and Advanced\1-Basics.bat (QoS, SR-IOV, VMQ off; RSS and RDMA on). Hardware offloads (checksum, LSO, RSC, USO) are
/// recommended on: they take work off the CPU on current adapters. The vendor keywords (Intel I218/I219, Realtek HwOption) only
/// apply where the driver has them, like every keyword here;
/// global TCP/UDP and offload = Advanced\1-Basics.bat; congestion provider = Advanced\2-CongestionProvider.cmd
/// (newreno, "recommended"); extra options and keywords (ITR, coalesce, power gating, port scaling) =
/// Advanced\5-Network_Tweaker.ps1, which doesn't recommend values.
/// </summary>
public static partial class NetTweaks
{
    static readonly NetTweakOption[] OffOn = { new("0", "Disabled"), new("1", "Enabled") };
    static readonly NetTweakOption[] TxRx =
    {
        new("0", "Disabled"), new("1", "Tx Enabled"), new("2", "Rx Enabled"), new("3", "Rx & Tx Enabled"),
    };

    static NetKnownKeyword K(string keyword, string? name, string section, NetTweakOption[]? options, string? recommended, long? min = null, long? max = null) =>
        new(keyword, name, section, options ?? Array.Empty<NetTweakOption>(), min, max,
            recommended is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["*"] = recommended });

    /// <summary>Written as REG_DWORD when they don't exist yet (the rest is REG_SZ, as Device Manager writes them).</summary>
    static readonly HashSet<string> Dwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PnPCapabilities", "EnablePME", "EnableConnectedPowerGating", "DisableDelayedPowerUp", "EnableCoalesce", "CoalesceBufferSize",
        "EnableUdpTxScaling", "DisablePortScaling", "ManyCoreScaling", "HwOption", "HwOptionV2", "HwOptionV3",
    };

    public static bool IsDword(string keyword) => Dwords.Contains(keyword);

    /// <summary>The KOS scripts only write keywords the driver already has: "Recommended" creates none.</summary>
    public static bool AddWhenMissing(string keyword) => false;

    static IEnumerable<NetKnownKeyword> KeywordList()
    {
        const string A = SectionAdvanced, I = SectionInterrupt, R = SectionRss;
        // ----- performance and offloads -----
        yield return K("*FlowControl", "Flow Control", A, TxRx, "0");
        yield return K("*PriorityVLANTag", "Priority & VLAN", A,
            new NetTweakOption[] { new("0", "Disabled"), new("1", "Priority Enabled"), new("2", "VLAN Enabled"), new("3", "Priority & VLAN Enabled") }, "0");
        yield return K("*ReceiveBuffers", "Receive Buffers", A, null, "1024", 64, 4096);
        yield return K("*TransmitBuffers", "Transmit Buffers", A, null, "1024", 64, 4096);
        yield return K("*IPChecksumOffloadIPv4", "IPv4 Checksum Offload", A, TxRx, "3");
        yield return K("*TCPChecksumOffloadIPv4", "TCP Checksum Offload (IPv4)", A, TxRx, "3");
        yield return K("*TCPChecksumOffloadIPv6", "TCP Checksum Offload (IPv6)", A, TxRx, "3");
        yield return K("*UDPChecksumOffloadIPv4", "UDP Checksum Offload (IPv4)", A, TxRx, "3");
        yield return K("*UDPChecksumOffloadIPv6", "UDP Checksum Offload (IPv6)", A, TxRx, "3");
        yield return K("*TCPUDPChecksumOffloadIPv4", "TCP/UDP Checksum Offload (IPv4)", A, TxRx, "3");
        yield return K("*TCPUDPChecksumOffloadIPv6", "TCP/UDP Checksum Offload (IPv6)", A, TxRx, "3");
        yield return K("*TCPConnectionOffloadIPv4", "TCP Connection Offload (IPv4)", A, TxRx, "3");
        yield return K("*TCPConnectionOffloadIPv6", "TCP Connection Offload (IPv6)", A, TxRx, "3");
        yield return K("*LsoV1IPv4", "Large Send Offload v1 (IPv4)", A, OffOn, "1");
        yield return K("*LsoV2IPv4", "Large Send Offload v2 (IPv4)", A, OffOn, "1");
        yield return K("*LsoV2IPv6", "Large Send Offload v2 (IPv6)", A, OffOn, "1");
        yield return K("*RscIPv4", "Recv Segment Coalescing (IPv4)", A, OffOn, "1");
        yield return K("*RscIPv6", "Recv Segment Coalescing (IPv6)", A, OffOn, "1");
        yield return K("*UdpRsc", "UDP Receive Segment Coalescing", A, OffOn, "0");
        yield return K("*UsoIPv4", "UDP Segmentation Offload (IPv4)", A, OffOn, "1");
        yield return K("*UsoIPv6", "UDP Segmentation Offload (IPv6)", A, OffOn, "1");
        yield return K("*QoSOffload", "QoS Offload", A, OffOn, "1");
        yield return K("TeredoOffload", "Teredo Offload", A, OffOn, "1");
        yield return K("*EncapsulatedPacketTaskOffloadVxlan", "VXLAN Encapsulated Task Offload", A, OffOn, "1");
        yield return K("*EncapsulatedPacketTaskOffloadNvgre", "NVGRE Encapsulated Task Offload", A, OffOn, "1");
        yield return K("*QOS", "Packet Priority & QoS", A, OffOn, "0");
        yield return K("*VMQ", "Virtual Machine Queues", A, OffOn, "0");
        yield return K("*SRIOV", "SR-IOV", A, OffOn, "0");
        yield return K("*NetworkDirect", "NetworkDirect (RDMA)", A, OffOn, "1");
        yield return K("*EncapsulatedPacketTaskOffload", "Encapsulated Task Offload", A, OffOn, "0");
        yield return K("*IPsecOffloadV2", "IPsec Offload", A,
            new NetTweakOption[] { new("0", "Disabled"), new("1", "Auth Header Enabled"), new("2", "ESP Enabled"), new("3", "Auth Header & ESP Enabled") }, "0");
        yield return K("*IPsecOffloadV2IPv4", "IPsec Offload (IPv4)", A,
            new NetTweakOption[] { new("0", "Disabled"), new("1", "Auth Header Enabled"), new("2", "ESP Enabled"), new("3", "Auth Header & ESP Enabled") }, "0");
        yield return K("*PacketDirect", "Packet Direct", A, OffOn, null);
        yield return K("EnableCoalesce", "Coalesce", A, OffOn, null);
        yield return K("CoalesceBufferSize", "Coalesce Buffer Size", A, null, null, 0, 65535);
        yield return K("EnableUdpTxScaling", "UDP Tx Scaling", A, OffOn, null);
        yield return K("*AlternateSemaphoreDelay", null, A, null, "0", 0, 65535);
        yield return K("*Node", "NUMA Node", A, null, "0", 0, 64);
        yield return K("Node", null, A, null, "0", 0, 64);
        yield return K("LogLevelWarn", null, A, null, "0", 0, 65535);
        yield return K("LinkNegotiationProcess", "Link Negotiation Process", A, null, "0", 0, 65535);
        yield return K("WaitForValidPhyIDRead", "Wait For Valid PHY ID Read", A, OffOn, "0");
        yield return K("SleepWhileWaiting", "Sleep While Waiting", A, OffOn, "0");
        yield return K("EnableETW", "ETW Logging", A, OffOn, "0");

        // ----- power saving (everything off in the KOS scripts) -----
        yield return K("*EEE", "Energy-Efficient Ethernet", A, OffOn, "0");
        yield return K("AdvancedEEE", "Advanced EEE", A, OffOn, "0");
        yield return K("EEELinkAdvertisement", "EEE Link Advertisement", A, OffOn, "0");
        yield return K("EeePhyEnable", "EEE PHY", A, OffOn, "0");
        yield return K("EEEPlus", "EEE Plus", A, OffOn, "0");
        yield return K("EnableGreenEthernet", "Green Ethernet", A, OffOn, "0");
        yield return K("GigaLite", "Giga Lite", A, OffOn, "0");
        yield return K("PowerSavingMode", "Power Saving Mode", A, OffOn, "0");
        yield return K("AutoPowerSaveModeEnabled", "Auto Power Saving", A, OffOn, "0");
        yield return K("SavePowerNowEnabled", "Save Power Now", A, OffOn, "0");
        yield return K("ReduceSpeedOnPowerDown", "Reduce Speed On Power Down", A, OffOn, "0");
        yield return K("AutoDisableGigabit", "Auto Disable Gigabit", A, OffOn, "0");
        yield return K("ULPMode", "Ultra Low Power Mode", A, OffOn, "0");
        yield return K("PowerDownPll", "Power Down PLL", A, OffOn, "0");
        yield return K("EnableModernStandby", "Modern Standby", A, OffOn, "0");
        yield return K("*SelectiveSuspend", "Selective Suspend", A, OffOn, "0");
        yield return K("*DeviceSleepOnDisconnect", "Device Sleep On Disconnect", A, OffOn, "0");
        yield return K("*PMARPOffload", "ARP Offload", A, OffOn, "0");
        yield return K("*PMNSOffload", "NS Offload", A, OffOn, "0");
        yield return K("ASPM", "ASPM", A, OffOn, "0");
        yield return K("EnableAspm", "Enable ASPM", A, OffOn, "0");
        yield return K("CLKREQ", "CLKREQ", A, OffOn, "0");
        yield return K("ForceMode", null, A, null, null, 0, 65535);
        yield return K("EnablePME", "Wake From Sleep (PME)", A, OffOn, "0");
        yield return K("*EnableDynamicPowerGating", "Dynamic Power Gating", A, OffOn, "0");
        yield return K("EnableConnectedPowerGating", "Connected Power Gating", A, OffOn, null);
        yield return K("*NicAutoPowerSaver", "NIC Auto Power Saver", A, OffOn, "0");
        yield return K("DisableDelayedPowerUp", "Disable Delayed Power Up", A, OffOn, null);
        yield return K("*SipsEnabled", "Selective Suspend Idle (SIPS)", A, OffOn, "0");
        yield return K("EnableGigaLite", "Giga Lite", A, OffOn, "0");
        yield return K("PowerSaveEnable", "Power Saving", A, OffOn, "0");
        yield return K("EnablePowerManagement", "Power Management", A, OffOn, "0");
        yield return K("EnableSavePowerNow", "Save Power Now", A, OffOn, "0");
        yield return K("DynamicPowerGating", "Dynamic Power Gating", A, OffOn, "0");
        yield return K("EnableD3ColdInS0", "D3 Cold In S0", A, OffOn, "0");
        yield return K("EnableD0PHYFlexibleSpeed", "D0 PHY Flexible Speed", A, OffOn, "0");
        yield return K("EnablePHYWakeUp", "PHY Wake Up", A, OffOn, "0");
        yield return K("EnablePHYFlexibleSpeed", "PHY Flexible Speed", A, OffOn, "0");
        yield return K("AllowAllSpeedsLPLU", "Low Power Link Up At All Speeds", A, OffOn, "0");
        yield return K("*SSIdleTimeout", "Selective Suspend Idle Timeout", A, null, "1", 0, 65535);
        yield return K("*SSIdleTimeoutScreenOff", "Selective Suspend Idle Timeout (Screen Off)", A, null, "1", 0, 65535);
        yield return K("SSIdleTimeoutMS", "Selective Suspend Idle Timeout (ms)", A, null, "1", 0, 65535);
        yield return K("*IdleRestriction", "Idle Restriction", A, OffOn, "1");
        yield return K("S0MgcPkt", "S0 Magic Packet", A, OffOn, "0");
        yield return K("OBFFEnabled", "Optimized Buffer Flush/Fill (OBFF)", A, OffOn, "0");
        yield return K("LatencyToleranceReporting", "Latency Tolerance Reporting", A, OffOn, "0");
        yield return K("DMACoalescing", "DMA Coalescing", A, null, "0", 0, 65535);
        // Intel (I218/I219 and other e1d/e1r drivers): PLL shutdown, K1 power state, PHY reset, ULP and LTR.
        yield return K("I218DisablePLLShut", "I218 Disable PLL Shutdown", A, OffOn, "1");
        yield return K("I218DisablePLLShutGiga", "I218 Disable PLL Shutdown (Gigabit)", A, OffOn, "1");
        yield return K("I219DisableK1Off", "I219 Disable K1 Off", A, OffOn, "1");
        yield return K("DisablePhyReset", "Disable PHY Reset", A, OffOn, "1");
        yield return K("ForceHostExitUlp", "Force Host Exit ULP", A, OffOn, "1");
        yield return K("DisableIntelRST", "Disable Intel RST", A, OffOn, "1");
        yield return K("ForceLtrValue", "Force LTR Value", A, null, "0", 0, 65535);
        yield return K("EnableDisconnectedStandby", "Disconnected Standby", A, OffOn, "0");
        yield return K("EnableWakeOnManagmentOnTCO", "Wake On Management (TCO)", A, OffOn, "0");
        yield return K("WakeOnFastStartup", "Wake On Fast Startup", A, OffOn, "0");
        // Realtek hardware option masks (REG_DWORD).
        yield return K("HwOption", "Hardware Option", A, null, "12582912", 0, uint.MaxValue);     // 0x00C00000
        yield return K("HwOptionV2", "Hardware Option V2", A, null, "4", 0, uint.MaxValue);         // 0x00000004
        yield return K("HwOptionV3", "Hardware Option V3", A, null, "262144", 0, uint.MaxValue);    // 0x00040000
        yield return K("PnPCapabilities", "Let Windows Turn Off The Device", A,
            new NetTweakOption[] { new("0", "Allowed (Windows default)"), new("24", "Not allowed") }, "24");

        // ----- Wake on LAN (off) -----
        yield return K("*WakeOnMagicPacket", "Wake on Magic Packet", A, OffOn, "0");
        yield return K("*WakeOnPattern", "Wake on Pattern Match", A, OffOn, "0");
        yield return K("*ModernStandbyWoLMagicPacket", "Modern Standby WoL Magic Packet", A, OffOn, "0");
        yield return K("S5WakeOnLan", "Shutdown Wake-On-LAN", A, OffOn, "0");
        yield return K("WakeOnLink", "Wake on Link", A, OffOn, "0");
        yield return K("WakeOnSlot", "Wake on Slot", A, OffOn, "0");
        yield return K("WakeUpModeCap", "Wake-Up Capabilities", A, null, "0", 0, 65535);
        yield return K("WakeOnLinkChg", "Wake on Link Change", A, OffOn, "0");
        yield return K("WakeOnLinkUp", "Wake on Link Up", A, OffOn, "0");
        yield return K("WakeFromS5", "Wake From Shutdown", A, OffOn, "0");
        yield return K("WakeFromPowerOff", "Wake From Power Off", A, OffOn, "0");
        yield return K("WakeOn", "Wake On", A, null, "0", 0, 65535);
        yield return K("ForceWakeFromMagicPacketOnModernStandby", "Force Wake From Magic Packet (Modern Standby)", A, OffOn, "0");
        // RunOnce sets 0; what other values mean differs between drivers.
        yield return K("WakeOnMagicPacketFromS5", "Wake on Magic Packet From Shutdown", A, null, "0", 0, 65535);
        // Its values differ between drivers (not 0/1 on every one): listed so it shows, with no recommendation.
        yield return K("LogLinkStateEvent", "Log Link State Event", A, null, null, 0, 65535);
        yield return K("WolShutdownLinkSpeed", "WOL & Shutdown Link Speed", A,
            new NetTweakOption[] { new("0", "10 Mbps First"), new("1", "100 Mbps First"), new("2", "Not Speed Down") }, "2");

        // ----- interrupt moderation -----
        yield return K("*InterruptModeration", "Interrupt Moderation", I, OffOn, null);
        yield return K("ITR", "Interrupt Moderation Rate", I, new NetTweakOption[]
        {
            new("0", "Off"), new("200", "Minimal"), new("400", "Low"), new("950", "Medium"), new("2000", "High"), new("3600", "Extreme"), new("65535", "Adaptive"),
        }, null);
        yield return K("TxIntDelay", "Transmit Interrupt Delay", I, null, "1", 0, 65535);
        yield return K("RxIntDelay", "Receive Interrupt Delay", I, null, "1", 0, 65535);
        yield return K("TxAbsIntDelay", "Transmit Absolute Interrupt Delay", I, null, "1", 0, 65535);
        yield return K("RxAbsIntDelay", "Receive Absolute Interrupt Delay", I, null, "1", 0, 65535);
        yield return K("*TxIntDelay", null, I, null, "1", 0, 65535);
        yield return K("*RxIntDelay", null, I, null, "1", 0, 65535);
        yield return K("*TxAbsIntDelay", null, I, null, "1", 0, 65535);
        yield return K("*RxAbsIntDelay", null, I, null, "1", 0, 65535);

        // ----- RSS -----
        yield return K("*RSS", "Receive Side Scaling", R, OffOn, "1");
        yield return K("*NumRssQueues", "Maximum Number of RSS Queues", R, null, "2", 1, 64);
        yield return K("*MaxRssProcessors", "Maximum Number of RSS Processors", R, null, "2", 1, 64);
        yield return K("*RssBaseProcNumber", "RSS Base Processor", R, null, "1", 0, 63);
        yield return K("*RSSProfile", "RSS Load Balancing Profile", R, new NetTweakOption[]
        {
            new("1", "Closest Processor"), new("2", "Closest Processor Static"), new("3", "NUMA Scaling"), new("4", "NUMA Scaling Static"), new("5", "Conservative Scaling"),
        }, null);
        yield return K("DisablePortScaling", "Disable Port Scaling", R, OffOn, null);
        yield return K("ManyCoreScaling", "Many Core Scaling", R, OffOn, null);
    }

    /// <summary>Recommended values for the global parameters (Advanced\1-Basics.bat and 2-CongestionProvider.cmd).</summary>
    public static IReadOnlyDictionary<string, string> GlobalRecommended { get; } = new Dictionary<string, string>
    {
        ["rss"] = "enabled", ["autotuninglevel"] = "disabled", ["ecncapability"] = "enabled", ["timestamps"] = "disabled",
        ["initialrto"] = "300", ["rsc"] = "enabled", ["nonsackrttresiliency"] = "enabled", ["maxsynretransmissions"] = "2",
        ["fastopen"] = "enabled", ["fastopenfallback"] = "disabled", ["hystart"] = "disabled", ["prr"] = "enabled", ["pacingprofile"] = "off",
        ["uro"] = "enabled", ["uso"] = "enabled", ["congestion"] = "newreno",
        ["taskoffload"] = "enabled", ["packetcoalescingfilter"] = "disabled", ["networkdirect"] = "enabled",
    };

    /// <summary>The KOS scripts don't touch the bindings: no recommendation.</summary>
    public static IReadOnlyDictionary<string, bool> BindingRecommended { get; } = new Dictionary<string, bool>();

    public const string InterruptNote =
        "Controls how the adapter's hardware interrupts reach the CPU. Enabled groups interrupts together: less CPU usage, slightly more latency. " +
        "Disabled gives the lowest network latency if your CPU can spare the extra interrupts.";
}
