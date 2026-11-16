using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using KosGovernor.Core;
using KosGovernor.ViewModels;

namespace KosGovernor.Views;

/// <summary>Live threads of a process, with the same "Apply Now" / "Always" adjustments (+T rules).</summary>
public partial class ThreadsWindow : Window
{
    static readonly ColumnDef[] Defs =
    {
        new("TID", "TID", 64, "Tid"),
        new("Description", "Description", 260, "Description", Center: false, Fill: true),
        new("Priority", "Priority", 100, "PriorityText"),
        new("Base", "Base", 50, "BaseText"),
        new("I/O", "I/O", 70, "IoText"),
        new("Memory", "Mem", 70, "MemText"),
        new("Ideal CPU", "Ideal CPU", 76, "IdealText"),
        new("Status", "Status", 80, "StatusText"),
        new("Boost", "Boost", 54, "BoostText"),
        new("Module", "Module", 170, "Module", Center: false),
    };

    readonly MainViewModel _main;
    readonly int _pid;
    readonly long _create;
    readonly string _exe;
    readonly CpuTopology _topo = CpuTopology.Current;
    readonly ObservableCollection<ThreadRow> _rows = new();
    readonly Dictionary<int, ThreadRow> _byTid = new();
    readonly ListCollectionView _view;
    readonly GridColumns _columns;
    readonly DispatcherTimer _timer = new();
    ModuleMap _modules = new();
    bool _refreshing, _ended;
    string _message = "";

    public ThreadsWindow(MainViewModel main, ProcessRow owner)
    {
        InitializeComponent();
        _main = main;
        _pid = owner.Pid;
        _create = owner.CreateTime;
        _exe = owner.Name;
        Title = Heading.Text = $"{_exe} ({_pid}) — Threads";
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.CustomSort = new ThreadComparer("TID", true);
        _view.Filter = o => o is ThreadRow r
            && (DescriptionSearch.Text.Length == 0 || r.Description.Contains(DescriptionSearch.Text, StringComparison.OrdinalIgnoreCase))
            && (ModuleSearch.Text.Length == 0 || r.Module.Contains(ModuleSearch.Text, StringComparison.OrdinalIgnoreCase));
        List.ItemsSource = _view;
        _columns = new GridColumns(List, Defs, Sort);
        ShowColumns();
        _main.Config.ColumnsChanged += ShowColumns;
        Closed += (_, _) => { _timer.Stop(); _main.Config.ColumnsChanged -= ShowColumns; };
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Config.General.GuiIntervalMs, Validation.MinGuiMs, Validation.MaxGuiMs));
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public GovernorConfig Config => _main.Config.Config;

    void ShowColumns()
    {
        var sort = (ThreadComparer)_view.CustomSort;
        _columns.Show(Config.General.HiddenThreadColumns, sort.Column, sort.Ascending);
    }

    void Sort(string column)
    {
        var current = (ThreadComparer)_view.CustomSort;
        _view.CustomSort = new ThreadComparer(column, current.Column != column || !current.Ascending);
        ShowColumns();
    }

    void OnSearch(object sender, TextChangedEventArgs e) => _view.Refresh();

    void OnMenu(object sender, ContextMenuEventArgs e)
    {
        var rows = List.SelectedItems.OfType<ThreadRow>().ToList();
        if (rows.Count == 0 || _ended) { e.Handled = true; return; }
        List.ContextMenu = Menus.ForThreads(this, rows);
    }

    sealed record Reading(ProcessEntry Process, List<(ThreadEntry Thread, ThreadDetails Details, string? Module)> Threads);

    async Task RefreshAsync()
    {
        if (_refreshing || _ended) return;
        _refreshing = true;
        try
        {
            var reading = await Task.Run(() =>
            {
                var p = Snapshot.Take().FirstOrDefault(x => x.Pid == _pid && x.CreateTime == _create);
                if (p is null) return null;
                var refreshed = false;
                var list = new List<(ThreadEntry, ThreadDetails, string?)>();
                foreach (var t in p.Threads)
                {
                    var d = ThreadControl.Read(t.Tid, _topo);
                    var module = d.StartAddress == IntPtr.Zero ? null : _modules.Find(d.StartAddress);
                    if (module is null && d.StartAddress != IntPtr.Zero && !refreshed)
                    {
                        refreshed = true;
                        _modules = ModuleMap.Read(_pid);
                        module = _modules.Find(d.StartAddress);
                    }
                    list.Add((t, d, module));
                }
                return new Reading(p, list);
            });
            if (reading is null)
            {
                _ended = true;
                _timer.Stop();
                Status.Text = "The process has ended.";
                return;
            }
            var alive = new HashSet<int>();
            foreach (var (t, d, module) in reading.Threads)
            {
                alive.Add(t.Tid);
                if (_byTid.TryGetValue(t.Tid, out var row) && row.CreateTime != t.CreateTime) { _rows.Remove(row); row = null; }
                if (row is null) { row = new ThreadRow(t); _byTid[t.Tid] = row; _rows.Add(row); }
                row.Update(t, d, reading.Process.BasePriority, module);
            }
            foreach (var gone in _byTid.Values.Where(r => !alive.Contains(r.Tid)).ToList()) { _rows.Remove(gone); _byTid.Remove(gone.Tid); }
            Status.Text = $"Threads  {reading.Threads.Count}{(_message.Length > 0 ? "     " + _message : "")}";
        }
        catch (Exception ex) { Log.Error("Refreshing the thread list failed", ex); }
        finally { _refreshing = false; }
    }

    void Report(string message, bool error = false)
    {
        _message = message;
        if (error) Log.Warn($"{_exe} threads: {message}");
        Status.Text = $"Threads  {_rows.Count}     {message}";
    }

    // ---------- actions ----------

    public void ApplyNow(IList<ThreadRow> rows, Action<ThreadRule> set)
    {
        var rule = new ThreadRule();
        set(rule);
        if (rule.Action == RuleAction.Terminate && !Dialog.Confirm("Terminate thread",
                $"End {rows.Count} thread(s) of {_exe}? The program will probably crash or misbehave.", "Terminate")) return;
        var affinity = ProcessControl.Read(_pid, false, _topo).Affinity ?? 0;
        var errors = new List<string>();
        foreach (var r in rows)
            errors.AddRange(ThreadControl.Apply(r.Tid, r.CreateTime, rule, affinity, Config.Masks, _topo).Select(e => $"{r.Tid}: {e}"));
        Report(errors.Count == 0 ? $"Applied to {rows.Count} thread(s)." : string.Join("; ", errors.Distinct()), errors.Count > 0);
        _ = RefreshAsync();
    }

    public void Resume(IList<ThreadRow> rows)
    {
        var errors = new List<string>();
        foreach (var r in rows)
        {
            try { ThreadControl.Resume(r.Tid); }
            catch (Exception ex) { errors.Add($"{r.Tid}: {ex.Message}"); }
        }
        Report(errors.Count == 0 ? "Resumed." : string.Join("; ", errors), errors.Count > 0);
        _ = RefreshAsync();
    }

    /// <summary>A thread matches by description; without a description, by the module where it starts.</summary>
    static (string? Description, string? Module) Key(ThreadRow r) =>
        r.Description.Length > 0 ? (r.Description, null) : (null, r.Module.Length > 0 ? r.Module : null);

    static bool Same(ThreadRule t, (string? Description, string? Module) k) =>
        string.Equals(t.Description ?? "", k.Description ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(t.Module ?? "", k.Module ?? "", StringComparison.OrdinalIgnoreCase);

    public ThreadRule? RuleFor(ThreadRow row) =>
        Config.Active.Rules.FirstOrDefault(r => r.Exe.Equals(_exe, StringComparison.OrdinalIgnoreCase))?.Threads.FirstOrDefault(t => Same(t, Key(row)));

    public void Always(IList<ThreadRow> rows, Action<ThreadRule> set)
    {
        var keys = rows.Select(Key).Distinct().ToList();
        if (keys.Any(k => k.Description is null && k.Module is null)) { Report("A thread without a description or start module can't get a rule.", true); return; }
        _main.Config.Mutate(c =>
        {
            var rule = c.Active.Rules.FirstOrDefault(r => r.Exe.Equals(_exe, StringComparison.OrdinalIgnoreCase));
            if (rule is null) c.Active.Rules.Add(rule = new ProcessRule { Exe = _exe });
            foreach (var k in keys)
            {
                var t = rule.Threads.FirstOrDefault(x => Same(x, k));
                if (t is null) rule.Threads.Add(t = new ThreadRule { Description = k.Description, Module = k.Module });
                set(t);
            }
        }, $"Thread rule saved for {_exe}.");
        Report($"Thread rule saved: {string.Join(", ", keys.Select(k => k.Description ?? k.Module))}.");
    }

    /// <summary>Descriptions and modules of the threads of open processes with that name (suggestions for the +T editor).</summary>
    public static (IReadOnlyList<string> Descriptions, IReadOnlyList<string> Modules) KnownThreads(string exe)
    {
        var descriptions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Snapshot.Take().Where(p => p.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            var map = ModuleMap.Read(p.Pid);
            foreach (var t in p.Threads)
            {
                if (ThreadControl.Description(t.Tid) is { } d) descriptions.Add(d);
                if (map.Find(ThreadControl.StartAddress(t.Tid)) is { } m) modules.Add(m);
            }
        }
        return (descriptions.ToList(), modules.ToList());
    }
}
