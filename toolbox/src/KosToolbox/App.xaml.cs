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
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"KOS Toolbox {typeof(App).Assembly.GetName().Version} started (administrator: {Elevation.IsAdmin}).");
        Motion.Install();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        GpuSettingsViewModel.ApplySaved(); // brightness/contrast/gamma/vibrance/hue adjusted in the Settings tab
        // Here and not in MainViewModel: the /ui-shot probe creates the MainViewModel and must not touch the graphics card.
        MainViewModel.Fire(vm.Pages.OfType<GpuPageViewModel>().First().ApplyProfileOnStartAsync());
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
