using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KosToolbox.Views;

/// <summary>Bars: one filled bar per point, as wide as the spacing of the points (a histogram).</summary>
public enum PlotStyle { Lines, Dots, Stem, Bars }

/// <summary>
/// X/Y plot with axes and grid (mouse test, Sleep(1), frame time and ping graphs): up to four series (light, grey, then dashed tones; an optional legend), drawn as lines, dots or stems.
/// Replacing <see cref="Series"/> redraws. Moving over the plot shows a crosshair and the values of the point nearest to the cursor (every series
/// at that X); a click pins it where it is, another click or a right click lets it go.
/// </summary>
public sealed class DataPlot : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<IReadOnlyList<Point>>), typeof(DataPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DataPlot)d).ClearPin()));
    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(nameof(StyleKind), typeof(PlotStyle), typeof(DataPlot),
        new FrameworkPropertyMetadata(PlotStyle.Lines, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty XTitleProperty = DependencyProperty.Register(nameof(XTitle), typeof(string), typeof(DataPlot),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YTitleProperty = DependencyProperty.Register(nameof(YTitle), typeof(string), typeof(DataPlot),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A fixed Y window (both set): points outside it are cut off by the plot's edge. NaN = the range of the data.</summary>
    public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(nameof(YMin), typeof(double), typeof(DataPlot),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(nameof(YMax), typeof(double), typeof(DataPlot),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(nameof(Marks), typeof(IReadOnlyList<double>), typeof(DataPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => (IReadOnlyList<IReadOnlyList<Point>>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    /// <summary>X values drawn as vertical lines across the plot in the warning color (lost pings).</summary>
    public IReadOnlyList<double>? Marks { get => (IReadOnlyList<double>?)GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    public double YMin { get => (double)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    public double YMax { get => (double)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }
    public PlotStyle StyleKind { get => (PlotStyle)GetValue(StyleKindProperty); set => SetValue(StyleKindProperty, value); }
    public string XTitle { get => (string)GetValue(XTitleProperty); set => SetValue(XTitleProperty, value); }
    public string YTitle { get => (string)GetValue(YTitleProperty); set => SetValue(YTitleProperty, value); }

    const double Left = 58, Right = 12, Top = 22, Bottom = 30;
    static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), 1));
    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    // Up to four series (frame capture comparison): tones, and dashes from the third on, since the theme has no colors.
    static readonly Color[] Colors = { Color.FromRgb(0xE6, 0xE6, 0xE6), Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0xB8, 0xB8, 0xB8), Color.FromRgb(0x5C, 0x5C, 0x5C) };
    static readonly DashStyle[] Dashes = { DashStyles.Solid, DashStyles.Solid, Frozen(new DashStyle(new[] { 4.0, 2.0 }, 0)), Frozen(new DashStyle(new[] { 1.0, 1.5 }, 0)) };
    internal static Color Tone(int i) => Colors[Math.Min(i, Colors.Length - 1)];
    internal static DashStyle Dash(int i) => Dashes[Math.Min(i, Dashes.Length - 1)];

    public static readonly DependencyProperty LegendProperty = DependencyProperty.Register(nameof(Legend), typeof(IReadOnlyList<string>), typeof(DataPlot),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Names of the series, drawn top right with a sample of each line.</summary>
    public IReadOnlyList<string>? Legend { get => (IReadOnlyList<string>?)GetValue(LegendProperty); set => SetValue(LegendProperty, value); }

    /// <summary>The legend at the right of the title row (also used by <see cref="BarChart"/>).</summary>
    internal static void DrawLegend(DrawingContext dc, IReadOnlyList<string>? names, double right, double y, Func<string, FormattedText> text, bool bars)
    {
        if (names is not { Count: > 0 }) return;
        var x = right;
        for (var i = names.Count - 1; i >= 0; i--)
        {
            var t = text(names[i]);
            x -= t.Width;
            dc.DrawText(t, new Point(x, y));
            x -= 22;
            var brush = new SolidColorBrush(Tone(i));
            brush.Freeze();
            if (bars) BarChart.DrawBar(dc, i, new Rect(x + 6, y + t.Height / 2 - 5, 10, 10));
            else dc.DrawLine(new Pen(brush, 1.6) { DashStyle = Dash(i) }, new Point(x + 2, y + t.Height / 2), new Point(x + 18, y + t.Height / 2));
            x -= 12;
        }
    }
    static readonly Typeface Face = new("Segoe UI");

    // The plot as last drawn, so the mouse can be turned back into data coordinates.
    Rect _plot;
    double _x0, _x1, _y0, _y1;
    List<IReadOnlyList<Point>> _drawn = new();
    Point? _hover;
    Point? _pinned; // data coordinates (X, and the Y of the point that was nearest) of the pinned point

    void ClearPin()
    {
        _pinned = null;
        _hover = null;
    }

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
        DrawLegend(dc, Legend, w - Right, 3, Text, false);
        var xt = Text(XTitle);
        dc.DrawText(xt, new Point(plot.Right - xt.Width, h - xt.Height - 1));

        var series = Series?.Where(s => s.Count > 0).ToList() ?? new();
        _drawn = series;
        _plot = plot;
        var marks = Marks ?? Array.Empty<double>();
        if (series.Count == 0 && marks.Count == 0)
        {
            dc.DrawRectangle(null, Border, plot);
            return;
        }
        var allX = series.SelectMany(s => s.Select(p => p.X)).Concat(marks).ToList();
        double minX = allX.Min(), maxX = allX.Max();
        var binWidth = StyleKind == PlotStyle.Bars ? BinWidth(series) : 0;
        if (binWidth > 0) { minX -= binWidth / 2; maxX += binWidth / 2; }
        double minY = series.Count == 0 ? 0 : series.Min(s => s.Min(p => p.Y)), maxY = series.Count == 0 ? 1 : series.Max(s => s.Max(p => p.Y));
        if (StyleKind is PlotStyle.Stem or PlotStyle.Bars) { minY = Math.Min(minY, 0); maxY = Math.Max(maxY, 0); }
        if (double.IsFinite(YMin) && double.IsFinite(YMax) && YMax > YMin) (minY, maxY) = (YMin, YMax);
        var (x0, x1, xs) = Ticks(minX, maxX);
        var (y0, y1, ys) = Ticks(minY, maxY);
        (_x0, _x1, _y0, _y1) = (x0, x1, y0, y1);
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
        if (marks.Count > 0)
        {
            var markPen = new Pen(TryFindResource("WarningBrush") as Brush ?? Brushes.Orange, 1);
            markPen.Freeze();
            foreach (var m in marks)
            {
                var x = Math.Round(X(m)) + 0.5;
                dc.DrawLine(markPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }
        for (var i = series.Count - 1; i >= 0; i--) // the first series on top
        {
            var points = series[i];
            var brush = new SolidColorBrush(Tone(i));
            brush.Freeze();
            if (StyleKind == PlotStyle.Bars)
            {
                // A histogram: filled bars, see-through when several series overlap, with an outline so each stays readable.
                var fill = new SolidColorBrush(Color.FromArgb((byte)(series.Count == 1 ? 0xB0 : 0x70 - i * 0x10), Tone(i).R, Tone(i).G, Tone(i).B));
                fill.Freeze();
                var outline = new Pen(brush, 1) { DashStyle = Dash(i) };
                outline.Freeze();
                var floor = Y(0);
                foreach (var p in points)
                {
                    var left = X(p.X - binWidth / 2) + 0.5;
                    var width = Math.Max(1, X(p.X + binWidth / 2) - X(p.X - binWidth / 2) - 1);
                    var top = Y(p.Y);
                    if (floor - top < 0.5) continue;
                    dc.DrawRectangle(fill, series.Count == 1 ? null : outline, new Rect(left, top, width, floor - top));
                }
                continue;
            }
            var pen = new Pen(brush, StyleKind == PlotStyle.Dots ? 2.5 : 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (StyleKind == PlotStyle.Lines) pen.DashStyle = Dash(i);
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
        DrawCursor(dc, plot, Text);
    }

    /// <summary>The smallest spacing between neighbouring points of the first series: a bar's width.</summary>
    static double BinWidth(List<IReadOnlyList<Point>> series)
    {
        var points = series[0];
        var best = double.MaxValue;
        for (var i = 1; i < points.Count; i++)
        {
            var d = points[i].X - points[i - 1].X;
            if (d > 1e-12 && d < best) best = d;
        }
        return best == double.MaxValue ? 1 : best;
    }

    /// <summary>The point of <paramref name="points"/> closest to <paramref name="x"/> (binary search when X only grows, else the closest on screen).</summary>
    static Point? Nearest(IReadOnlyList<Point> points, double x)
    {
        if (points.Count == 0) return null;
        var sorted = true;
        for (var i = 1; i < points.Count && sorted; i += Math.Max(1, points.Count / 64)) sorted = points[i].X >= points[i - 1].X;
        if (!sorted) return points.MinBy(p => Math.Abs(p.X - x));
        int lo = 0, hi = points.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].X < x) lo = mid; else hi = mid;
        }
        return Math.Abs(points[lo].X - x) <= Math.Abs(points[hi].X - x) ? points[lo] : points[hi];
    }

    /// <summary>Pins the point nearest to <paramref name="x"/> (data coordinates), as a click would; for the screenshot probe.</summary>
    public void PinAtX(double x)
    {
        _pinned = _drawn.Count > 0 ? Nearest(_drawn[0], x) : null;
        InvalidateVisual();
    }

    double ToDataX(double px) => _x0 + (px - _plot.Left) / _plot.Width * (_x1 - _x0);
    double ToPixelX(double x) => _plot.Left + (x - _x0) / (_x1 - _x0) * _plot.Width;
    double ToPixelY(double y) => _plot.Bottom - (y - _y0) / (_y1 - _y0) * _plot.Height;

    /// <summary>A crosshair at the pinned point, else at the point under the mouse, with a box saying what the point is.</summary>
    void DrawCursor(DrawingContext dc, Rect plot, Func<string, FormattedText> text)
    {
        if (_drawn.Count == 0 || _x1 <= _x0 || _y1 <= _y0) return;
        double? x = _pinned is { } p ? p.X : _hover is { } h && plot.Contains(h) ? ToDataX(h.X) : null;
        if (x is null) return;
        var hits = _drawn.Select(s => Nearest(s, x.Value)).ToList();
        if (hits[0] is not { } first) return;
        var line = new Pen(new SolidColorBrush(_pinned is null ? Color.FromRgb(0x70, 0x70, 0x70) : Color.FromRgb(0xE6, 0xE6, 0xE6)), 1) { DashStyle = _pinned is null ? DashStyles.Dash : DashStyles.Solid };
        var cx = Math.Round(ToPixelX(first.X)) + 0.5;
        dc.PushClip(new RectangleGeometry(plot));
        dc.DrawLine(line, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), 1.5);
        for (var i = 0; i < hits.Count; i++)
            if (hits[i] is { } pt) dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)), ring, new Point(ToPixelX(pt.X), ToPixelY(pt.Y)), 3.5, 3.5);
        dc.Pop();

        var lines = new List<FormattedText> { text($"{(string.IsNullOrEmpty(XTitle) ? "X" : XTitle)}  {Num(first.X)}") };
        for (var i = 0; i < hits.Count; i++)
            if (hits[i] is { } pt)
                lines.Add(text($"{(Legend is { } names && i < names.Count ? names[i] : string.IsNullOrEmpty(YTitle) ? "Y" : YTitle)}  {Num(pt.Y)}"));
        var width = lines.Max(l => l.Width) + 16;
        var height = lines.Sum(l => l.Height) + 10;
        var anchor = _hover is { } m && _pinned is null ? m : new Point(cx, plot.Top + 10);
        var box = new Rect(Math.Clamp(anchor.X + 14, plot.Left + 2, Math.Max(plot.Left + 2, plot.Right - width - 2)), Math.Clamp(anchor.Y + 14, plot.Top + 2, Math.Max(plot.Top + 2, plot.Bottom - height - 2)), width, height);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x14, 0x14)), new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)), 1), box, 4, 4);
        var y = box.Top + 5;
        foreach (var l in lines)
        {
            l.SetForegroundBrush(Brushes.WhiteSmoke);
            dc.DrawText(l, new Point(box.Left + 8, y));
            y += l.Height;
        }
    }

    static string Num(double v) => Math.Abs(v) >= 1000 ? v.ToString("#,0.#", CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _hover = _plot.Contains(p) ? p : null;
        Cursor = _hover is null ? Cursors.Arrow : Cursors.Cross;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var p = e.GetPosition(this);
        if (!_plot.Contains(p) || _drawn.Count == 0) return;
        var near = Nearest(_drawn[0], ToDataX(p.X));
        // Clicking the pinned point again lets it go.
        _pinned = near is { } n && _pinned is { } old && Math.Abs(ToPixelX(old.X) - ToPixelX(n.X)) < 4 ? null : near;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_pinned is null) return;
        _pinned = null;
        InvalidateVisual();
    }

    /// <summary>Axis from a round number below to one above, about five steps of 1, 2 or 5 × 10^n.</summary>
    internal static (double Start, double End, double Step) Ticks(double min, double max)
    {
        if (max - min < 1e-9) { min -= 1; max += 1; }
        var raw = (max - min) / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    internal static string Format(double v, double step) =>
        Math.Abs(v) < step / 1e6 ? "0" : v.ToString(step >= 1 ? "#,0" : step >= 0.1 ? "0.0" : step >= 0.01 ? "0.00" : "0.000", CultureInfo.CurrentCulture);
}
