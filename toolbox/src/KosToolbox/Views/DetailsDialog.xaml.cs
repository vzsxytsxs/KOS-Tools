using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.Views;

/// <summary>A row of the top panel; <see cref="Note"/> appears smaller, below the text.</summary>
public sealed record DetailFact(string Label, string Text, string? Note = null);

/// <summary>Details of a setting: panel with state and source, and the table of values read × options.</summary>
public partial class DetailsDialog : Window
{
    DetailsDialog() => InitializeComponent();

    /// <param name="focus">Option highlighted in the table: the current one, or the closest when the state is partial.</param>
    public static void Show(string title, string summary, IReadOnlyList<DetailFact> facts,
        IReadOnlyList<(string Key, string Label)> options, string? focus, IReadOnlyList<SettingReading> readings)
    {
        var d = new DetailsDialog { Title = title };
        d.Heading.Text = title;
        d.Summary.Text = summary;
        d.AddFacts(facts);
        if (readings.Count > 0) d.AddReadings(options, focus, readings);
        else d.ReadingsSection.Visibility = Visibility.Collapsed;
        if (Application.Current?.MainWindow is { IsVisible: true } owner) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.SourceInitialized += (_, _) => DarkTitleBar.Apply(d);
        d.Loaded += (_, _) => Motion.Pop(d.Root);
        d.ShowDialog();
    }

    Brush Res(string key) => (Brush)FindResource(key);
    Style StyleOf(string key) => (Style)FindResource(key);

    void AddFacts(IReadOnlyList<DetailFact> facts)
    {
        foreach (var f in facts)
        {
            var row = Facts.RowDefinitions.Count;
            Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = row == 0 ? 0 : 8;
            Place(Facts, new TextBlock { Text = f.Label, Style = StyleOf("Secondary"), Margin = new Thickness(0, top, 24, 0) }, row, 0);
            var value = new StackPanel { Margin = new Thickness(0, top, 0, 0) };
            value.Children.Add(new TextBox { Text = f.Text, Style = StyleOf("ReadOnlyText") }); // selectable (paths)
            if (f.Note is not null) value.Children.Add(new TextBlock { Text = f.Note, Style = StyleOf("Secondary"), Margin = new Thickness(2, 2, 0, 0) }); // 2 px: aligns with the TextBox text
            Place(Facts, value, row, 1);
        }
    }

    void AddReadings(IReadOnlyList<(string Key, string Label)> options, string? focus, IReadOnlyList<SettingReading> readings)
    {
        var g = Readings;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var _ in options) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64 });
        for (var i = 0; i <= readings.Count; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var focusColumn = options.Select(o => o.Key).ToList().IndexOf(focus ?? "");
        if (focusColumn >= 0) Place(g, new Border { Background = Res("ControlBrush") }, 0, focusColumn + 2, rowSpan: readings.Count + 1);

        Place(g, Header(Txt.DetailSetting, false, left: true), 0, 0);
        Place(g, Header(Txt.DetailValue, false, left: true), 0, 1);
        for (var c = 0; c < options.Count; c++) Place(g, Header(options[c].Label, c == focusColumn, left: false), 0, c + 2);

        for (var i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            var row = i + 1;
            Place(g, new Border { BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0) }, row, 0, columnSpan: g.ColumnDefinitions.Count);

            var (name, path) = Split(r.Display);
            var setting = new StackPanel { Margin = new Thickness(14, 8, 10, 8) };
            setting.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
            if (path is not null) setting.Children.Add(new TextBlock { Text = path, Style = StyleOf("Secondary"), FontSize = 11.5 });
            Place(g, setting, row, 0);

            var value = new TextBlock { Margin = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 180 };
            if (r.Error is not null) { value.Text = Txt.ReadErrorWith(r.Error); value.Foreground = Res("ErrorBrush"); }
            else if (r.Value is null) { value.Text = Txt.DetailMissing; value.Foreground = Res("TextSecondaryBrush"); value.FontStyle = FontStyles.Italic; }
            else value.Text = r.Value;
            Place(g, value, row, 1);

            for (var c = 0; c < options.Count; c++)
            {
                var match = r.Matches.GetValueOrDefault(options[c].Key);
                Place(g, new TextBlock
                {
                    Text = match switch { true => "✓", false => "✗", null => "–" },
                    FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = match == true ? FontWeights.SemiBold : FontWeights.Normal,
                    // What blocks the highlighted option is shown in yellow.
                    Foreground = Res(match == true ? "TextBrush" : match == false && c == focusColumn ? "WarningBrush" : "TextSecondaryBrush"),
                    Opacity = match is null ? 0.5 : 1,
                }, row, c + 2);
            }
        }
    }

    TextBlock Header(string text, bool focus, bool left) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 110,
        TextAlignment = left ? TextAlignment.Left : TextAlignment.Center,
        HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(left ? 14 : 8, 10, 8, 8),
        Foreground = Res(focus ? "TextBrush" : "TextSecondaryBrush"),
    };

    /// <summary>"HKLM\...\wuauserv → Start" becomes "wuauserv › Start" with the path below.</summary>
    internal static (string Name, string? Path) Split(string display)
    {
        var i = display.LastIndexOf(" → ", StringComparison.Ordinal);
        if (i < 0) return (display, null);
        var path = display[..i];
        return ($"{path[(path.LastIndexOf('\\') + 1)..]} › {display[(i + 3)..]}", path);
    }

    static void Place(Grid g, UIElement e, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        Grid.SetRowSpan(e, rowSpan);
        Grid.SetColumnSpan(e, columnSpan);
        g.Children.Add(e);
    }
}
