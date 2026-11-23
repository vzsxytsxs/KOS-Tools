using System.Windows;
using System.Windows.Media;

namespace KosToolbox.Views;

/// <summary>
/// The segmented bar under a latency figure, like LatencyMon's: <see cref="Segments"/> blocks that fill as <see cref="Value"/> goes from 0 to
/// <see cref="Limit"/> (a value above 0 always lights the first one). Light while it is far from the limit, the warning colour from half
/// of it and the error colour from the limit itself (from 1 ms a DPC, an ISR or a thread's wait can cause audio dropouts).
/// </summary>
public sealed class LatencyBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LatencyBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LimitProperty = DependencyProperty.Register(nameof(Limit), typeof(double), typeof(LatencyBar),
        new FrameworkPropertyMetadata(1000.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(int), typeof(LatencyBar),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Limit { get => (double)GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    static readonly Brush Empty = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));
    static readonly Brush Light = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    static readonly Brush Warning = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xC2, 0x7A))); // Theme.xaml WarningBrush
    static readonly Brush Error = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82)));   // Theme.xaml ErrorBrush

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>How many of <paramref name="segments"/> blocks are lit for <paramref name="value"/> out of <paramref name="limit"/>.</summary>
    internal static int Lit(double value, double limit, int segments) =>
        value <= 0 || limit <= 0 || segments <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(value / limit * segments), 1, segments);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var n = Math.Max(1, Segments);
        if (w < n * 2 || h < 2) return;
        var lit = Lit(Value, Limit, n);
        var brush = Value >= Limit ? Error : Value >= Limit / 2 ? Warning : Light;
        const double gap = 2;
        var block = (w - gap * (n - 1)) / n;
        for (var i = 0; i < n; i++)
            dc.DrawRectangle(i < lit ? brush : Empty, null, new Rect(i * (block + gap), 0, block, h));
    }
}
