using System.Windows;
using System.Windows.Media.Imaging;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

/// <summary>The window of a KOS app that is one page (see <see cref="AppWindowViewModel"/>).</summary>
public partial class AppWindow : Window
{
    public AppWindow(AppWindowViewModel vm, string iconPack)
    {
        InitializeComponent();
        DataContext = vm;
        Icon = BitmapFrame.Create(new Uri(iconPack));
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        FocusRelease.Attach(this);
        // Periodic reads stop while the window is minimized or hidden, as in the toolbox.
        void Shown() => vm.Current.OnShown(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => Shown();
        IsVisibleChanged += (_, _) => Shown();
    }

    /// <summary>The page's scroll area (the ui-shot probe scrolls it).</summary>
    public System.Windows.Controls.ScrollViewer PageScroller => Scroller;
}
