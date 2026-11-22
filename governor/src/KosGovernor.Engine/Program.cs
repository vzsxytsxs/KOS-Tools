using KosGovernor.Core;

namespace KosGovernor.Engine;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        args.Contains("--setup", StringComparer.OrdinalIgnoreCase) ? SetupCommands.Run(args)
        : args.Contains("--firewall-off", StringComparer.OrdinalIgnoreCase) ? EngineHost.FirewallOff()
        : EngineHost.Run();
}
