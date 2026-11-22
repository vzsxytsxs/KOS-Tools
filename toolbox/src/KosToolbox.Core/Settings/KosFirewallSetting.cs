using KosToolbox.Core.Firewall;
using KosToolbox.Core.Interop;

namespace KosToolbox.Core.Settings;

/// <summary>
/// KOS Firewall on ("on") or off ("off"), as its saved config says. Writing turns it on or off the way its own page does: on puts its
/// filters in place and then turns the Windows Firewall profiles off; off turns those profiles back on and removes its filters.
/// </summary>
public sealed class KosFirewallSetting : ISetting
{
    public string Key => "kosfirewall:enabled";
    public string Display => "KOS Firewall";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    /// <summary>Throws when the config is there but can't be read (the caller shows "Unknown").</summary>
    public string? Read() => KosFirewall.Load().Enabled ? "on" : "off";

    public void Write(string? value)
    {
        if (value == "on")
        {
            if (Read() != "on") KosFirewall.TurnOn();
            return;
        }
        // Off is checked in the filter engine, not only the config (a crash between the filters and the save leaves them out of
        // step): skipped only when the config says off and BFE isn't running, so no filter can be there to remove.
        if (ServiceControl.State("BFE") != ServiceControl.StateRunning && Read() == "off") return;
        KosFirewall.TurnOff(restoreWindows: true, strict: true);
    }
}
