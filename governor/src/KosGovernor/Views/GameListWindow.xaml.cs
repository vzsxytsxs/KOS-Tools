using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KosGovernor.Core;
using KosGovernor.ViewModels;

namespace KosGovernor.Views;

/// <summary>A row of the automatic gamemode game list.</summary>
public sealed record GameListRow(string Program, string Game, string Status, string Source, bool IsGame, bool IsMine);

/// <summary>
/// Full list for automatic gamemode (the built-in one with the user's changes): search by program or game
/// name, filters and the same Gamemode menu as the process list to change a row.
/// </summary>
public partial class GameListWindow : Window
{
    static readonly ColumnDef[] Defs =
    {
        new("Program", "Program", 300, "Program", Center: false),
        new("Game", "Game", 280, "Game", Center: false, Fill: true),
        new("Status", "Status", 110, "Status"),
        new("Source", "Source", 120, "Source"),
    };

    readonly MainViewModel _main;
    readonly GridColumns _columns;
    List<GameListRow> _rows = new();
    ICollectionView? _view;
    string _sort = "Program";
    bool _ascending = true;

    public GameListWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _columns = new GridColumns(List, Defs, Sort);
        _main.Config.PropertyChanged += OnConfigChanged;
        Closed += (_, _) => _main.Config.PropertyChanged -= OnConfigChanged;
        Load();
        Loaded += (_, _) => Search.Focus();
    }

    void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigViewModel.GameRows)) Load();
    }

    void Load()
    {
        var builtIn = GameList.BuiltIn.ToDictionary(g => GameList.Key(g.Name), StringComparer.OrdinalIgnoreCase);
        var list = new GameList(_main.Config.Config.AutoGamemode.Games);
        _rows = list.Entries.Select(e =>
        {
            var mine = list.IsUserEntry(e.Name);
            var source = !mine ? "Built-in" : builtIn.ContainsKey(GameList.Key(e.Name)) ? "Changed by you" : "Added by you";
            return new GameListRow(GameList.Key(e.Name) + ".exe", e.DisplayName ?? "", e.IsBlacklisted ? "Never a game" : "Game", source, !e.IsBlacklisted, mine);
        }).ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Visible;
        List.ItemsSource = _view;
        Sort(_sort, keep: true);
    }

    bool Visible(object o)
    {
        if (o is not GameListRow r) return false;
        if (Games.IsChecked == true && !r.IsGame || Never.IsChecked == true && r.IsGame || Mine.IsChecked == true && !r.IsMine) return false;
        var q = Search.Text.Trim();
        return q.Length == 0 || r.Program.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Game.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Sort(string column) => Sort(column, keep: false);

    void Sort(string column, bool keep)
    {
        if (!keep) _ascending = column != _sort || !_ascending;
        _sort = column;
        if (_view is null) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(column, _ascending ? ListSortDirection.Ascending : ListSortDirection.Descending));
            if (column != "Program") _view.SortDescriptions.Add(new SortDescription("Program", ListSortDirection.Ascending));
        }
        _columns.Show(Array.Empty<string>(), _sort, _ascending);
        UpdateStatus();
    }

    void OnSearch(object sender, TextChangedEventArgs e) => Refresh();
    void OnFilter(object sender, RoutedEventArgs e) => Refresh();

    void Refresh()
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var shown = _view?.Cast<object>().Count() ?? 0;
        Status.Text = $"Showing {shown} of {_rows.Count} · {_rows.Count(r => r.IsGame)} games, {_rows.Count(r => !r.IsGame)} never a game, {_rows.Count(r => r.IsMine)} changed by you · right-click a row to change it";
    }

    void OnMenu(object sender, ContextMenuEventArgs e)
    {
        if (List.SelectedItem is not GameListRow row) { e.Handled = true; return; }
        var menu = new ContextMenu();
        foreach (var item in Menus.GameItems(_main, row.Program, _main.Config.Config)) menu.Items.Add(item);
        List.ContextMenu = menu;
    }
}
