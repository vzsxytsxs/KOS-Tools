using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace KosGovernor.Views;

/// <summary>List column: key (General › columns), title, width and what to show.</summary>
public sealed record ColumnDef(string Key, string Header, double Width, string Path, bool Center = true, bool Fill = false, string? TemplateKey = null);

/// <summary>Builds the GridView columns (visible according to General), sorts by title and stretches the main column.</summary>
public sealed class GridColumns
{
    readonly ListView _list;
    readonly GridView _view;
    readonly IReadOnlyList<ColumnDef> _defs;
    readonly Dictionary<GridViewColumn, ColumnDef> _shown = new();
    readonly Action<string> _sort;
    readonly Style _leftHeader;

    public GridColumns(ListView list, IReadOnlyList<ColumnDef> defs, Action<string> sort)
    {
        _list = list;
        _defs = defs;
        _sort = sort;
        var header = (Style)list.FindResource("GridHeader");
        _view = new GridView { ColumnHeaderContainerStyle = header };
        _leftHeader = new Style(typeof(GridViewColumnHeader), header) { Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left) } };
        list.View = _view;
        list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        list.SizeChanged += (_, _) => Stretch();
    }

    public void Show(ICollection<string> hidden, string sortKey, bool ascending)
    {
        _view.Columns.Clear();
        _shown.Clear();
        foreach (var d in _defs.Where(d => d.Fill || !hidden.Contains(d.Key)))
        {
            var col = new GridViewColumn
            {
                Header = d.Header + (d.Key == sortKey ? (ascending ? "  ∧" : "  ∨") : ""),
                Width = d.Width,
                CellTemplate = d.TemplateKey is { } key ? (DataTemplate)_list.FindResource(key) : Text(d.Path, d.Center),
                HeaderContainerStyle = d.Center ? null : _leftHeader,
            };
            _shown[col] = d;
            _view.Columns.Add(col);
        }
        Stretch();
    }

    void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } col } && _shown.TryGetValue(col, out var def)) _sort(def.Key);
    }

    void Stretch()
    {
        var fill = _shown.FirstOrDefault(p => p.Value.Fill).Key;
        if (fill is null || _list.ActualWidth <= 0) return;
        var others = _view.Columns.Where(c => c != fill).Sum(c => c.ActualWidth > 0 ? c.ActualWidth : c.Width);
        fill.Width = Math.Max(_shown[fill].Width, _list.ActualWidth - others - 24);
    }

    static DataTemplate Text(string path, bool center) => (DataTemplate)XamlReader.Parse(
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        $"<TextBlock Text='{{Binding {path}, Mode=OneWay}}' TextAlignment='{(center ? "Center" : "Left")}' TextTrimming='CharacterEllipsis' />" +
        "</DataTemplate>");
}
