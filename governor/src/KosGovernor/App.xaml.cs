using System.Windows;
using System.Windows.Threading;
using KosGovernor.Core;
using KosGovernor.ViewModels;
using KosGovernor.Views;

namespace KosGovernor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"Interface {typeof(App).Assembly.GetName().Version} opened.");
        var window = new MainWindow(new MainViewModel());
        MainWindow = window;
        window.Show();

        // Second launch (hotkey, tray): brings this window to the front instead of opening another one.
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Names.ShowUi);
        new Thread(() =>
        {
            while (show.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
        }) { IsBackground = true, Name = "Show window" }.Start();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error in the interface", e.Exception);
        Dialog.Show("Something went wrong", $"{e.Exception.Message}\n\nDetails are in the log: {Log.Directory}");
        e.Handled = true;
        if (MainWindow is not { IsVisible: true }) Shutdown(1);
    }
}
