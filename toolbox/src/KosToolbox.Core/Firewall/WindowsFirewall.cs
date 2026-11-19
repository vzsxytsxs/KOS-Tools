using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Firewall;

/// <summary>
/// The Windows Firewall profiles, read from the registry (KOS Engine is Native AOT and can't use the firewall's COM API) and
/// changed with netsh, the supported way: the profiles are turned off with the service left running, never by stopping MpsSvc.
/// </summary>
public static class WindowsFirewall
{
    public static readonly string[] Profiles = ["Domain", "Private", "Public"];
    static readonly string[] LocalKeys = ["DomainProfile", "StandardProfile", "PublicProfile"];
    static readonly string[] NetshNames = ["domainprofile", "privateprofile", "publicprofile"];
    const string LocalRoot = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
    const string PolicyRoot = @"SOFTWARE\Policies\Microsoft\WindowsFirewall\";

    /// <summary>Whether the Windows Firewall service is running (with it stopped, no profile filters anything).</summary>
    public static bool ServiceRunning => ServiceControl.State("mpssvc") == ServiceControl.StateRunning;

    /// <summary>Each profile's effective state (a group policy value wins over the local one); all off when the service isn't running.</summary>
    public static bool[] State()
    {
        var running = ServiceRunning;
        var state = new bool[Profiles.Length];
        for (var i = 0; i < Profiles.Length; i++)
            state[i] = running && (Read(PolicyRoot + Profiles[i] + "Profile") ?? Read(LocalRoot + LocalKeys[i]) ?? true);
        return state;
    }

    /// <summary>A group policy decides this profile (netsh can't change it).</summary>
    public static bool ForcedByPolicy(int profile) => Read(PolicyRoot + Profiles[profile] + "Profile") is not null;

    public static bool AnyOn => State().Any(on => on);

    static bool? Read(string key)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue("EnableFirewall") is int v ? v != 0 : null;
    }

    /// <summary>Turns the named profiles (all when null) on or off and reads them back; throws if one didn't change.</summary>
    public static void Set(bool on, IEnumerable<string>? profiles = null)
    {
        var wanted = (profiles ?? Profiles).Select(p => Array.IndexOf(Profiles, p)).Where(i => i >= 0).Distinct().ToList();
        if (wanted.Count == 0) return;
        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        foreach (var i in wanted)
        {
            var r = ProcessRunner.Run(netsh, $"advfirewall set {NetshNames[i]} state {(on ? "on" : "off")}", TimeSpan.FromSeconds(30));
            if (r.ExitCode != 0) Log.Warn($"netsh advfirewall set {NetshNames[i]} exited with {r.ExitCode}: {r.Output.Trim()}");
        }
        var after = State();
        var wrong = wanted.Where(i => after[i] != on).Select(i => Profiles[i]).ToList();
        if (wrong.Count > 0)
            throw new InvalidOperationException($"Windows Firewall ({string.Join(", ", wrong)}) is still {(on ? "off" : "on")}" +
                (wrong.Any(p => ForcedByPolicy(Array.IndexOf(Profiles, p))) ? ": a group policy sets it." : on && !ServiceRunning ? ": its service isn't running." : "."));
    }
}
