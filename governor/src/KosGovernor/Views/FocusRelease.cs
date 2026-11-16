using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KosGovernor.Views;

/// <summary>
/// A text box releases focus on Enter or on a click outside it that didn't move focus to another control (empty
/// background): the value is committed (LostFocus bindings) and the keyboard returns to the window, where the hotkeys work.
/// </summary>
static class FocusRelease
{
    public static void Attach(Window window)
    {
        window.Focusable = true;
        window.FocusVisualStyle = null;
        // After the box's own shortcuts (e.g. Enter that applies the value): hence handledEventsToo.
        window.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox { AcceptsReturn: false } box) Release(window, box);
        }), handledEventsToo: true);
        window.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (Keyboard.FocusedElement is not TextBox box || e.OriginalSource is DependencyObject d && IsInside(d, box)) return;
            // After the click: if a button, list or another box took focus, nothing to do; otherwise the click was on the background.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (Keyboard.FocusedElement == box) Release(window, box); });
        }), handledEventsToo: true);
    }

    static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (DependencyObject? x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    static void Release(Window window, TextBox box)
    {
        // Logical focus out of the box (fires LostFocus: commits the value) and keyboard on the window.
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
        if (!window.Focus()) Keyboard.ClearFocus();
    }
}
