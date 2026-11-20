using KosToolbox.Core.Firewall;

namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public const string PageFirewall = "Firewall",
        PageFirewallSubtitle = "KOS Firewall (an allow-list in the Windows filter engine), the Windows Firewall and the hosts file.";

    public const string FirewallToggleRefused =
        "KOS Firewall is on, and its filters need the Base Filtering Engine: turn KOS Firewall off on the Firewall page first.";

    // Header
    public const string FwNeedsAdmin = "KOS Firewall's filters and settings need administrator.",
        FwKosSwitch = "KOS Firewall", FwPopupsSwitch = "Ask about new programs",
        FwPopupsTip = "KOS Engine asks Allow or Block when a program without a rule is blocked.",
        FwMasterOn = "KOS Firewall on", FwMasterOff = "KOS Firewall off",
        FwMasterTip = "Turns KOS Firewall on or off: its filters first, then Windows Firewall off (and the reverse).",
        FwKillswitchLan = "Killswitch cuts the local network too",
        FwKillswitchLanTip = "KOS Engine's killswitch (Ctrl+Alt+K by default, set in KOS Governor) cuts the internet until pressed again, " +
                             "with or without KOS Firewall on. On: printers, NAS and other PCs here are cut too. Loopback always stays.",
        FwNote = "An allow-list: everything not allowed is blocked. Loopback, DHCP, DNS, ICMPv6 and the KOS programs are always allowed. " +
                 "Its filters stay in the filter engine and hold from boot, with no KOS program running. " +
                 "While it's on, Windows Security reports the firewall as off: KOS Firewall isn't a registered firewall product.",
        FwStatusOff = "Off", FwStatusOnUnread = "On",
        FwStatusOnNoFilters = "On, but the filter engine holds no KOS filters: turn it off and on again.",
        FwEngineNotRunning = "KOS Engine isn't running: programs without a rule are blocked without asking.",
        FwWindowsServiceStopped = "off (its service isn't running)",
        FwWindowsOn = "Turn Windows Firewall on",
        FwWindowsOnTip = "Turns every Windows Firewall profile on; KOS Firewall then turns itself off.";
    public static string FwStatusOn(int filters) => $"On · {filters} filters";
    public static string FwStatusOffLeftover(int filters) => $"Off, but the filter engine still holds {filters} KOS filters: turn it on and off again.";
    public static string FwStatusUnread(string message) => $"The filters couldn't be read: {message}";
    public static string FwWindows(string state) => $"Windows Firewall: {state}";
    public static string FwProfile(string profile, bool on, bool policy) => $"{profile} {(on ? "on" : "off")}{(policy ? " (group policy)" : "")}";

    // Results
    public static string FwTurnedOn(string status) => $"KOS Firewall: {status}.";
    public static string FwTurnedOff(string windows) => $"KOS Firewall is off. {windows}.";
    public static string FwWindowsTurnedOn(string windows, bool kosOn) => $"{windows}." + (kosOn ? "" : " KOS Firewall is off.");
    public static string FwSkipped(IEnumerable<string> rules) => "Rules left out: " + string.Join("; ", rules);
    public static string FwPopups(bool on) => on ? "KOS Engine asks about newly blocked programs." : "Newly blocked programs stay blocked without asking.";
    public static string FwKillswitchLanSet(bool on) => on ? "The killswitch cuts the local network too." : "The killswitch cuts the internet only; the local network stays.";
    public static string FwSaved(string? status) => status is null ? "Saved. KOS Firewall is off: the rules apply when it's turned on." : $"Saved. {status}.";

    // Tabs
    public const string FwTabApps = "Apps", FwTabServices = "Services", FwTabStore = "Store apps", FwTabRules = "Rules", FwTabLog = "Log", FwTabHosts = "Hosts";

    // Rules and rows
    public const string FwAllow = "Allow", FwBlock = "Block", FwNone = "None", FwRemove = "Remove rule",
        FwEnabledTip = "Rule on (unchecked: kept, not applied)",
        FwOut = "Out", FwIn = "In", FwBoth = "In and out", FwAny = "Any",
        FwMissingProgram = "the program doesn't exist.",
        FwAddProgram = "Add program…", FwAddProgramTitle = "Allow a program", FwExeFilter = "Programs (*.exe)|*.exe",
        FwAppsHint = "Programs allowed or blocked, by path. A program added here is allowed out.",
        FwAppsEmpty = "No program rules yet. Add a program, or allow one from the Log.",
        FwServicesHint = "Each service's outbound traffic, matched by its service SID (services inside svchost too).",
        FwStoreHint = "Each Store app's traffic, matched by its package SID.",
        FwLoading = "Reading…";
    public static string FwLeftOut(string reason) => $"Left out: {reason}";
    public static string FwNoServiceSid(string service) => $"{service} has no service SID, so it can't have a rule.";
    public static string FwRuleSet(string name, bool? allow) => allow switch { true => $"{name}: allowed.", false => $"{name}: blocked.", _ => $"{name}: rule removed." };
    public const string FwUntilRestart = "Until restart",
        FwUntilRestartTip = "Allowed until Windows restarts. Then the program's other rule applies again, or KOS Engine asks again.";
    public static string FwAllowedUntilRestart(string name) => $"{name}: allowed until Windows restarts.";

    // Custom rules
    public const string FwRuleName = "Name", FwRuleProgram = "Program (optional)", FwBrowse = "Browse…", FwRuleDirection = "Direction",
        FwRuleProtocol = "Protocol", FwRuleRemotePorts = "Remote ports", FwRuleLocalPorts = "Local ports", FwRuleAddresses = "Remote addresses",
        FwPortsExample = "Any (e.g. 80, 443, 27000-27050)", FwAddressesExample = "Any (e.g. 1.1.1.1, 10.0.0.0/8, fe80::/10)",
        FwAddRule = "Add rule", FwNewRule = "New rule", FwCustomRules = "Custom rules", FwRuleNeedsName = "Give the rule a name.",
        FwCustomEmpty = "No custom rules yet.",
        FwPresets = "Presets", FwPresetNtp = "NTP", FwPresetWindowsUpdate = "Windows Update", FwPresetDiscovery = "Network discovery",
        FwPresetNtpTip = "Time sync: UDP out to remote port 123.",
        FwPresetWindowsUpdateTip = "Allows the wuauserv, BITS, DoSvc and UsoSvc services out.",
        FwPresetDiscoveryTip = "Inbound on local ports UDP 137, 138, 1900, 3702, 5353, 5355 and TCP 139, 445, 2869, 5357, 5358.",
        FwPresetNtpRule = "NTP (time sync)", FwPresetDiscoveryUdp = "Network discovery (UDP)", FwPresetDiscoveryTcp = "Network discovery (TCP)";
    public static string FwRuleProblem(string problem) => $"Can't add it: {problem}";
    public static string FwRuleAdded(string name) => $"Rule added: {name}.";
    public static string FwPresetAdded(int added) => added == 0 ? "Those rules are already there." : $"{added} rule(s) added.";
    public static string FwPresetServices(IEnumerable<string> allowed, IReadOnlyCollection<string> missing) =>
        $"Allowed out: {string.Join(", ", allowed)}." + (missing.Count > 0 ? $" Not on this PC: {string.Join(", ", missing)}." : "");

    // Log
    public const string FwLogHint = "Recent drops by KOS Firewall from the filter engine's own log (about the last hundred; a restart empties it), newest first.",
        FwLogEmpty = "No drops by KOS Firewall in the filter engine's log.",
        FwLogOff = "The filter engine isn't keeping its event log (a system-wide option), so there's nothing to show.",
        FwSystem = "System", FwRefreshLog = "Refresh";

    // Hosts
    public static string FwHostsHint => $"{HostsFile.DefaultPath}: names Windows resolves before asking DNS. Saving keeps a backup of the previous file.";
    public const string FwSave = "Save", FwReload = "Reload", FwHostsUnsaved = "Unsaved changes", FwHostsValid = "No problems.",
        FwHostsSavedNoBackup = "Hosts file saved and read back.";
    public static string FwHostsSaved(string backup) => $"Hosts file saved and read back. The previous file is in {backup}.";
    public static string FwHostsLine(int line, string message) => $"Line {line}: {message}";
}
