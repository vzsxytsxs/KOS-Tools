using System.Windows;
using System.Windows.Media;

namespace KosToolbox.Views;

/// <summary>
/// History graph like the Task Manager one: subtle grid, filled area and line. Values range from 0
/// to <see cref="Maximum"/>, newest on the right; replacing the array redraws.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>False: no background box or border, and the grid is three horizontal lines (Home's list and inspector, inside their own card).</summary>
    public static readonly DependencyProperty FramedProperty = DependencyProperty.Register(nameof(Framed), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public bool Framed { get => (bool)GetValue(FramedProperty); set => SetValue(FramedProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), 1));
    static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), 1.2) { LineJoin = PenLineJoin.Round });
    static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xE6, 0xE6, 0xE6)));
    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        if (Framed) dc.DrawRectangle(Background, null, rect);
        if (ShowGrid)
        {
            for (var i = 1; i < 4; i++) dc.DrawLine(Grid, new Point(0, Math.Round(h * i / 4) + 0.5), new Point(w, Math.Round(h * i / 4) + 0.5));
            if (Framed) for (var i = 1; i < 6; i++) dc.DrawLine(Grid, new Point(Math.Round(w * i / 6) + 0.5, 0), new Point(Math.Round(w * i / 6) + 0.5, h));
        }
        if (Values is { Count: > 1 } v && Maximum > 0)
        {
            var step = w / (v.Count - 1);
            Point P(int i) => new(i * step, h - Math.Clamp(v[i] / Maximum, 0, 1) * (h - 2) - 1);
            var area = new StreamGeometry();
            var line = new StreamGeometry();
            using (var a = area.Open())
            using (var l = line.Open())
            {
                a.BeginFigure(new Point(0, h), isFilled: true, isClosed: true);
                l.BeginFigure(P(0), isFilled: false, isClosed: false);
                for (var i = 0; i < v.Count; i++)
                {
                    a.LineTo(P(i), isStroked: false, isSmoothJoin: false);
                    if (i > 0) l.LineTo(P(i), isStroked: true, isSmoothJoin: true);
                }
                a.LineTo(new Point(w, h), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();
            line.Freeze();
            dc.DrawGeometry(Fill, null, area);
            dc.DrawGeometry(null, Line, line);
        }
        if (Framed) dc.DrawRectangle(null, Border, rect);
    }
}
