using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KosToolbox.Views;

/// <summary>One bar: its height, the value written over it and an optional second line (the difference from the reference).</summary>
public sealed record BarValue(double Value, string Text, string? Note = null);

/// <summary>A group of bars side by side (one per series) under a label.</summary>
public sealed record BarGroup(string Label, IReadOnlyList<BarValue> Bars);

/// <summary>Grouped bar chart in <see cref="DataPlot"/>'s style (frame capture comparison): series in its tones, a legend, values written on the bars.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty GroupsProperty = DependencyProperty.Register(nameof(Groups), typeof(IReadOnlyList<BarGroup>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LegendProperty = DependencyProperty.Register(nameof(Legend), typeof(IReadOnlyList<string>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty YTitleProperty = DependencyProperty.Register(nameof(YTitle), typeof(string), typeof(BarChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarGroup>? Groups { get => (IReadOnlyList<BarGroup>?)GetValue(GroupsProperty); set => SetValue(GroupsProperty, value); }
    public IReadOnlyList<string>? Legend { get => (IReadOnlyList<string>?)GetValue(LegendProperty); set => SetValue(LegendProperty, value); }
    public string YTitle { get => (string)GetValue(YTitleProperty); set => SetValue(YTitleProperty, value); }

    const double Left = 58, Right = 12, Top = 22, Bottom = 30;
    // The same tones as DataPlot.
    static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), 1));
    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)));
    static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    static readonly Brush Value = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)));
    static readonly Typeface Face = new("Segoe UI");

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static readonly Brush Hollow = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)));

    static readonly Pen Edge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))), 1.5) { DashStyle = Frozen(new DashStyle(new[] { 2.0, 1.5 }, 0)) });

    /// <summary>A bar of series <paramref name="i"/> in DataPlot's tones: the third outlined, the fourth with a dashed light edge, so no two look alike.</summary>
    internal static void DrawBar(DrawingContext dc, int i, Rect r)
    {
        var brush = new SolidColorBrush(DataPlot.Tone(i));
        brush.Freeze();
        var inner = new Rect(r.X + 0.75, r.Y + 0.75, Math.Max(0, r.Width - 1.5), Math.Max(0, r.Height - 1.5));
        if (i == 2) dc.DrawRectangle(Hollow, new Pen(brush, 1.5), inner);
        else if (i >= 3) dc.DrawRectangle(brush, Edge, inner);
        else dc.DrawRectangle(brush, null, r);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < Left + Right + 20 || h < Top + Bottom + 20) return;
        var plot = new Rect(Left, Top, w - Left - Right, h - Top - Bottom);
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string s, Brush? brush = null, double size = 11) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush ?? Label, dpi);
        dc.DrawText(Text(YTitle), new Point(Left, 3));
        DataPlot.DrawLegend(dc, Legend, w - Right, 3, s => Text(s), true);
        var groups = Groups?.Where(g => g.Bars.Count > 0).ToList() ?? new();
        if (groups.Count == 0) { dc.DrawRectangle(null, Border, plot); return; }

        // Room above the tallest bar for its two lines of text.
        var (y0, y1, ys) = DataPlot.Ticks(0, groups.Max(g => g.Bars.Max(b => b.Value)) * 1.18);
        double Y(double v) => plot.Bottom - (v - y0) / (y1 - y0) * plot.Height;
        for (var v = y0; v <= y1 + ys / 2; v += ys)
        {
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Text(DataPlot.Format(v, ys));
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        var slot = plot.Width / groups.Count;
        for (var g = 0; g < groups.Count; g++)
        {
            var bars = groups[g].Bars;
            var width = Math.Min(90, slot * 0.8 / bars.Count);
            var x = plot.Left + g * slot + (slot - width * bars.Count) / 2;
            var label = Text(groups[g].Label);
            dc.DrawText(label, new Point(plot.Left + g * slot + (slot - label.Width) / 2, plot.Bottom + 4));
            for (var i = 0; i < bars.Count; i++, x += width)
            {
                var top = Y(Math.Max(0, bars[i].Value));
                DrawBar(dc, i, new Rect(x + 2, top, width - 4, plot.Bottom - top));
                var text = Text(bars[i].Text, Value);
                var y = top - text.Height - 1;
                if (bars[i].Note is { Length: > 0 } note)
                {
                    var n = Text(note, null, 10);
                    dc.DrawText(n, new Point(x + (width - n.Width) / 2, top - n.Height - 1));
                    y -= n.Height;
                }
                dc.DrawText(text, new Point(x + (width - text.Width) / 2, y));
            }
        }
        dc.DrawRectangle(null, Border, plot);
    }
}
