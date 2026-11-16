using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;

namespace KosToolbox.Views;

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Angle of the section arrow (Motion.Angle): the down arrow rotates to point up when the section opens.</summary>
public sealed class ChevronAngle : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? 180.0 : 0.0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>Visible when the value is neither null nor empty text.</summary>
public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is null or "" ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when any value is true or non-empty text.</summary>
public sealed class AnyVisibleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Any(v => v is true || v is string { Length: > 0 }) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Opens the button's ContextMenu on left click ("⋯" button).</summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnClick", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject d) => (bool)d.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject d, bool value) => d.SetValue(OpenOnClickProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button b || e.NewValue is not true) return;
        b.Click += (_, _) =>
        {
            if (b.ContextMenu is not { } menu) return;
            menu.PlacementTarget = b;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = b.DataContext;
            menu.IsOpen = true;
        };
    }
}

/// <summary>
/// Connects a page's result message (PageMessage style) to the notifications: each new message becomes a toast,
/// with the kind read from IsError on the same DataContext. Progress messages ("Applying…") do not become toasts.
/// </summary>
public static class ToastOnMessage
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(string), typeof(ToastOnMessage), new PropertyMetadata(null, OnChanged));

    public static string? GetMessage(DependencyObject d) => (string?)d.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject d, string? value) => d.SetValue(MessageProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Before Loaded it is the initial binding (the page showing up again): old messages are not repeated.
        if (d is not FrameworkElement { IsLoaded: true } el || e.NewValue is not string { Length: > 0 } message || message == Core.Text.Txt.Applying) return;
        // IsError is written right after Message: read it on the next cycle.
        el.Dispatcher.BeginInvoke(() =>
        {
            var error = el.DataContext?.GetType().GetProperty("IsError")?.GetValue(el.DataContext) is true;
            ViewModels.Toasts.Show(message, ViewModels.Toasts.KindOf(message, error));
        });
    }
}

/// <summary>
/// On click or Tab into the field, the text clears and typing can start right away (DRS profile search). Whoever keeps the
/// previous value (the ViewModel) restores it if nothing is chosen.
/// </summary>
public static class ClearOnFocus
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ClearOnFocus), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.GotKeyboardFocus += (_, a) =>
        {
            // Focus coming back from the list (another window: the popup) after an item is chosen: keeps the chosen name.
            if (a.OldFocus is System.Windows.Media.Visual old && PresentationSource.FromVisual(old) != PresentationSource.FromVisual(box)) return;
            box.Clear();
        };
        // Already focused (e.g. the list closed and returned the name): a new click clears it too.
        box.PreviewMouseLeftButtonDown += (_, _) => { if (box.IsKeyboardFocusWithin) box.Clear(); };
    }
}

/// <summary>On click or Tab into the field, all the text is selected: typing replaces it right away (search fields).</summary>
public static class SelectAllOnFocus
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SelectAllOnFocus), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        // The click that gives focus must not move the caret and undo the selection.
        box.PreviewMouseLeftButtonDown += (_, a) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            box.Focus();
            a.Handled = true;
        };
    }
}

/// <summary>Numeric field: ↑/↓ arrows and the mouse wheel (with the field focused) add/subtract the step.</summary>
public static class Spin
{
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(int), typeof(Spin), new PropertyMetadata(0, OnChanged));

    public static int GetStep(DependencyObject d) => (int)d.GetValue(StepProperty);
    public static void SetStep(DependencyObject d, int value) => d.SetValue(StepProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.OldValue is not 0) return;
        void Bump(int direction)
        {
            if (!int.TryParse(box.Text.Trim(), out var v)) return;
            box.Text = Math.Max(0, v + direction * GetStep(box)).ToString();
            box.CaretIndex = box.Text.Length;
        }
        box.PreviewKeyDown += (_, a) =>
        {
            if (a.Key == System.Windows.Input.Key.Up) { Bump(1); a.Handled = true; }
            else if (a.Key == System.Windows.Input.Key.Down) { Bump(-1); a.Handled = true; }
        };
        box.PreviewMouseWheel += (_, a) =>
        {
            if (!box.IsKeyboardFocusWithin) return;
            Bump(Math.Sign(a.Delta));
            a.Handled = true;
        };
    }
}

/// <summary>Draggable point of a chart; coordinates in pixels of the chart area (the element with ChartSize.Track).</summary>
public interface IChartPoint
{
    void DragStart();
    void DragTo(double x, double y);
    void DragEnd() { }
}

/// <summary>
/// Charts drawn in real pixels (no Viewbox, which stretched text and dots): the element with Track="True"
/// reports its own size to the ViewModel through the Size property (bind with Mode=OneWayToSource).
/// </summary>
public static class ChartSize
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(Size), typeof(ChartSize), new FrameworkPropertyMetadata(Size.Empty));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(ChartSize), new PropertyMetadata(false, OnTrack));

    public static Size GetSize(DependencyObject d) => (Size)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, Size value) => d.SetValue(SizeProperty, value);
    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);

    // SetCurrentValue keeps the binding to the ViewModel (SetValue would replace it with a local value).
    static void OnTrack(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement el && e.NewValue is true) el.SizeChanged += (_, a) => el.SetCurrentValue(SizeProperty, a.NewSize);
    }
}

/// <summary>Connects a Thumb to the DataContext's IChartPoint: a click calls DragStart, dragging calls DragTo with the mouse position.</summary>
public static class ChartDrag
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ChartDrag), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Thumb thumb || e.NewValue is not true) return;
        thumb.DragStarted += (_, _) =>
        {
            // Chart that accepts the keyboard (V/F curve): clicking the point gives it focus.
            if (Area(thumb) is UIElement { Focusable: true } area) area.Focus();
            (thumb.DataContext as IChartPoint)?.DragStart();
        };
        thumb.DragCompleted += (_, _) => (thumb.DataContext as IChartPoint)?.DragEnd();
        thumb.DragDelta += (_, _) =>
        {
            if (thumb.DataContext is not IChartPoint point || Area(thumb) is not IInputElement input) return;
            var pos = System.Windows.Input.Mouse.GetPosition(input);
            point.DragTo(pos.X, pos.Y);
        };
    }

    static DependencyObject? Area(DependencyObject from)
    {
        var area = from;
        while (area is not null && !ChartSize.GetTrack(area)) area = System.Windows.Media.VisualTreeHelper.GetParent(area);
        return area;
    }
}

/// <summary>Range selection on a chart's background (x in pixels of the area); a click without dragging also lands here.</summary>
public interface IChartRange
{
    void RangeStart(double x);
    void RangeTo(double x);
    void RangeEnd();
}

/// <summary>Connects the chart background (the element with ChartSize.Track) to the DataContext's IChartRange and gives it focus.</summary>
public static class ChartRange
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ChartRange), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el || e.NewValue is not true) return;
        // The points (Thumb) handle their own click; only background clicks arrive here.
        el.MouseLeftButtonDown += (_, a) =>
        {
            if (el.DataContext is not IChartRange range) return;
            el.Focus();
            el.CaptureMouse();
            range.RangeStart(a.GetPosition(el).X);
            a.Handled = true;
        };
        el.MouseMove += (_, a) => { if (el.IsMouseCaptured && el.DataContext is IChartRange range) range.RangeTo(a.GetPosition(el).X); };
        el.MouseLeftButtonUp += (_, _) =>
        {
            if (!el.IsMouseCaptured) return;
            el.ReleaseMouseCapture();
            (el.DataContext as IChartRange)?.RangeEnd();
        };
    }
}

static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar (Windows 10 20H1+) and black (Windows 11); ignored where unsupported.</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int dark = 1, black = 0; // COLORREF 0x000000
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int)); // DWMWA_CAPTION_COLOR
    }
}
