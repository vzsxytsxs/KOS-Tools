using System.Windows;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

/// <summary>A view model edited in a dialog: OK is enabled while <see cref="IsValid"/>; Cancel drops the edits.</summary>
public interface IEditorDialog
{
    string Title { get; }
    double DialogWidth { get; }
    bool IsValid { get; }
    string? Error { get; }
}

/// <summary>Modal window for the EDID editors (standard resolution, extension block, data blocks), in the style of CRU's.</summary>
public partial class EditorDialog : Window
{
    EditorDialog() => InitializeComponent();

    /// <summary>True if confirmed with OK.</summary>
    public static bool Open(IEditorDialog vm)
    {
        var d = Create(vm);
        SetOwner(d);
        return d.ShowDialog() == true;
    }

    /// <summary>The window without showing it (the UI probe renders it to PNG).</summary>
    public static Window Create(IEditorDialog vm)
    {
        var d = new EditorDialog { DataContext = vm, Width = vm.DialogWidth };
        d.SourceInitialized += (_, _) => DarkTitleBar.Apply(d);
        d.Loaded += (_, _) => Motion.Pop(d.Root);
        return d;
    }

    /// <summary>A dialog opened from another dialog belongs to (and centers on) that one, not the main window.</summary>
    internal static void SetOwner(Window d)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != d) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != d) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is IEditorDialog { IsValid: true }) DialogResult = true;
    }
}
