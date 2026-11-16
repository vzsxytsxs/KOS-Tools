using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KosToolbox.Views;

public enum PlotStyle { Lines, Dots, Stem }

/// <summary>
/// X/Y plot with axes and grid (mouse test and Sleep(1) graphs): one or two series (light and grey), drawn as lines, dots or stems.
/// Replacing <see cref="Series"/> redraws.
/// </summary>
public sealed class DataPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<IReadOnlyList<Point>>), typeof(DataPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(nameof(StyleKind), typeof(PlotStyle), typeof(DataPlot),
        new FrameworkPropertyMetadata(PlotStyle.Lines, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty XTitleProperty = DependencyProperty.Register(nameof(XTitle), typeof(string), typeof(DataPlot),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YTitleProperty = DependencyProperty.Register(nameof(YTitle), typeof(string), typeof(DataPlot),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => (IReadOnlyList<IReadOnlyList<Point>>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public PlotStyle StyleKind { get => (PlotStyle)GetValue(StyleKindProperty); set => SetValue(StyleKindProperty, value); }
    public string XTitle { get => (string)GetValue(XTitleProperty); set => SetValue(XTitleProperty, value); }
    public string YTitle { get => (string)GetValue(YTitleProperty); set => SetValue(YTitleProperty, value); }

    const double Left = 58, Right = 12, Top = 22, Bottom = 30;
    static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), 1));
    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    static readonly Color[] Colors = { Color.FromRgb(0xE6, 0xE6, 0xE6), Color.FromRgb(0x80, 0x80, 0x80) };
    static readonly Typeface Face = new("Segoe UI");

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < Left + Right + 20 || h < Top + Bottom + 20) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, Label, dpi);
        dc.DrawText(Text(YTitle), new Point(Left, 3));
        var xt = Text(XTitle);
        dc.DrawText(xt, new Point(plot.Right - xt.Width, h - xt.Height - 1));

        var series = Series?.Where(s => s.Count > 0).ToList();
        if (series is not { Count: > 0 })
        {
            dc.DrawRectangle(null, Border, plot);
            return;
        }
        double minX = series.Min(s => s.Min(p => p.X)), maxX = series.Max(s => s.Max(p => p.X));
        double minY = series.Min(s => s.Min(p => p.Y)), maxY = series.Max(s => s.Max(p => p.Y));
        if (StyleKind == PlotStyle.Stem) { minY = Math.Min(minY, 0); maxY = Math.Max(maxY, 0); }
        var (x0, x1, xs) = Ticks(minX, maxX);
        var (y0, y1, ys) = Ticks(minY, maxY);
        double X(double v) => plot.Left + (v - x0) / (x1 - x0) * plot.Width;
        double Y(double v) => plot.Bottom - (v - y0) / (y1 - y0) * plot.Height;

        for (var v = x0; v <= x1 + xs / 2; v += xs)
        {
            var x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(Grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Text(Format(v, xs));
            if (x - t.Width / 2 > plot.Left - 20 && x + t.Width / 2 < plot.Right - xt.Width - 8) dc.DrawText(t, new Point(x - t.Width / 2, plot.Bottom + 3));
        }
        for (var v = y0; v <= y1 + ys / 2; v += ys)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(Format(v, ys));
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }

        dc.PushClip(new RectangleGeometry(plot));
        for (var i = series.Count - 1; i >= 0; i--) // the first series on top
        {
            var points = series[i];
            var brush = new SolidColorBrush(Colors[Math.Min(i, Colors.Length - 1)]);
            brush.Freeze();
            var pen = new Pen(brush, StyleKind == PlotStyle.Dots ? 2.5 : 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                var zero = Y(0);
                for (var k = 0; k < points.Count; k++)
                {
                    var p = new Point(X(points[k].X), Y(points[k].Y));
                    switch (StyleKind)
                    {
                        case PlotStyle.Lines:
                            if (k == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, true);
                            break;
                        case PlotStyle.Dots: // a dot = a tiny segment with round caps
                            c.BeginFigure(p, false, false);
                            c.LineTo(new Point(p.X + 0.01, p.Y), true, false);
                            break;
                        default:
                            c.BeginFigure(new Point(p.X, zero), false, false);
                            c.LineTo(p, true, false);
                            break;
                    }
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        dc.Pop();
        dc.DrawRectangle(null, Border, plot);
    }

    /// <summary>Axis from a round number below to one above, about five steps of 1, 2 or 5 × 10^n.</summary>
    static (double Start, double End, double Step) Ticks(double min, double max)
    {
        if (max - min < 1e-9) { min -= 1; max += 1; }
        var raw = (max - min) / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    static string Format(double v, double step) =>
        Math.Abs(v) < step / 1e6 ? "0" : v.ToString(step >= 1 ? "#,0" : step >= 0.1 ? "0.0" : step >= 0.01 ? "0.00" : "0.000", CultureInfo.CurrentCulture);
}
