using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace KosGovernor.Views;

static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark (Windows 10 20H1+) and black (Windows 11) title bar; ignored where unsupported.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int dark = 1, black = 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int)); // DWMWA_CAPTION_COLOR
    }
}

/// <summary>Visibility from the parameter: "false" (value false), "text" (text filled in), "empty" (empty list).</summary>
public sealed class VisibleIf : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => ((parameter as string) switch
    {
        "false" => value is false,
        "text" => !string.IsNullOrEmpty(value as string),
        "empty" => value is System.Collections.ICollection { Count: 0 },
        _ => value is true,
    }) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the value (a width) is under the parameter: a layout for narrow windows.</summary>
public sealed class NarrowerThan : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is double width && width > 0 && width < double.Parse((string)parameter, System.Globalization.CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A button whose ContextMenu also opens on a left click, under the button (the "···" of a gamemode rule).</summary>
public static class ClickMenu
{
    public static readonly DependencyProperty OpenProperty = DependencyProperty.RegisterAttached("Open", typeof(bool), typeof(ClickMenu),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not System.Windows.Controls.Button b || e.NewValue is not true) return;
            b.Click += (_, _) =>
            {
                if (b.ContextMenu is not { } menu) return;
                menu.DataContext = b.DataContext;
                menu.PlacementTarget = b;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
        }));
    public static bool GetOpen(DependencyObject d) => (bool)d.GetValue(OpenProperty);
    public static void SetOpen(DependencyObject d, bool value) => d.SetValue(OpenProperty, value);
}

/// <summary>Explorer's "Properties" window for the file.</summary>
static class ShellProperties
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    public static void Show(string path) => SHObjectProperties(IntPtr.Zero, 0x2, path, null); // SHOP_FILEPATH
}

/// <summary>Dialogs without a title bar: owner, Esc closes, drag by the background and entrance animation.</summary>
static class DialogSetup
{
    public static void Prepare(Window d, FrameworkElement root)
    {
        if (Application.Current?.Windows.OfType<Window>().LastOrDefault(w => w.IsActive && w != d) is { } owner) d.Owner = owner;
        else if (Application.Current?.MainWindow is { IsVisible: true } main && main != d) d.Owner = main;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { d.DialogResult = false; e.Handled = true; } };
        root.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) d.DragMove(); };
        d.Loaded += (_, _) => Motion.Pop(root);
    }
}
