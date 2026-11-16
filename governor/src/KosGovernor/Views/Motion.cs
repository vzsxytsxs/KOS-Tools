using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KosGovernor.Views;

/// <summary>
/// Short animations (120–160 ms, with easing). Off when
/// Windows disables animations (Settings › Accessibility › Visual effects). Each new animation replaces the
/// previous one on the same property, so quick clicks don't pile up.
/// </summary>
public static class Motion
{
    static readonly Duration Fast = TimeSpan.FromMilliseconds(120), Normal = TimeSpan.FromMilliseconds(160);
    static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static bool IsOn => SystemParameters.ClientAreaAnimation;

    /// <summary>Opacity 0 ↔ 1 depending on the value (hover, pressed, selection).</summary>
    public static readonly DependencyProperty ShowWhenProperty = DependencyProperty.RegisterAttached(
        "ShowWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Fade((UIElement)d, (bool)e.NewValue, collapse: false)));
    public static bool GetShowWhen(DependencyObject d) => (bool)d.GetValue(ShowWhenProperty);
    public static void SetShowWhen(DependencyObject d, bool value) => d.SetValue(ShowWhenProperty, value);

    /// <summary>Like ShowWhen, but collapses the element after it fades out (expanding/collapsing groups).</summary>
    public static readonly DependencyProperty RevealWhenProperty = DependencyProperty.RegisterAttached(
        "RevealWhen", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) => Fade((UIElement)d, (bool)e.NewValue, collapse: true)));
    public static bool GetRevealWhen(DependencyObject d) => (bool)d.GetValue(RevealWhenProperty);
    public static void SetRevealWhen(DependencyObject d, bool value) => d.SetValue(RevealWhenProperty, value);

    /// <summary>Smooth entrance (opacity and 8 px offset) whenever the value changes: pages, tabs, panels, messages.</summary>
    public static readonly DependencyProperty TransitionProperty = DependencyProperty.RegisterAttached(
        "Transition", typeof(object), typeof(Motion), new PropertyMetadata(null, (d, _) => Enter((FrameworkElement)d)));
    public static object? GetTransition(DependencyObject d) => d.GetValue(TransitionProperty);
    public static void SetTransition(DependencyObject d, object? value) => d.SetValue(TransitionProperty, value);

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

    /// <summary>Dialogs: appear with a fade and a slight scale.</summary>
    public static void Pop(FrameworkElement el)
    {
        if (!IsOn) return;
        var s = new ScaleTransform(0.97, 0.97);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = s;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Normal) { EasingFunction = Ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Normal) { EasingFunction = Ease });
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
