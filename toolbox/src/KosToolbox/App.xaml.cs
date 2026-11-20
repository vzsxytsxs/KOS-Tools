using System.Windows;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.ViewModels;
using KosToolbox.Views;

namespace KosToolbox;

public partial class App : Application
{
    static readonly List<string> _phases = new();
    static TimeSpan _last;

    /// <summary>Startup timing, logged once the first frame is drawn: how long each phase took since the process started.</summary>
    internal static void Phase(string name)
    {
        var now = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        _phases.Add($"{name} {(now - _last).TotalMilliseconds:0}");
        _last = now;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"KOS Toolbox {typeof(App).Assembly.GetName().Version} started (administrator: {Elevation.IsAdmin}).");
        Motion.Install();
        var vm = new MainViewModel();
        Phase("view models");
        var window = new MainWindow { DataContext = vm };
        Phase("main window");
        MainWindow = window;
        SingleInstance.ShowOnRequest(window);
        window.ContentRendered += (_, _) =>
        {
            Phase("first frame");
            Log.Info($"Window ready {_last.TotalMilliseconds:0} ms after the start ({string.Join(", ", _phases)} ms).");
        };
        window.Show();
        Phase("window");
        GpuSettingsViewModel.ApplySaved(); // brightness/contrast/gamma/vibrance/hue adjusted in the Settings tab
        // Here and not in MainViewModel: the /ui-shot probe creates the MainViewModel and must not touch the graphics card.
        MainViewModel.Fire(vm.Pages.OfType<GpuPageViewModel>().First().ApplyProfileOnStartAsync());
        Phase("GPU settings");
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error in the interface", e.Exception);
        Dialog.Show(Txt.UnexpectedError, Txt.UnexpectedErrorDetail(e.Exception.Message, Log.Problem is null ? Log.CurrentFile : null));
        e.Handled = true;
        // If the main window never even opened, do not leave an invisible process running.
        if (MainWindow is not { IsVisible: true }) Shutdown(1);
    }
}
