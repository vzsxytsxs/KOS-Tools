using KosGovernor.Core;

namespace KosGovernor.Engine;

/// <summary>
/// "KosEngine.exe --setup &lt;command&gt;": what the KOS installer asks of the engine, silently (no window), exit code 0 on success.
/// sign-in on|off: the "KOS Engine" sign-in task; firewall-off: KOS Firewall off and the Windows Firewall profiles back on (uninstall).
/// </summary>
public static class SetupCommands
{
    public static int Run(string[] args)
    {
        var command = string.Join(' ', args.SkipWhile(a => !a.Equals("--setup", StringComparison.OrdinalIgnoreCase)).Skip(1)).ToLowerInvariant();
        try
        {
            switch (command)
            {
                case "sign-in on": SignIn.Enable(); break;
                case "sign-in off": if (SignIn.Enabled()) SignIn.Disable(); break;
                // The Windows page's way: under the lock, Windows Firewall back on or nothing removed, no filter left; skipped
                // only when the config says off and BFE isn't running.
                case "firewall-off": new KosToolbox.Core.Settings.KosFirewallSetting().Write("off"); break;
                default: Log.Warn($"Setup: unknown command '{command}'."); return 2;
            }
            Log.Info($"Setup: {command} done.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Setup: {command} failed", ex);
            return 1;
        }
    }
}
