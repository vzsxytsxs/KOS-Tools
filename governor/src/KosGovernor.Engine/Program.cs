using KosGovernor.Core;

namespace KosGovernor.Engine;

public static class Program
{
    [STAThread]
    public static int Main() => EngineHost.Run();
}
