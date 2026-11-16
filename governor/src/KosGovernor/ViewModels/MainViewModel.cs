using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using KosGovernor.Core;
using KosGovernor.Views;

namespace KosGovernor.ViewModels;

/// <summary>A view of the sidebar, with a small count on the right (processes, rules, masks; On/Off for gamemode).</summary>
public sealed class SectionItem(string key, string title, string glyph) : ObservableObject
{
    string _count = "";
    public string Key => key;
    public string Title => title;
    public string Glyph => glyph;
    public string Count { get => _count; set => Set(ref _count, value); }
}

/// <summary>Live process list, sidebar, status bar and the "Apply Now" / "Always" actions of the menus and the details panel.</summary>
public sealed class MainViewModel : ObservableObject
{
    readonly Dictionary<int, ProcessRow> _byPid = new();
    readonly DispatcherTimer _timer = new();
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly SystemLoad _load = new();
    readonly CpuUsage _usage = new();
    ProcessGpu? _gpu;
    bool _gpuFailed;
    string _search = "", _cpu = "", _counts = "", _memory = "";
    string? _message;
    bool _refreshing, _engineRunning, _gamemode, _messageIsError;
    DateTime _configStamp;
    SectionItem _section;

    public MainViewModel()
    {
        Config = new ConfigViewModel(this);
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ProcessRow r && Matches(r);
        View.CustomSort = new ProcessComparer("Name", true);
        Sections = new[]
        {
            new SectionItem("Processes", "Processes", "\uE8FD"), new SectionItem("Rules", "Rules", "\uE9D5"), new SectionItem("Gamemode", "Gamemode", "\uE7FC"),
            new SectionItem("Masks", "CPU masks", "\uE950"), new SectionItem("Settings", "Settings", "\uE713"),
        };
        _section = Sections[0];
        Panel = new ProcessPanelViewModel(this);
        Config.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ConfigViewModel.StartWithWindows) or nameof(ConfigViewModel.SignInProblem)) Raise(nameof(EngineNote)); };
        ToggleGamemodeCommand = new RelayCommand(() => { EngineHost.EnsureRunning(); Names.Signal(Names.ToggleGamemode); });
        _timer.Tick += async (_, _) => await RefreshAsync();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Message = null; };
        _configStamp = ConfigStore.Stamp;
        ApplyInterval(Config.Config.General.GuiIntervalMs);
        _timer.Start();
        _ = RefreshAsync();
    }

    public CpuTopology Topology { get; } = CpuTopology.Current;
    public ConfigViewModel Config { get; }
    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public ListCollectionView View { get; }
    public ICommand ToggleGamemodeCommand { get; }
    public IReadOnlyList<SectionItem> Sections { get; }
    /// <summary>The details panel under the process list (the selected processes).</summary>
    public ProcessPanelViewModel Panel { get; }

    /// <summary>Processes, or one of the configuration views (Rules, Gamemode, CPU masks, Settings = ConfigViewModel's tabs).</summary>
    public SectionItem SelectedSection
    {
        get => _section;
        set
        {
            if (value is null || !Set(ref _section, value)) return;
            if (value.Key != "Processes") Config.Tab = value.Key == "Settings" ? "General" : value.Key;
            Raise(nameof(ShowConfig));
        }
    }
    public bool ShowConfig => _section.Key != "Processes";

    public void ShowSection(string key) => SelectedSection = Sections.First(s => s.Key == key);
    public string CpuText { get => _cpu; private set => Set(ref _cpu, value); }
    public string CountsText { get => _counts; private set => Set(ref _counts, value); }
    public string MemoryText { get => _memory; private set => Set(ref _memory, value); }
    public bool EngineRunning { get => _engineRunning; private set { if (Set(ref _engineRunning, value)) Raise(nameof(EngineNote)); } }
    /// <summary>Under "Engine running/stopped" in the sidebar, as the Settings view says it.</summary>
    public string EngineNote => !EngineRunning ? "Rules aren't applied."
        : Config.StartWithWindows ? "In the tray. Starts with Windows." : Config.SignInProblem ?? "In the tray. Doesn't start with Windows.";
    public bool GamemodeOn { get => _gamemode; private set => Set(ref _gamemode, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool MessageIsError { get => _messageIsError; private set => Set(ref _messageIsError, value); }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) View.Refresh(); }
    }

    public void Report(string message, bool error = false)
    {
        Message = message;
        MessageIsError = error;
        _messageTimer.Stop();
        _messageTimer.Start();
        if (error) Log.Warn(message);
    }

    public void ApplyInterval(int ms) => _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, Validation.MinGuiMs, Validation.MaxGuiMs));

    bool Matches(ProcessRow r)
    {
        if (r.Parent is { IsExpanded: false }) return false;
        if (_search.Length == 0) return true;
        var p = r.Parent ?? r;
        return p.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString() == _search
               || p.Services.Any(s => s.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    public void Sort(string column)
    {
        var current = (ProcessComparer)View.CustomSort;
        View.CustomSort = new ProcessComparer(column, current.Column != column || !current.Ascending);
        Raise(nameof(SortColumn));
    }

    public (string Column, bool Ascending) SortColumn => View.CustomSort is ProcessComparer c ? (c.Column, c.Ascending) : ("Name", true);

    // ---------- reading ----------

    sealed record Reading(List<ProcessEntry> Processes, Dictionary<int, ProcessDetails> Details, Dictionary<int, List<HostedService>> Services,
        Dictionary<int, double>? GpuPercent, Dictionary<int, long>? Vram);

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var known = _byPid.ToDictionary(p => p.Key, p => p.Value.CreateTime);
            var hidden = Config.Config.General.HiddenProcessColumns;
            var wantGpu = !hidden.Contains("GPU %") || !hidden.Contains("VRAM") || Panel.IsOpen;
            var reading = await Task.Run(() =>
            {
                var list = Snapshot.Take();
                _usage.Update(list, Environment.TickCount64, Topology.Cpus.Count);
                var details = new Dictionary<int, ProcessDetails>();
                foreach (var p in list)
                    if (p.Pid > 4) details[p.Pid] = ProcessControl.Read(p.Pid, !(known.TryGetValue(p.Pid, out var c) && c == p.CreateTime), Topology);
                var (gpu, vram) = wantGpu ? ReadGpu() : (null, null);
                return new Reading(list, details, Services.ByProcess(), gpu, vram);
            });
            Apply(reading);
            if (ConfigStore.Stamp != _configStamp) { _configStamp = ConfigStore.Stamp; Config.Reload(); }
        }
        catch (Exception ex) { Log.Error("Refreshing the process list failed", ex); }
        finally { _refreshing = false; }
    }

    void Apply(Reading reading)
    {
        var alive = new HashSet<int>();
        var showIcon = !Config.Config.General.HiddenProcessColumns.Contains("Icon");
        foreach (var e in reading.Processes)
        {
            alive.Add(e.Pid);
            if (_byPid.TryGetValue(e.Pid, out var row) && row.CreateTime != e.CreateTime) { Remove(row); row = null; }
            var d = reading.Details.GetValueOrDefault(e.Pid) ?? new ProcessDetails();
            if (row is null)
            {
                row = new ProcessRow(e, OnExpandChanged) { ImagePath = d.ImagePath };
                _byPid[e.Pid] = row;
                Rows.Add(row);
                _ = LoadIconAsync(row, d.ImagePath);
            }
            row.ShowIcon = showIcon;
            row.Update(e, d, Topology);
            row.UpdateUse(_usage.Percent(e), e.PrivateWorkingSet, reading.GpuPercent?.GetValueOrDefault(e.Pid), reading.Vram?.GetValueOrDefault(e.Pid));
            var services = (IReadOnlyList<HostedService>?)reading.Services.GetValueOrDefault(e.Pid) ?? Array.Empty<HostedService>();
            if (!services.Select(s => s.Name).SequenceEqual(row.Services.Select(s => s.Name)))
            {
                var expanded = row.IsExpanded;
                if (expanded) SetChildren(row, false);
                row.Services = services;
                if (expanded) SetChildren(row, true);
            }
        }
        foreach (var gone in _byPid.Values.Where(r => !alive.Contains(r.Pid)).ToList()) Remove(gone);

        CpuText = $"CPU  {_load.CpuPercent():0.0}%";
        CountsText = $"Processes  {reading.Processes.Count}     Threads  {reading.Processes.Sum(p => p.Threads.Count)}     Handles  {reading.Processes.Sum(p => (long)p.HandleCount)}";
        var (load, used, total) = SystemLoad.Memory();
        MemoryText = $"Memory  {load}% ({used / 1073741824.0:0.0} / {total / 1073741824.0:0.0} GB)";
        EngineRunning = Names.EngineRunning;
        GamemodeOn = Names.Exists(Names.GamemodeOn);
        var config = Config.Config;
        (Sections[0].Count, Sections[1].Count, Sections[2].Count, Sections[3].Count) =
            (_byPid.Count.ToString(), config.Active.Rules.Count.ToString(), GamemodeOn ? "On" : "Off", config.Masks.Count.ToString());
    }

    /// <summary>GPU counters, opened the first time a GPU column is shown; missing counters (no GPU driver) only blank the columns.</summary>
    (Dictionary<int, double>?, Dictionary<int, long>?) ReadGpu()
    {
        if (_gpuFailed) return (null, null);
        try
        {
            _gpu ??= new ProcessGpu();
            return _gpu.Read();
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            Log.Warn($"GPU use per process can't be read: {ex.Message}");
            return (null, null);
        }
    }

    static async Task LoadIconAsync(ProcessRow row, string? path) => row.Icon = await (path is null ? IconCache.GenericAsync() : IconCache.GetAsync(path, large: false));

    void Remove(ProcessRow row)
    {
        if (row.IsExpanded) SetChildren(row, false);
        Rows.Remove(row);
        _byPid.Remove(row.Pid);
    }

    void OnExpandChanged(ProcessRow row) => SetChildren(row, row.IsExpanded);

    void SetChildren(ProcessRow row, bool show)
    {
        if (show) foreach (var s in row.Services) Rows.Add(new ProcessRow(row, s));
        else foreach (var child in Rows.Where(r => r.Parent == row).ToList()) Rows.Remove(child);
    }

    /// <summary>Names of the open programs ("Process name" field of the rules).</summary>
    public IEnumerable<string> RunningNames => _byPid.Values.Select(r => r.Name).Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Open programs, one per name with its icon, games first ("Add from open programs").</summary>
    public List<OpenProgram> OpenPrograms(GameList games) => _byPid.Values.Where(r => r.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => new OpenProgram { Name = g.First().Name, Icon = g.Select(r => r.Icon).FirstOrDefault(i => i is not null), IsGame = games.IsGame(g.Key) })
        .OrderByDescending(p => p.IsGame).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IEnumerable<string> ServicesOf(string exe) => _byPid.Values.Where(r => r.Name.Equals(exe, StringComparison.OrdinalIgnoreCase))
        .SelectMany(r => r.Services).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // ---------- actions ----------

    static IEnumerable<ProcessRow> Owners(IEnumerable<ProcessRow> rows) => rows.Select(r => r.Parent ?? r).Distinct();

    /// <summary>Applies now to the chosen processes (service rows apply to their own process).</summary>
    public void ApplyNow(IEnumerable<ProcessRow> rows, Action<ProcessLevelRule> set)
    {
        var errors = new List<string>();
        var count = 0;
        foreach (var r in Owners(rows))
        {
            var rule = new ProcessRule { Exe = r.Name };
            set(rule);
            if (rule.Action is RuleAction.Terminate or RuleAction.Suspend && !Confirm(rule.Action, r)) continue;
            var e = ProcessControl.Apply(r.Pid, rule, Config.Config.Masks, Topology);
            if (e.Count > 0) errors.Add($"{r.Name} ({r.Pid}): {string.Join("; ", e)}");
            else Log.Info($"Apply now on {r.Name} ({r.Pid}): {Labels.Summary(rule)}");
            count++;
        }
        if (count == 0) return;
        Report(errors.Count == 0 ? $"Applied to {count} process{(count == 1 ? "" : "es")}." : string.Join("\n", errors), errors.Count > 0);
        _ = RefreshAsync();
    }

    static bool Confirm(RuleAction action, ProcessRow r) => action == RuleAction.Terminate
        ? Dialog.Confirm("Terminate", $"End {r.Name} ({r.Pid}) now? Unsaved work in it is lost.", "Terminate")
        : Dialog.Confirm("Suspend", $"Freeze {r.Name} ({r.Pid})? It stays frozen until you resume it.", "Suspend");

    public void Resume(IEnumerable<ProcessRow> rows)
    {
        foreach (var r in Owners(rows))
        {
            try { ProcessControl.Resume(r.Pid); Report($"{r.Name} resumed."); }
            catch (Exception ex) { Report($"{r.Name}: {ex.Message}", true); }
        }
        _ = RefreshAsync();
    }

    /// <summary>
    /// Creates or changes the program's rule (or the service's, on a service row) in the active profile; with <paramref name="gamemode"/>, the
    /// one that only applies while gamemode is on. False if it wasn't saved (cancelled, or the error is in the status bar).
    /// </summary>
    public bool Always(IEnumerable<ProcessRow> rows, Action<ProcessLevelRule> set, bool gamemode = false)
    {
        var targets = rows.Select(r => (Exe: (r.Parent ?? r).Name, r.Service)).Distinct().ToList();
        if (targets.Count == 0) return false;
        var probe = new ProcessRule();
        set(probe);
        if (probe.Action == RuleAction.Terminate && !Dialog.Confirm("Always terminate",
                $"{string.Join(", ", targets.Select(t => t.Exe).Distinct())} will be ended every time it starts while this profile is active.", "Create rule")) return false;
        return Config.Mutate(c =>
        {
            var list = gamemode ? c.Active.OnEnable.Rules : c.Active.Rules;
            foreach (var (exe, service) in targets)
            {
                var rule = list.FirstOrDefault(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
                if (rule is null) list.Add(rule = new ProcessRule { Exe = exe });
                if (service is null) { set(rule); continue; }
                var s = rule.Services.FirstOrDefault(x => x.Service.Equals(service.Name, StringComparison.OrdinalIgnoreCase));
                if (s is null) rule.Services.Add(s = new ServiceRule { Service = service.Name });
                set(s);
            }
        }, $"Rule saved for {string.Join(", ", targets.Select(t => t.Service?.DisplayName ?? t.Exe).Distinct())}.");
    }

    public void OpenLocation(ProcessRow row)
    {
        if ((row.Parent ?? row).ImagePath is not { } path) { Report("The program's path can't be read.", true); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public void Properties(ProcessRow row)
    {
        if ((row.Parent ?? row).ImagePath is not { } path) { Report("The program's path can't be read.", true); return; }
        ShellProperties.Show(path);
    }
}
