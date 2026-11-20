using System.Windows;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.ViewModels;
using KosToolbox.Views;

namespace KosEqualizerApp;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        // As in the toolbox: loaded now from inside the .exe, so closing doesn't crash if the .exe was replaced meanwhile.
        System.Reflection.Assembly.Load("System.Diagnostics.Tracing");
        Log.Initialize();
        if (!SingleInstance.Claim("KosEqualizer")) return 0; // already open: its window comes to the front
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

public partial class App : Application
{
    bool _settling;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"KOS Equalizer {typeof(App).Assembly.GetName().Version} started (administrator: {Elevation.IsAdmin}).");
        Motion.Install();
        var page = new EqualizerPageViewModel();
        var window = new AppWindow(new AppWindowViewModel(Txt.AppEqualizer, page), "pack://application:,,,/Assets/kos-equalizer.ico");
        // Edits waiting for their pause are saved, and a turn on or off finishes, before the window closes.
        window.Closing += async (_, args) =>
        {
            if (page.Idle) return;
            args.Cancel = true;
            if (_settling) return;
            _settling = true;
            if (page.IsBusy) Toasts.Show(Txt.EqClosingAfterChange, ToastKind.Warning);
            await page.SettleAsync();
            _settling = false; // this close runs the check again: an edit made meanwhile is waited for too
            window.Close();
        };
        MainWindow = window;
        SingleInstance.ShowOnRequest(window);
        window.Show(); // the window tells the page it's shown
        MainViewModel.Fire(page.RefreshAsync());
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error in KOS Equalizer", e.Exception);
        Dialog.Show(Txt.UnexpectedError, Txt.UnexpectedErrorDetail(e.Exception.Message, Log.Problem is null ? Log.CurrentFile : null));
        e.Handled = true;
        if (MainWindow is not { IsVisible: true }) Shutdown(1);
    }
}
