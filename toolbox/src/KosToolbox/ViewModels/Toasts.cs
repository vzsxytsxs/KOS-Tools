using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;

namespace KosToolbox.ViewModels;

public enum ToastKind { Success, Info, Warning, Error }

public sealed class ToastItem : ObservableObject
{
    public ToastItem(string message, ToastKind kind)
    {
        Message = message;
        Kind = kind;
        CloseCommand = new RelayCommand(() => Toasts.Close(this));
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public ICommand CloseCommand { get; }
    // Segoe MDL2/Fluent: done, information, warning, error.
    public string Icon => Kind switch { ToastKind.Success => "", ToastKind.Info => "", ToastKind.Warning => "", _ => "" };
}

/// <summary>Short notifications at the bottom of the window ("Applied", warnings, errors). They disappear on their own; errors stay longer.</summary>
public static class Toasts
{
    const int MaxVisible = 4;
    public static ObservableCollection<ToastItem> Items { get; } = new();

    public static void Show(string? message, ToastKind kind = ToastKind.Success)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => Show(message, kind)); return; }
        // The same repeated message (e.g. from a slider) does not stack.
        if (Items.LastOrDefault() is { } last && last.Message == message && last.Kind == kind) Items.Remove(last);
        var item = new ToastItem(message, kind);
        Items.Add(item);
        while (Items.Count > MaxVisible) Items.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 8 : 4) };
        timer.Tick += (_, _) => { timer.Stop(); Close(item); };
        timer.Start();
    }

    public static void Close(ToastItem item) => Items.Remove(item);

    /// <summary>Kind from the text when the page only knows "error or not".</summary>
    public static ToastKind KindOf(string message, bool error) =>
        error ? ToastKind.Error
        : message.Contains("restart", StringComparison.OrdinalIgnoreCase) || message.Contains("reboot", StringComparison.OrdinalIgnoreCase) ? ToastKind.Warning
        : ToastKind.Success;
}
