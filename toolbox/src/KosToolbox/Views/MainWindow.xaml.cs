using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        FocusRelease.Attach(this);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
                vm.PropertyChanged += (_, p) => { if (p.PropertyName == nameof(MainViewModel.Current)) Motion.ScrollToTop(Scroller); };
        };
        // Periodic reads (GPU page) stop while the window is minimized or hidden.
        void Shown() { if (DataContext is MainViewModel vm) vm.Current.OnShown(IsVisible && WindowState != WindowState.Minimized); }
        StateChanged += (_, _) => Shown();
        IsVisibleChanged += (_, _) => Shown();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
        // Ctrl+Z undoes the last edit on the GPU page; inside a text field the field's own undo applies.
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
                 && DataContext is MainViewModel { Current: ViewModels.GpuPageViewModel gpu } && gpu.UndoCommand.CanExecute(null))
        {
            gpu.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Search.IsKeyboardFocused && Search.Text.Length > 0)
        {
            Search.Clear();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing now would end the process before Windows Update's services and policies are put back.
        if (Core.Updates.WindowsUpdates.Busy)
        {
            ViewModels.Toasts.Show("Windows Update is still working. Close the toolbox when it finishes, so its services and policies go back as they were.", ViewModels.ToastKind.Warning);
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }
        // Closing in the middle of a change would leave the verification without a result on screen.
        if (Core.Tweaks.TweakEngine.Instance.IsBusy
            && !Dialog.Confirm(Core.Text.Txt.ChangeInProgressTitle, Core.Text.Txt.CloseWhileBusy, Core.Text.Txt.Close))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
