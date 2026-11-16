using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class StartupRowViewModel(StartupEntry entry) : ObservableObject
{
    ImageSource? _icon;
    bool _iconRequested;

    public StartupEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Source => Entry.Source;
    public string Status => Entry.StatusText;
    public string StartupType => Entry.StartupType;
    public string Command => Entry.Command;
    public string GroupName => Txt.GroupName(Entry.Group);
    public bool IsDisabled => Entry.Enabled == false;
    public bool IsUnknown => Entry.Enabled is null;
    public bool IsCritical => Entry.CriticalReason is not null;
    public string SearchText => $"{Name} {Source} {Command} {Entry.Location} {StartupType} {Entry.Publisher} {Signer}";
    string? _signer;
    /// <summary>Who signed the file ("" while being checked or without a file; "Not signed" when nobody did).</summary>
    public string? Signer { get => _signer; set => Set(ref _signer, value); }
    public bool MicrosoftSigned { get; set; }
    /// <summary>Null = can be removed; otherwise, the reason (trash button tooltip).</summary>
    public string? CannotRemove { get; } = StartupRemoval.CannotRemove(entry);
    public bool CanRemove => CannotRemove is null;
    public string RemoveTip => CannotRemove ?? "Remove this entry (a backup is saved first)";

    /// <summary>Icon loaded only when the row appears on screen (virtualized list).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested) { _iconRequested = true; LoadIcon(); }
            return _icon;
        }
    }

    async void LoadIcon()
    {
        _icon = await IconCache.GetAsync(Entry.ImagePath, large: false);
        if (_icon is not null) Raise(nameof(Icon));
    }
}

/// <summary>
/// Startup in the style of Autoruns: one source per provider, Standard (what the user installed) and Advanced
/// (Microsoft tasks, hidden ones and services). Drivers only with their own filter and a warning. Nothing is deleted; one entry
/// at a time, through the source's own mechanism.
/// </summary>
public sealed class StartupPageViewModel : PageViewModel
{
    const string FilterAll = "all", FilterLogon = "logon", FilterTasks = "tasks", FilterServices = "services", FilterDrivers = "drivers", FilterHooks = "hooks";
    bool _hideMicrosoft;

    public bool HideMicrosoft { get => _hideMicrosoft; set { if (Set(ref _hideMicrosoft, value)) Refilter(); } }

    /// <summary>Signers are read after the list shows (one check per file, in parallel); rows fill in as they finish.</summary>
    async Task LoadSignersAsync(IReadOnlyList<StartupRowViewModel> rows, int scan)
    {
        var paths = rows.Select(r => r.Entry.ImagePath).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var signers = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() => Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
            p => signers[p!] = Signatures.Signer(p!)));
        if (scan != _scan) return;
        foreach (var r in rows)
        {
            if (r.Entry.ImagePath is not { } p) { r.Signer = ""; continue; }
            var signer = signers.GetValueOrDefault(p);
            var host = Signatures.IsHost(p);
            r.MicrosoftSigned = !host && Signatures.IsMicrosoft(signer); // a script or DLL run by a Windows host always shows
            r.Signer = signer is null ? (System.IO.File.Exists(Environment.ExpandEnvironmentVariables(p)) ? "Not signed" : "File not found")
                : host ? $"{signer} ({System.IO.Path.GetFileName(p)})" : signer;
        }
        if (_hideMicrosoft) Refilter();
    }

    ListCollectionView _view = new(new List<StartupRowViewModel>());
    StartupRowViewModel? _selected;
    OptionItem _filter;
    string _search = "", _coverage = "", _count = "";
    bool _advanced, _loading, _busy, _driversLoaded, _isError, _loaded;
    double _progress;
    string? _progressText, _message;
    IReadOnlyList<string> _problems = Array.Empty<string>();
    IReadOnlyList<OptionItem> _filters;
    int _scan;

    public StartupPageViewModel() : base("startup", Txt.PageStartup, "", Txt.PageStartupSubtitle)
    {
        Modes = new[] { new OptionItem(Txt.Standard, Txt.Standard) { IsSelected = true }, new OptionItem(Txt.Advanced, Txt.Advanced) };
        SelectModeCommand = new AsyncCommand(p => SetModeAsync((string)p! == Txt.Advanced), _ => !_loading && !_busy);
        _filters = BuildFilters();
        _filter = _filters[0];
        EnableCommand = new AsyncCommand(() => ToggleAsync(true), () => CanToggle(false));
        DisableCommand = new AsyncCommand(() => ToggleAsync(false), () => CanToggle(true));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        RemoveCommand = new AsyncCommand(p => RemoveAsync(p as StartupRowViewModel ?? _selected), p => !_busy && !_loading && (p as StartupRowViewModel ?? _selected) is { CanRemove: true });
        DelayChoices = new[] { new OptionItem("0", "At sign-in") }.Concat(DelayedStartup.Choices.Select(s => new OptionItem(s.ToString(), s < 60 ? $"{s} s" : $"{s / 60} min"))).ToArray();
        SelectDelayCommand = new AsyncCommand(p => SetDelayAsync(int.Parse((string)p!)), _ => !_busy && !_loading && CanDelaySelected);
    }

    public IReadOnlyList<OptionItem> DelayChoices { get; }
    public ICommand SelectDelayCommand { get; }
    public bool CanDelaySelected => _selected?.Entry is { } e && DelayedStartup.CanDelay(e);

    /// <summary>Marks the selected entry's current start: its delay task, else "At sign-in" when the entry is on, else nothing.</summary>
    async Task LoadDelayAsync()
    {
        foreach (var c in DelayChoices) c.IsSelected = false;
        if (_selected?.Entry is not { } e || !DelayedStartup.CanDelay(e)) return;
        var delay = await Task.Run(() => DelayedStartup.Delay(e));
        if (_selected?.Entry != e) return;
        var key = delay?.ToString() ?? (e.Enabled == true ? "0" : null);
        foreach (var c in DelayChoices) c.IsSelected = c.Key == key;
    }

    async Task SetDelayAsync(int seconds)
    {
        if (_selected?.Entry is not { } e || !DelayedStartup.CanDelay(e)) return;
        _busy = true;
        IsError = false;
        Message = Txt.Applying;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (seconds == 0) await DelayedStartup.RemoveAsync(e, enable: true, default);
            else await DelayedStartup.SetAsync(e, seconds, default);
            Message = seconds == 0 ? $"\"{e.Name}\" starts at sign-in again." : $"\"{e.Name}\" starts {DelayChoices.First(c => c.Key == seconds.ToString()).Label} after sign-in (task \"{DelayedStartup.TaskName(e)}\").";
        }
        catch (ElevationCancelledException ex) { Message = ex.Message; }
        catch (Exception ex)
        {
            Core.Persistence.Log.Error($"Startup: delay of {e.Id} failed", ex);
            Message = ex.Message;
            IsError = true;
        }
        finally { _busy = false; }
        await ScanAsync(_driversLoaded && _filter.Key == FilterDrivers);
    }

    public ICommand RemoveCommand { get; }

    async Task RemoveAsync(StartupRowViewModel? row)
    {
        if (row?.Entry is not { } e || !row.CanRemove) return;
        _busy = true;
        IsError = false;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (DelayedStartup.CanDelay(e)) await DelayedStartup.RemoveAsync(e, enable: false, default);
            var backup = await StartupRemoval.RemoveAsync(e);
            Message = $"\"{e.Name}\" removed. Backup: {backup}";
        }
        catch (Exception ex)
        {
            Core.Persistence.Log.Error($"Startup: removing {e.Id} failed", ex);
            Message = ex.Message;
            IsError = true;
        }
        finally { _busy = false; }
        if (Selected == row) Selected = null;
        await ScanAsync(_driversLoaded && _filter.Key == FilterDrivers); // the state shown always comes from a fresh read
    }

    public override bool ScrollsItself => true;
    public IReadOnlyList<OptionItem> Modes { get; }
    public ICommand SelectModeCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public ListCollectionView View { get => _view; private set => Set(ref _view, value); }
    public IReadOnlyList<OptionItem> Filters { get => _filters; private set => Set(ref _filters, value); }
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string Coverage { get => _coverage; private set => Set(ref _coverage, value); }
    public string CountText { get => _count; private set => Set(ref _count, value); }
    public IReadOnlyList<string> Problems { get => _problems; private set { Set(ref _problems, value); Raise(nameof(HasProblems)); } }
    public bool HasProblems => _problems.Count > 0;
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public string ModeDetail => _advanced ? Txt.StartupAdvancedDetail : Txt.StartupStandardDetail;

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Refilter(); }
    }

    public OptionItem SelectedFilter
    {
        get => _filter;
        set
        {
            if (value is null || value == _filter) return;
            if (value.Key == FilterDrivers && !_driversLoaded)
            {
                _filter = value;
                Raise();
                Toasts.Show(Txt.DriversWarning, ToastKind.Warning);
                MainViewModel.Fire(ScanAsync(includeDrivers: true));
                return;
            }
            _filter = value;
            Raise();
            Refilter();
        }
    }

    public StartupRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedDetails));
            Raise(nameof(SelectedWarning));
            Raise(nameof(SelectedReadOnly));
            Raise(nameof(CanDelaySelected));
            _ = LoadDelayAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => _selected is not null;
    public string? SelectedWarning => _selected?.Entry.CriticalReason;
    public string? SelectedReadOnly => _selected?.Entry is { CanToggle: false } e ? e.ReadOnlyReason ?? Txt.StateUnknownCantChange : null;

    public IReadOnlyList<KeyValuePair<string, string>> SelectedDetails
    {
        get
        {
            if (_selected?.Entry is not { } e) return Array.Empty<KeyValuePair<string, string>>();
            var list = new List<KeyValuePair<string, string>>
            {
                new(Txt.ColStatus, e.StatusText + (e.StatusDetail.Length > 0 ? " · " + e.StatusDetail : "")),
                new(Txt.ColStartupType, e.StartupType),
            };
            if (e.RunState is not null) list.Add(new(Txt.ColRunState, e.RunState));
            list.Add(new(Txt.ColSource, e.Source));
            list.Add(new(Txt.ColLocation, e.Location));
            if (e.Command.Length > 0) list.Add(new(Txt.ColCommand, e.Command));
            if (e.ImagePath is not null) list.Add(new(Txt.DetailFile, e.ImagePath));
            list.AddRange(e.Details);
            return list;
        }
    }

    public override Task RefreshAsync() => ScanAsync(_driversLoaded && _filter.Key == FilterDrivers);

    async Task SetModeAsync(bool advanced)
    {
        if (advanced == _advanced) return;
        _advanced = advanced;
        foreach (var m in Modes) m.IsSelected = (m.Key == Txt.Advanced) == advanced;
        Raise(nameof(ModeDetail));
        Filters = BuildFilters();
        _filter = Filters[0];
        Raise(nameof(SelectedFilter));
        await ScanAsync(includeDrivers: false);
    }

    IReadOnlyList<OptionItem> BuildFilters()
    {
        var list = new List<OptionItem>
        {
            new(FilterAll, _advanced ? Txt.FilterAllNoDrivers : Txt.FilterAll),
            new(FilterLogon, Txt.GroupLogon),
            new(FilterTasks, Txt.GroupTasks),
            new(FilterServices, Txt.GroupServices),
        };
        if (_advanced) { list.Add(new(FilterHooks, Txt.GroupHooks)); list.Add(new(FilterDrivers, Txt.GroupDrivers)); }
        return list;
    }

    async Task ScanAsync(bool includeDrivers)
    {
        var id = ++_scan;
        var keep = _selected?.Entry.Id;
        IsLoading = true;
        Progress = 0;
        var progress = new Progress<(int Done, int Total, string Source)>(p =>
        {
            if (id != _scan) return;
            Progress = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText = p.Source.Length > 0 ? Txt.ReadingSource(p.Source) : null;
        });
        try
        {
            var advanced = _advanced;
            var result = await Task.Run(() => StartupManager.Scan(advanced, includeDrivers, progress));
            if (id != _scan) return; // another read started later (mode or filter change)
            var rows = result.Entries
                .OrderBy(e => e.Group).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new StartupRowViewModel(e)).ToList();
            var view = new ListCollectionView(rows) { Filter = o => Visible((StartupRowViewModel)o) };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartupRowViewModel.GroupName)));
            _driversLoaded = includeDrivers;
            View = view;
            Problems = result.Problems;
            Coverage = Txt.StartupCoverage(StartupManager.Active(advanced, includeDrivers).Select(p => p.Name));
            Selected = keep is null ? null : rows.FirstOrDefault(r => r.Entry.Id == keep);
            _loaded = true;
            UpdateCount();
            _ = LoadSignersAsync(rows, id);
        }
        catch (Exception ex)
        {
            Core.Persistence.Log.Error("Startup scan failed", ex);
            if (id != _scan) return;
            Problems = new[] { Txt.StartupScanFailed(ex.Message) };
            // Drivers were not read: go back to the general filter so an empty list is not shown as if it were the result.
            if (_filter.Key == FilterDrivers && !_driversLoaded) { _filter = Filters[0]; Raise(nameof(SelectedFilter)); Refilter(); }
        }
        finally
        {
            if (id == _scan) { IsLoading = false; ProgressText = null; }
        }
    }

    bool Visible(StartupRowViewModel r)
    {
        var group = r.Entry.Group;
        var byFilter = _filter.Key switch
        {
            FilterLogon => group == StartupGroup.Logon,
            FilterTasks => group == StartupGroup.ScheduledTasks,
            FilterServices => group == StartupGroup.Services,
            FilterDrivers => group == StartupGroup.Drivers,
            FilterHooks => group == StartupGroup.Hooks,
            _ => group != StartupGroup.Drivers,
        };
        if (_hideMicrosoft && r.MicrosoftSigned) return false;
        var q = _search.Trim();
        return byFilter && (q.Length == 0 || MainViewModel.Matches(r.SearchText, q));
    }

    void Refilter()
    {
        _view.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        if (!_loaded) return;
        var total = _view.SourceCollection.Cast<StartupRowViewModel>().Count(r => _filter.Key == FilterDrivers || r.Entry.Group != StartupGroup.Drivers);
        CountText = Txt.EntriesCount(_view.Count, total);
    }

    bool CanToggle(bool enabled) => !_busy && !_loading && _selected?.Entry is { CanToggle: true } e && e.Enabled == enabled;

    async Task ToggleAsync(bool enable)
    {
        if (_selected?.Entry is not { CanToggle: true } e) return;
        var kind = e.Group switch { StartupGroup.Drivers => Txt.WordDriver, StartupGroup.Services => Txt.WordService, StartupGroup.ScheduledTasks => Txt.WordTask, _ => Txt.WordEntry };
        // Disabling something critical or part of Windows itself: applies and warns in a notification.
        var warning = enable ? null : e.CriticalReason is { } critical ? Txt.CriticalWarning(kind, critical) : e.WindowsComponent ? Txt.WindowsComponentWarning(kind) : null;

        _busy = true;
        IsError = false;
        Message = Txt.Applying;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (enable && DelayedStartup.CanDelay(e)) await DelayedStartup.RemoveAsync(e, enable: false, default); // else it would start twice
            await StartupManager.SetEnabledAsync(e, enable);
            Message = Txt.StartupChanged(e.Name, enable);
            if (warning is not null) Toasts.Show($"{e.Name}: {warning}", ToastKind.Warning);
        }
        catch (ElevationCancelledException ex) { Message = ex.Message; }
        catch (Exception ex)
        {
            Message = ex.Message;
            IsError = true;
        }
        finally
        {
            _busy = false;
        }
        await ScanAsync(_driversLoaded && _filter.Key == FilterDrivers); // the state shown always comes from a fresh read
    }
}
