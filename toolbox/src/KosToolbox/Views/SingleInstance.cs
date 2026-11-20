using System.Runtime.InteropServices;
using System.Windows;

namespace KosToolbox.Views;

/// <summary>
/// One window per KOS app (KOS Toolbox, KOS Firewall, KOS Equalizer): a second start brings the first one's window to the front
/// and exits, as KOS Governor's interface does.
/// </summary>
static class SingleInstance
{
    static Mutex? _running;
    static EventWaitHandle? _show;

    /// <summary>
    /// True for the first copy (call <see cref="ShowOnRequest"/> once its window exists). False when the app already runs: its
    /// window was asked to come to the front, and this start should exit.
    /// </summary>
    public static bool Claim(string app)
    {
        bool first;
        try { _running = new Mutex(false, $@"Local\{app}.Ui", out first); }
        catch (UnauthorizedAccessException) { first = false; } // a copy runs as administrator and this one doesn't
        if (first)
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{app}.ShowUi");
            return true;
        }
        AllowSetForegroundWindow(-1); // this start is in front (the user just started it): the first copy may take the focus
        try
        {
            using var show = EventWaitHandle.OpenExisting($@"Local\{app}.ShowUi");
            show.Set();
        }
        catch (UnauthorizedAccessException) { Core.Execution.KosApps.Focus(app + ".exe"); } // its event is the administrator's: focus its window from here
        catch (WaitHandleCannotBeOpenedException) { } // it's closing
        return false;
    }

    /// <summary>Brings the window to the front whenever another start asks.</summary>
    public static void ShowOnRequest(Window window)
    {
        if (_show is not { } show) return;
        new Thread(() =>
        {
            while (show.WaitOne())
                window.Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
        }) { IsBackground = true, Name = "Show window" }.Start();
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
