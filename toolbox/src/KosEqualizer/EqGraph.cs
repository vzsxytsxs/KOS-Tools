using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KosToolbox.Core.Text;

namespace KosEqualizerApp;

/// <summary>EqView.TopOn on a ScrollViewer: back to the top whenever the bound value changes (another device selected).</summary>
public static class EqView
{
    public static readonly DependencyProperty TopOnProperty = DependencyProperty.RegisterAttached("TopOn", typeof(object), typeof(EqView),
        new PropertyMetadata(null, (d, _) => { if (d is ScrollViewer sv) KosToolbox.Views.Motion.ScrollToTop(sv); }));
    public static object? GetTopOn(DependencyObject d) => d.GetValue(TopOnProperty);
    public static void SetTopOn(DependencyObject d, object? value) => d.SetValue(TopOnProperty, value);
}

/// <summary>
/// A profile's frequency response: log frequency 20 Hz–20 kHz across, dB up (±12, wider in 6 dB steps when the curve needs it).
/// <see cref="Points"/> are (Hz, dB); replacing them redraws. Drawn like the toolbox's DataPlot.
/// </summary>
public sealed class EqGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<Point>), typeof(EqGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<Point>? Points { get => (IReadOnlyList<Point>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    const double Left = 34, Right = 12, Top = 10, Bottom = 22, MinHz = 20, MaxHz = 20000;
    static readonly double[] Marks = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), 1));
    static readonly Pen Zero = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), 1));
    static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), 1.6) { LineJoin = PenLineJoin.Round });
    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    static readonly Typeface Face = new("Segoe UI");

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < Left + Right + 40 || h < Top + Bottom + 40) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, Label, dpi);

        var points = Points ?? Array.Empty<Point>();
        var peak = points.Count == 0 ? 0 : points.Max(p => Math.Abs(p.Y));
        var range = Math.Min(30, Math.Max(12, Math.Ceiling(peak / 6) * 6));
        double X(double hz) => plot.Left + Math.Log(Math.Clamp(hz, MinHz, MaxHz) / MinHz) / Math.Log(MaxHz / MinHz) * plot.Width;
        double Y(double db) => plot.Top + (range - Math.Clamp(db, -range - 1, range + 1)) / (2 * range) * plot.Height;

        foreach (var hz in Marks)
        {
            var x = Math.Round(X(hz)) + 0.5;
            dc.DrawLine(Grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Text(Txt.EqHz(hz));
            dc.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, plot.Left, plot.Right - t.Width), plot.Bottom + 3));
        }
        var step = range <= 18 ? 6 : 12;
        for (var db = -Math.Floor(range / step) * step; db <= range; db += step)
        {
            var y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(db == 0 ? Zero : Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(Txt.EqDbShort(db));
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }

        if (points.Count > 1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(points[0].X), Y(points[0].Y)), false, false);
                for (var i = 1; i < points.Count; i++) c.LineTo(new Point(X(points[i].X), Y(points[i].Y)), true, true);
            }
            g.Freeze();
            dc.PushClip(new RectangleGeometry(plot));
            dc.DrawGeometry(null, Line, g);
            dc.Pop();
        }
        dc.DrawRectangle(null, Border, plot);
    }
}
