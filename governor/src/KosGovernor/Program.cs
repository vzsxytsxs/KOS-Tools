using System.Runtime.CompilerServices;
using KosGovernor.Core;

namespace KosGovernor;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // WPF loads this assembly only when the window closes (control telemetry). Loaded now, from inside the .exe:
        // if the .exe is replaced or renamed while the governor is open, closing doesn't fail with FileNotFoundException.
        System.Reflection.Assembly.Load("System.Diagnostics.Tracing");
        // Engine: its own Native AOT executable when it sits next to this one (sign-in tasks created before it still start
        // this one with --engine, which hands over); otherwise this process runs it (tray, hotkey and rules, no WPF).
        if (args.Length == 1 && args[0] == "--engine")
        {
            if (EngineHost.EnginePath is null) return EngineHost.Run();
            EngineHost.EnsureRunning();
            return 0;
        }
        return RunUi();
    }

    // Outside Main: compiling Main with "new App()" loaded WPF (~19 MB) in the engine process too.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunUi()
    {
        using var single = new Mutex(true, Names.UiMutex, out var created);
        if (!created)
        {
            AllowSetForegroundWindow(-1); // this start is in front: the open window may take the focus
            Names.Signal(Names.ShowUi);
            return 0;
        }
        ProcessControl.EnablePrivileges();
        EngineHost.EnsureRunning();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
