using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

/// <summary>
/// Short animations (120–160 ms, with easing; scrolling chases its target, see ScrollTau). Only the toolbox's "Enable animations" preference turns them on and off: the Windows
/// visual effects (which KOS turns off) do not interfere. Each new animation replaces the previous one on the same property,
/// so rapid clicks do not pile up.
/// </summary>
public static class Motion
{
    static readonly Duration Fast = TimeSpan.FromMilliseconds(120), Normal = TimeSpan.FromMilliseconds(160);
    static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static bool IsOn => AppSettings.Current.EnableAnimations;

    /// <summary>
    /// What does not depend on a XAML attribute: smooth scrolling in every ScrollViewer and the entrance of tooltips and menus.
    /// Called once at startup.
    /// </summary>
    public static void Install()
    {
        // When the event bubbles up, the ScrollContentPresenter comes before the ScrollViewer: inner lists and fields with their own wheel
        // handling (Spin) have already handled the event, and the ScrollViewer, seeing it handled, does not scroll again.
        EventManager.RegisterClassHandler(typeof(ScrollContentPresenter), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((s, _) => Pop((FrameworkElement)s, top: true)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => Pop((FrameworkElement)s, top: true)));
    }

    /// <summary>Opacity 0 ↔ 1 according to the value (hover, pressed, selection).</summary>
    public static readonly DependencyProperty ShowWhenProperty = DependencyProperty.RegisterAttached(
        "ShowWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Fade((UIElement)d, (bool)e.NewValue, collapse: false)));
    public static bool GetShowWhen(DependencyObject d) => (bool)d.GetValue(ShowWhenProperty);
    public static void SetShowWhen(DependencyObject d, bool value) => d.SetValue(ShowWhenProperty, value);

    /// <summary>Like ShowWhen, but collapses the element after it fades out (expanding/collapsing groups).</summary>
    public static readonly DependencyProperty RevealWhenProperty = DependencyProperty.RegisterAttached(
        "RevealWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Fade((UIElement)d, (bool)e.NewValue, collapse: true)));
    public static bool GetRevealWhen(DependencyObject d) => (bool)d.GetValue(RevealWhenProperty);
    public static void SetRevealWhen(DependencyObject d, bool value) => d.SetValue(RevealWhenProperty, value);

    /// <summary>Smooth entrance (opacity and an 8 px offset) whenever the value changes: pages, tabs, panels, messages.</summary>
    public static readonly DependencyProperty TransitionProperty = DependencyProperty.RegisterAttached(
        "Transition", typeof(object), typeof(Motion), new PropertyMetadata(null, (d, _) => Enter((FrameworkElement)d)));
    public static object? GetTransition(DependencyObject d) => d.GetValue(TransitionProperty);
    public static void SetTransition(DependencyObject d, object? value) => d.SetValue(TransitionProperty, value);

    /// <summary>Appears with a fade and slight scale whenever it becomes visible (notifications, details panel).</summary>
    public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached(
        "Appear", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is FrameworkElement el && e.NewValue is true) el.IsVisibleChanged += (_, a) => { if (a.NewValue is true) Pop(el); };
        }));
    public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
    public static void SetAppear(DependencyObject d, bool value) => d.SetValue(AppearProperty, value);

    /// <summary>On a Popup: the content enters with a fade and scale from the top whenever it opens (lists, editors).</summary>
    public static readonly DependencyProperty PopOnOpenProperty = DependencyProperty.RegisterAttached(
        "PopOnOpen", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is Popup popup && e.NewValue is true) popup.Opened += (_, _) => { if (popup.Child is FrameworkElement c) Pop(c, top: true); };
        }));
    public static bool GetPopOnOpen(DependencyObject d) => (bool)d.GetValue(PopOnOpenProperty);
    public static void SetPopOnOpen(DependencyObject d, bool value) => d.SetValue(PopOnOpenProperty, value);

    /// <summary>Rotates the element to the angle, around its center (arrows of open and closed sections).</summary>
    public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached(
        "Angle", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnAngle));
    public static double GetAngle(DependencyObject d) => (double)d.GetValue(AngleProperty);
    public static void SetAngle(DependencyObject d, double value) => d.SetValue(AngleProperty, value);

    /// <summary>Width that follows the value, sliding from the current width when it changes (the sidebar's Collapse button).</summary>
    public static readonly DependencyProperty WidthToProperty = DependencyProperty.RegisterAttached(
        "WidthTo", typeof(double), typeof(Motion), new PropertyMetadata(double.NaN, OnWidthTo));
    public static double GetWidthTo(DependencyObject d) => (double)d.GetValue(WidthToProperty);
    public static void SetWidthTo(DependencyObject d, double value) => d.SetValue(WidthToProperty, value);

    static void Fade(UIElement el, bool show, bool collapse)
    {
        if (show) el.Visibility = Visibility.Visible;
        if (!IsOn || el is FrameworkElement { IsLoaded: false })
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = show ? 1 : 0;
            if (!show && collapse) el.Visibility = Visibility.Collapsed;
            return;
        }
        var a = new DoubleAnimation(show ? 1 : 0, show ? Normal : Fast) { EasingFunction = Ease };
        if (!show && collapse) a.Completed += (_, _) => { if (!GetRevealWhen(el)) el.Visibility = Visibility.Collapsed; };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    static void Enter(FrameworkElement el)
    {
        if (!IsOn || !el.IsLoaded) return;
        if (el.RenderTransform is not TranslateTransform { IsFrozen: false } t) el.RenderTransform = t = new TranslateTransform();
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Normal) { EasingFunction = Ease });
    }

    /// <summary>Dialogs and popups: appear with a fade and a slight scale (from the center, or from the top for those that open downward).</summary>
    public static void Pop(FrameworkElement el, bool top = false)
    {
        if (!IsOn) return;
        var s = new ScaleTransform(0.97, 0.97);
        el.RenderTransformOrigin = new Point(0.5, top ? 0 : 0.5);
        el.RenderTransform = s;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Normal) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Normal) { EasingFunction = Ease });
    }

    static void OnAngle(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el.RenderTransform is not RotateTransform { IsFrozen: false } r)
        {
            el.RenderTransform = r = new RotateTransform();
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var to = (double)e.NewValue;
        if (!IsOn || el is FrameworkElement { IsLoaded: false })
        {
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            r.Angle = to;
            return;
        }
        r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(to, Normal) { EasingFunction = Ease });
    }

    static void OnWidthTo(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        var to = (double)e.NewValue;
        el.BeginAnimation(FrameworkElement.WidthProperty, null); // a quick second click starts from the width on screen, not from the end
        if (!IsOn || !el.IsLoaded) { el.Width = to; return; }
        var from = el.ActualWidth;
        el.Width = to;
        el.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(from, to, Normal) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
    }

    // ---------- smooth scrolling ----------

    /// <summary>Time constant of the chase: every 44 ms the scroll covers ~63% of the remaining distance (every 1/30 s, ~53%).</summary>
    const double ScrollTau = 0.044;

    sealed class Scrolling { public double Target, Position; public TimeSpan Last; public EventHandler? Tick; }
    static readonly ConditionalWeakTable<ScrollViewer, Scrolling> Scrolls = new();

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !IsOn || sender is not ScrollContentPresenter { TemplatedParent: ScrollViewer sv } presenter) return;
        // List that scrolls by items: the offset is not in pixels, so the default WPF scrolling stays.
        if (sv.CanContentScroll && !(presenter.Content is ItemsPresenter items && VisualTreeHelper.GetChildrenCount(items) > 0
                                     && VisualTreeHelper.GetChild(items, 0) is VirtualizingStackPanel panel && ItemsControl.GetItemsOwner(panel) is { } owner
                                     && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel)) return;
        var s = Scrolls.GetOrCreateValue(sv);
        var from = sv.VerticalOffset;
        // Turning the wheel again during a scroll only pushes the target (the speed carries on, nothing restarts);
        // when stopped, it starts from where it is (the bar may have been dragged).
        var start = s.Tick is not null ? s.Target : from;
        var lines = SystemParameters.WheelScrollLines;
        var step = lines < 0 ? sv.ViewportHeight : lines * 16.0; // 16 px per line, like WPF
        var to = Math.Clamp(start - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
        if (Math.Abs(to - from) < 0.5) return; // at the limit: WPF handles it (and does not scroll)
        s.Target = to;
        e.Handled = true;
        if (s.Tick is not null) return;
        s.Position = from;
        s.Last = TimeSpan.Zero;
        s.Tick = (_, a) => Glide(sv, s, ((RenderingEventArgs)a).RenderingTime);
        CompositionTarget.Rendering += s.Tick;
    }

    /// <summary>One scroll frame: covers a fraction of the remaining distance to the target, proportional to the frame time.</summary>
    static void Glide(ScrollViewer sv, Scrolling s, TimeSpan now)
    {
        if (now == s.Last) return; // Rendering can fire more than once in the same frame
        var dt = s.Last == TimeSpan.Zero ? 1 / 60.0 : (now - s.Last).TotalSeconds; // first frame: moves right away, without waiting for the next
        s.Last = now;
        s.Position += (s.Target - s.Position) * (1 - Math.Exp(-dt / ScrollTau));
        if (Math.Abs(s.Target - s.Position) < 0.5) { s.Position = s.Target; Stop(s); }
        sv.ScrollToVerticalOffset(s.Position);
    }

    static void Stop(Scrolling s)
    {
        if (s.Tick is null) return;
        CompositionTarget.Rendering -= s.Tick;
        s.Tick = null;
    }

    /// <summary>Returns to the top, stopping any smooth scroll in progress (page change).</summary>
    public static void ScrollToTop(ScrollViewer sv)
    {
        if (Scrolls.TryGetValue(sv, out var s)) Stop(s);
        sv.ScrollToTop();
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
