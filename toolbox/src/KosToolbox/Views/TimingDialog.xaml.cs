using System.Windows;
using KosToolbox.Core.Display;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

/// <summary>Resolution editor window (Add/Edit) in the style of CRU.</summary>
public partial class TimingDialog : Window
{
    TimingDialog() => InitializeComponent();

    /// <summary>The confirmed timing and whether it should become the preferred one; null if cancelled.</summary>
    public static (DetailedTiming Timing, bool Preferred)? Open(TimingEditorViewModel vm)
    {
        var d = new TimingDialog { DataContext = vm };
        EditorDialog.SetOwner(d);
        d.SourceInitialized += (_, _) => DarkTitleBar.Apply(d);
        d.Loaded += (_, _) => Motion.Pop(d.Root);
        return d.ShowDialog() == true ? (vm.Build(), vm.Preferred) : null;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is TimingEditorViewModel { IsValid: true }) DialogResult = true;
    }
}
