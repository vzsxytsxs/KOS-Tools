using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KosGovernor.ViewModels;

namespace KosGovernor.Views;

public partial class MainWindow : Window
{
    static readonly ColumnDef[] Defs =
    {
        new("Name", "Name", 240, "DisplayName", Center: false, Fill: true, TemplateKey: "NameCell"),
        new("PID", "PID", 64, "PidText"),
        new("CPU %", "CPU %", 60, "CpuUseText"),
        new("Memory", "Memory", 84, "MemUseText"),
        new("GPU %", "GPU %", 60, "GpuText"),
        new("VRAM", "VRAM", 80, "VramText"),
        new("CPU Priority", "CPU", 86, "CpuText"),
        new("I/O Priority", "I/O", 70, "IoText"),
        new("Memory Priority", "MemPri", 76, "MemText"),
        new("Threads", "Thrd", 52, "ThreadsText"),
        new("Status", "Status", 80, "StatusText"),
        new("Boost", "Boost", 54, "BoostText"),
        new("Eco", "Eco", 48, "EcoText"),
        new("Affinity", "Affinity", 84, "AffinityText"),
        new("CPU Sets", "CpuSets", 84, "CpuSetsText"),
    };

    readonly MainViewModel _vm;
    readonly GridColumns _columns;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        FocusRelease.Attach(this);
        _columns = new GridColumns(List, Defs, key => { _vm.Sort(key); ShowColumns(); });
        ShowColumns();
        _vm.Config.ColumnsChanged += ShowColumns;
    }

    void ShowColumns()
    {
        var (column, ascending) = _vm.SortColumn;
        _columns.Show(_vm.Config.Config.General.HiddenProcessColumns, column, ascending);
    }

    IList<ProcessRow> Selected => List.SelectedItems.OfType<ProcessRow>().ToList();

    void OnMenu(object sender, ContextMenuEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0) { e.Handled = true; return; }
        List.ContextMenu = Menus.ForProcesses(_vm, rows, OpenThreads);
    }

    void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the expand arrow doesn't open the threads.
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Primitives.ToggleButton>(d) is not null) return;
        if (List.SelectedItem is ProcessRow row) OpenThreads(row.Parent ?? row);
    }

    void OpenThreads(ProcessRow row) => new ThreadsWindow(_vm, row) { Owner = this }.Show();

    // The details panel follows the list's selection; closing it unselects.
    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => _vm.Panel.Show(Selected);
    void OnClosePanel(object sender, RoutedEventArgs e) => List.UnselectAll();
    void OnPanelThreads(object sender, RoutedEventArgs e) { if (_vm.Panel.Owner is { } row) OpenThreads(row); }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        var config = _vm.Config;
        var menu = new ContextMenu { PlacementTarget = ProfileButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top }; // the button sits at the bottom of the sidebar
        for (var i = 0; i < config.Config.Profiles.Count; i++)
        {
            var index = i;
            var item = new MenuItem { Header = config.Config.Profiles[i].Name, IsChecked = i == config.Config.ActiveProfile };
            item.Click += (_, _) => config.SwitchProfile(index);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        void Add(string header, Action action) { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("New profile…", () => config.NewProfile(copy: false));
        Add("Duplicate…", () => config.NewProfile(copy: true));
        Add("Rename…", config.RenameProfile);
        Add("Delete", config.DeleteProfile);
        menu.IsOpen = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_vm.Config.CapturingHotkey)
        {
            _vm.Config.CaptureHotkey(Keyboard.Modifiers, e.Key == Key.System ? e.SystemKey : e.Key);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_vm.ShowConfig)
        {
            Search.Focus();
            Search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Search.IsKeyboardFocused && Search.Text.Length > 0)
        {
            Search.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.Panel.IsOpen && !_vm.ShowConfig)
        {
            List.UnselectAll();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }
}
