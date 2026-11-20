using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;

namespace KosToolbox;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // WPF loads this assembly only when the window closes (controls telemetry). Loaded now, from inside the .exe:
        // if the .exe is replaced or renamed while the toolbox is open, closing does not crash with FileNotFoundException.
        System.Reflection.Assembly.Load("System.Diagnostics.Tracing");
        // Prepares Logs\Toolbox and applies retention; a failure is kept in Log.Problem and does not prevent startup.
        Log.Initialize();

        // Elevated child process: runs a request validated against the catalog and exits. It does not load WPF and
        // has no synchronization context, so synchronous waits (PowerRun, bcdedit) do not deadlock.
        if (args.Length == 2 && args[0] == "--elevated-job")
            return ElevatedJob.ExecuteFromArgumentAsync(args[1]).GetAwaiter().GetResult();

        // The process rules became KOS Governor (a separate program). The old Run value still calls
        // "--governor" at logon: it is only removed, without opening the window.
        if (args.Length == 1 && args[0] == "--governor")
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("KOS Toolbox process rules", throwOnMissingValue: false);
            return 0;
        }

        // Logon task of the GPU page: applies the saved values (revalidated) and exits.
        if (args.Length == 1 && args[0] == "--gpu-apply")
            return Core.Gpu.GpuTuner.RunAtSignIn();

        // Startup entry of the Discord debloater: runs hidden and exits; this exe has no console and WPF isn't loaded.
        if (args.Length is 2 or 3 && args[0] == Core.Debloat.DiscordDebloat.LogonArgument)
            return Core.Debloat.DiscordDebloat.RunAtLogonAsync(args[1], args.Length == 3 ? args[2] : "").GetAwaiter().GetResult();

        if (!Views.SingleInstance.Claim("KosToolbox")) return 0; // already open: its window comes to the front
        App.Phase("runtime");
        var app = new App();
        app.InitializeComponent(); // the theme and every page's templates
        App.Phase("resources");
        return app.Run();
    }
}
