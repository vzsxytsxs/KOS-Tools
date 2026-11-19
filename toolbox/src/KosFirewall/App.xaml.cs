using System.Windows;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.ViewModels;
using KosToolbox.Views;

namespace KosFirewallApp;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        // As in the toolbox: loaded now from inside the .exe, so closing doesn't crash if the .exe was replaced meanwhile.
        System.Reflection.Assembly.Load("System.Diagnostics.Tracing");
        Log.Initialize();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"KOS Firewall {typeof(App).Assembly.GetName().Version} started (administrator: {Elevation.IsAdmin}).");
        Motion.Install();
        var page = new FirewallPageViewModel();
        var window = new AppWindow(new AppWindowViewModel(Txt.AppFirewall, page), "pack://application:,,,/Assets/kos.ico");
        MainWindow = window;
        window.Show(); // the window tells the page it's shown
        MainViewModel.Fire(page.RefreshAsync());
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error in KOS Firewall", e.Exception);
        Dialog.Show(Txt.UnexpectedError, Txt.UnexpectedErrorDetail(e.Exception.Message, Log.Problem is null ? Log.CurrentFile : null));
        e.Handled = true;
        if (MainWindow is not { IsVisible: true }) Shutdown(1);
    }
}
