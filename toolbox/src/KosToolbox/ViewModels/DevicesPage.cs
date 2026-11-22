using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Devices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class IrqRowViewModel(PnpDevice device, InterruptConfig config, IrqState? effective, bool input)
{
    public PnpDevice Device { get; } = device;
    public InterruptConfig Config { get; } = config;
    public IrqDevice Irq { get; } = new(device.InstanceId, device.Name, IrqLayout.RoleOf(device, input), device.InterruptSupport, config, effective);
    public bool Pending => IrqResources.Pending(Config, Irq.Effective, Device.InterruptSupport, Cpus.AllMask);
    public string InEffect => Txt.IrqInEffect(Irq.Effective, Pending, Cpus.AllMask);
    public string Name => Device.Name;
    public string Class => Device.Class;
    public string Support => Txt.IrqSupport(Device.InterruptSupport);
    public bool SupportsMsi => (Device.InterruptSupport & 6) is > 0; // null (no data) does not count as support
    public string Msi => Config.Msi switch { null => Txt.NotSetShort, 0 => Txt.Off, _ => Txt.On };
    public string Limit => Config.Limit?.ToString() ?? Txt.NotSetShort;
    public string Priority => Label(DevicesPageViewModel.Priorities, Config.Priority);
    public string Policy => Label(DevicesPageViewModel.Policies, Config.Policy)
        + (Config.Policy == Interrupts.PolicySpecified && Config.Mask is { } m ? $" ({Cpus.Format(m)})" : "");
    public string SearchText => $"{Name} {Class} {Device.InstanceId}";

    static string Label(IReadOnlyList<OptionItem> options, uint? value) =>
        options.FirstOrDefault(o => o.Key == (value?.ToString() ?? ""))?.Label ?? value?.ToString() ?? "";
}

public sealed class GhostRowViewModel(PnpDevice device) : ObservableObject
{
    bool _checked;
    public PnpDevice Device { get; } = device;
    public string Name => Device.HasAffinity ? $"{Device.Name} ({Txt.GhostAffinity})" : Device.Name;
    public string Class => Device.Class.Length > 0 ? Device.Class : Txt.Unknown;
    public string LastSeen => Device.LastArrival?.ToString("d") ?? "";
    public string InstanceId => Device.InstanceId;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public string SearchText => $"{Name} {Class} {InstanceId}";
}

/// <summary>
/// Devices: interrupts (MSI, message limit, priority
/// and affinity) of connected PCI devices, and removal of ghost devices. Nothing is applied without confirmation.
/// </summary>
public sealed class DevicesPageViewModel : PageViewModel
{
    const string IrqTab = "irq", GhostTab = "ghost";
    public static readonly IReadOnlyList<OptionItem> MsiModes = Options(("", Txt.NotSet), ("0", Txt.IrqLineBased), ("1", Txt.IrqMsi));
    public static readonly IReadOnlyList<OptionItem> Priorities = Options(("", Txt.NotSet), ("0", "Undefined"), ("1", "Low"), ("2", "Normal"), ("3", "High"));
    public static readonly IReadOnlyList<OptionItem> Policies = Options(("", Txt.NotSet), ("0", "Machine default"), ("1", "All close processors"),
        ("2", "One close processor"), ("3", "All processors"), ("4", "Specified processors"), ("5", "Spread messages across all processors"));

    ListCollectionView _irqView = new(new List<IrqRowViewModel>()), _ghostView = new(new List<GhostRowViewModel>());
    IrqRowViewModel? _selected;
    OptionItem _msi = MsiModes[0], _priority = Priorities[0], _policy = Policies[0];
    string _limit = "", _cpus = "", _search = "";
    string? _loadError, _message;
    bool _msiOnly = true, _loading, _busy, _isError, _hasOriginal; // without the filter, the list fills with chipset functions that have no interrupt
    List<IrqRowViewModel> _rows = new();
    IReadOnlyList<string> _advice = Array.Empty<string>();
    List<IrqPick> _picks = new();

    readonly bool _ghostsOnly;

    /// <summary>
    /// <paramref name="ghostsOnly"/>: only ghost-device removal (tab of the Cleanup page); otherwise, only the
    /// interrupts (Devices page). Each instance reads only its own list.
    /// </summary>
    public DevicesPageViewModel(bool ghostsOnly = false)
        : base(ghostsOnly ? "ghosts" : "devices", ghostsOnly ? Txt.TabGhostDevices : Txt.PageDevices, "\uE772",
            ghostsOnly ? "Devices that aren't connected anymore." : "Interrupt settings (MSI, priority, affinity) of connected PCI devices.")
    {
        _ghostsOnly = ghostsOnly;
        Tabs = ghostsOnly ? new[] { new OptionItem(GhostTab, Txt.TabGhostDevices) { IsSelected = true } } : new[] { new OptionItem(IrqTab, Txt.TabInterrupts) { IsSelected = true } };
        SelectTabCommand = new RelayCommand(p =>
        {
            foreach (var t in Tabs) t.IsSelected = t.Key == (string)p!;
            Raise(nameof(ShowIrq));
            Raise(nameof(ShowGhosts));
        });
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !_busy && _selected is not null);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => !_busy && _hasOriginal);
        CheckAllCommand = new RelayCommand(() => Check(true));
        UncheckAllCommand = new RelayCommand(() => Check(false));
        RemoveCommand = new AsyncCommand(RemoveAsync, () => !_busy && !_loading);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ApplyLayoutCommand = new AsyncCommand(ApplyLayoutAsync, () => !_busy && !_loading && _picks.Any(p => !p.Kept));
        EnableMsiAllCommand = new AsyncCommand(EnableMsiAllAsync, () => !_busy && !_loading);
        RestartDeviceCommand = new AsyncCommand(RestartDeviceAsync, () => !_busy && _selected is not null);
    }

    static IReadOnlyList<OptionItem> Options(params (string Key, string Label)[] o) => o.Select(x => new OptionItem(x.Key, x.Label)).ToArray();

    public override bool ScrollsItself => true;
    public IReadOnlyList<OptionItem> Tabs { get; }
    public bool HasTabs => Tabs.Count > 1;
    public bool ShowIrq => !_ghostsOnly;
    public bool ShowGhosts => _ghostsOnly;
    public ICommand SelectTabCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand UncheckAllCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ApplyLayoutCommand { get; }
    public ICommand EnableMsiAllCommand { get; }
    public ICommand RestartDeviceCommand { get; }

    // ---------- advice and suggested layout (from the last read) ----------
    public IReadOnlyList<string> Advice { get => _advice; private set => Set(ref _advice, value); }
    public IReadOnlyList<string> LayoutRows => _picks.Count == 0 ? new[] { Txt.LayoutEmpty } : _picks.Select(p => Txt.LayoutRow(p.Device.Name, p.Mask, p.Kept)).ToArray();

    public ListCollectionView IrqView { get => _irqView; private set => Set(ref _irqView, value); }
    public ListCollectionView GhostView { get => _ghostView; private set => Set(ref _ghostView, value); }
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public string CpuInfo => Txt.CpuInfo(Cpus.Count);
    public string GhostCount => Txt.GhostCount(_ghostView.Count, _ghostView.SourceCollection.Cast<object>().Count());

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) { _irqView.Refresh(); _ghostView.Refresh(); Raise(nameof(GhostCount)); } }
    }

    public bool MsiOnly
    {
        get => _msiOnly;
        set { if (Set(ref _msiOnly, value)) _irqView.Refresh(); }
    }

    // ---------- interrupt editor ----------
    public IReadOnlyList<OptionItem> MsiOptions => MsiModes;
    public IReadOnlyList<OptionItem> PriorityOptions => Priorities;
    public IReadOnlyList<OptionItem> PolicyOptions => Policies;
    public bool HasSelection => _selected is not null;
    public string? SelectedName => _selected?.Name;
    public string? SelectedDetail => _selected is { } s ? Txt.IrqDetail(s.Device.InstanceId, s.Support, s.Device.MessageMaximum) : null;
    public OptionItem EditMsi { get => _msi; set { if (value is not null) Set(ref _msi, value); } }
    public OptionItem EditPriority { get => _priority; set { if (value is not null) Set(ref _priority, value); } }
    public OptionItem EditPolicy { get => _policy; set { if (value is not null && Set(ref _policy, value)) Raise(nameof(CpusEnabled)); } }
    public string EditLimit { get => _limit; set => Set(ref _limit, value ?? ""); }
    public string EditCpus { get => _cpus; set => Set(ref _cpus, value ?? ""); }
    public bool CpusEnabled => _policy.Key == Interrupts.PolicySpecified.ToString();

    public IrqRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Load(value?.Config);
            _hasOriginal = value is not null && Interrupts.Original(value.Device.InstanceId) is not null;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedName));
            Raise(nameof(SelectedDetail));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    void Load(InterruptConfig? c)
    {
        EditMsi = Pick(MsiModes, c?.Msi);
        EditPriority = Pick(Priorities, c?.Priority);
        EditPolicy = Pick(Policies, c?.Policy);
        EditLimit = c?.Limit?.ToString() ?? "";
        EditCpus = c?.Mask is { } m ? Cpus.Format(m) : "";
    }

    static OptionItem Pick(IReadOnlyList<OptionItem> options, uint? value) => options.FirstOrDefault(o => o.Key == (value?.ToString() ?? "")) ?? options[0];
    static uint? ValueOf(OptionItem o) => o.Key.Length == 0 ? null : uint.Parse(o.Key);

    public override async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        var keep = _selected?.Device.InstanceId;
        try
        {
            var (irq, ghosts) = await Task.Run(() =>
                (_ghostsOnly ? new List<IrqRowViewModel>() : ReadIrqRows(),
                 _ghostsOnly ? GhostDevices.List().OrderBy(d => d.Class).ThenBy(d => d.Name).Select(d => new GhostRowViewModel(d)).ToList() : new List<GhostRowViewModel>()));
            _rows = irq;
            if (!_ghostsOnly)
            {
                var devices = irq.Select(r => r.Irq).ToList();
                var cores = IrqLayout.Cores;
                Advice = IrqAdvice.Find(devices, cores.FirstOrDefault(c => (c.Mask & 1) == 1)?.Mask ?? 1, Cpus.AllMask) is { Count: > 0 } found ? found : new[] { Txt.AdviceNone };
                _picks = IrqLayout.Suggest(devices, cores);
                Raise(nameof(LayoutRows));
            }
            IrqView = new ListCollectionView(irq) { Filter = o => o is IrqRowViewModel r && (!_msiOnly || r.SupportsMsi) && Matches(r.SearchText) };
            var view = new ListCollectionView(ghosts) { Filter = o => o is GhostRowViewModel r && Matches(r.SearchText) };
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GhostRowViewModel.Class)));
            GhostView = view;
            Raise(nameof(GhostCount));
            LoadError = null;
            Selected = keep is null ? null : irq.FirstOrDefault(r => r.Device.InstanceId == keep);
        }
        catch (Exception ex)
        {
            Log.Error("Reading devices failed", ex);
            LoadError = Txt.DevicesReadFailed(ex.Message);
        }
        finally { IsLoading = false; }
    }

    bool Matches(string text) => _search.Trim() is not { Length: > 0 } q || MainViewModel.Matches(text, q);

    static List<IrqRowViewModel> ReadIrqRows()
    {
        HashSet<string> input;
        try { input = IrqResources.InputControllers(); }
        catch (Exception ex)
        {
            Log.Warn($"Input controllers couldn't be read: {ex.Message}");
            input = new();
        }
        return Interrupts.PciDevices().Select(d => new IrqRowViewModel(d, Interrupts.Read(d.InstanceId), IrqResources.Read(d.InstanceId), input.Contains(d.InstanceId)))
            .OrderBy(r => r.Class).ThenBy(r => r.Name).ToList();
    }

    // ---------- suggested layout, MSI on all, device restart ----------
    async Task ApplyLayoutAsync()
    {
        var changes = _picks.Where(p => !p.Kept).Select(p =>
        {
            var c = p.Device.Config.Copy(); // MSI, message limit and priority stay as they are
            c.Policy = Interrupts.PolicySpecified;
            c.Mask = p.Mask;
            return (Row: _rows.First(r => r.Device.InstanceId == p.Device.InstanceId), Config: c);
        }).Where(x => !x.Config.SameValues(x.Row.Config)).ToList();
        if (changes.Count == 0) { Report(Txt.LayoutNothingToDo, false); return; }
        var batch = new InterruptBatch
        {
            Configs = changes.Select(x => x.Config).ToList(),
            Restart = changes.Where(x => DeviceRestart.Allowed(x.Row.Class)).Select(x => x.Row.Device.InstanceId).ToList(),
        };
        if (await RunBatchAsync(batch) is { } result) ReportBatch(batch, result.Unconfirmed, result.RestartFailed);
    }

    async Task EnableMsiAllAsync()
    {
        var configs = _rows.Where(r => r.SupportsMsi && r.Config.Msi != 1 && !Interrupts.IsStorage(r.Device.Class)).Select(r => { var c = r.Config.Copy(); c.Msi = 1; return c; }).ToList();
        if (configs.Count == 0) { Report(Txt.MsiAllNone, false); return; }
        if (await RunBatchAsync(new InterruptBatch { Configs = configs }) is not { Unconfirmed: var failed }) return;
        foreach (var c in configs.Where(c => !failed.Contains(c.InstanceId))) TweakEngine.Instance.Pending.Mark(Txt.IrqPendingName(NameOf(c.InstanceId)));
        Report(Txt.MsiAllApplied(configs.Count - failed.Count, configs.Count), failed.Count > 0);
        if (failed.Count < configs.Count) Toasts.Show(Txt.MsiAllWarning, ToastKind.Warning);
    }

    async Task RestartDeviceAsync()
    {
        if (_selected is not { } row) return;
        if (!DeviceRestart.Allowed(row.Class)) { Report(Txt.RestartNeedsWindows, false); return; }
        var id = row.Device.InstanceId;
        if (await RunBatchAsync(new InterruptBatch { Restart = { id } }) is not { } result) return;
        if (result.RestartFailed) Fail(Txt.DeviceRestartFailed);
        else Report(_rows.FirstOrDefault(r => r.Device.InstanceId == id)?.Pending == false ? Txt.RestartInEffect : Txt.RestartNotYet, false);
    }

    /// <summary>Runs the batch, then reads everything again. Null when it didn't run (cancelled or failed; the message says so).</summary>
    async Task<(List<string> Unconfirmed, bool RestartFailed)?> RunBatchAsync(InterruptBatch batch)
    {
        _busy = true;
        Report(Txt.Applying, false);
        CommandManager.InvalidateRequerySuggested();
        (List<string>, bool)? failed = null;
        try { failed = await Interrupts.ApplyBatchAsync(batch); }
        catch (ElevationCancelledException ex) { Report(ex.Message, false); }
        catch (Exception ex)
        {
            Log.Error("Interrupt batch failed", ex);
            Fail(ex.Message);
        }
        finally { _busy = false; }
        var (message, error) = (_message, _isError);
        await RefreshAsync(); // results come from a fresh read
        Report(message, error);
        return failed;
    }

    /// <summary>
    /// Per device: in effect now only when it was restarted, every restart worked and the fresh read shows no difference;
    /// everything else (graphics and audio always) after a Windows restart, marked pending.
    /// </summary>
    void ReportBatch(InterruptBatch batch, List<string> failed, bool restartFailed)
    {
        var now = new List<string>();
        var later = new List<string>();
        foreach (var c in batch.Configs.Where(c => !failed.Contains(c.InstanceId)))
        {
            var name = NameOf(c.InstanceId);
            if (!restartFailed && batch.Restart.Contains(c.InstanceId) && _rows.FirstOrDefault(r => r.Device.InstanceId == c.InstanceId) is { Pending: false })
                now.Add(name);
            else
            {
                later.Add(name);
                TweakEngine.Instance.Pending.Mark(Txt.IrqPendingName(name));
            }
        }
        Report(Txt.IrqBatchResult(now, later, failed.Select(NameOf).ToList()) + (restartFailed ? "\n" + Txt.DeviceRestartFailed : ""), failed.Count > 0 || restartFailed);
    }

    string NameOf(string id) => _rows.FirstOrDefault(r => r.Device.InstanceId == id)?.Name ?? id;

    async Task ApplyAsync()
    {
        if (_selected is not { } row) return;
        var target = new InterruptConfig
        {
            InstanceId = row.Device.InstanceId, Msi = ValueOf(_msi), Priority = ValueOf(_priority), Policy = ValueOf(_policy),
        };
        if (_limit.Trim() is { Length: > 0 } l)
        {
            if (!uint.TryParse(l, out var limit) || limit is 0 or > 2048) { Fail(Txt.IrqLimitInvalid); return; }
            target.Limit = limit;
        }
        if (CpusEnabled)
        {
            target.Mask = Cpus.Parse(_cpus, out var error);
            if (error is not null) { Fail(error); return; }
        }
        if (target.SameValues(row.Config)) { Report(Txt.IrqNoChanges, false); return; }
        var risky = target.Msi == 1 && !row.SupportsMsi;
        await RunAsync(() => Interrupts.ApplyAsync(target), row.Name);
        if (risky && !_isError) Toasts.Show($"{row.Name}: {Txt.IrqMsiUnsupported}", ToastKind.Warning);
    }

    async Task RestoreAsync()
    {
        if (_selected is not { } row || Interrupts.Original(row.Device.InstanceId) is not { } original) return;
        await RunAsync(() => Interrupts.ApplyAsync(original), row.Name);
    }

    async Task RunAsync(Func<Task> op, string device)
    {
        _busy = true;
        Report(Txt.Applying, false);
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await op();
            TweakEngine.Instance.Pending.Mark(Txt.IrqPendingName(device));
            Report(Txt.IrqApplied, false);
        }
        catch (ElevationCancelledException ex) { Report(ex.Message, false); }
        catch (Exception ex)
        {
            Log.Error("Interrupt change failed", ex);
            Fail(ex.Message);
        }
        finally { _busy = false; }
        var message = _message;
        var error = _isError;
        await RefreshAsync(); // the state shown always comes from a fresh read
        Report(message, error);
    }

    // ---------- ghost devices ----------
    void Check(bool value)
    {
        foreach (var r in _ghostView.Cast<GhostRowViewModel>()) r.IsChecked = value && !r.Device.HasAffinity;
    }

    async Task RemoveAsync()
    {
        var ids = _ghostView.SourceCollection.Cast<GhostRowViewModel>().Where(r => r.IsChecked).Select(r => r.InstanceId).ToList();
        if (ids.Count == 0) { Report(Txt.GhostNoneChecked, false); return; }
        _busy = true;
        Report(Txt.Removing, false);
        CommandManager.InvalidateRequerySuggested();
        string message;
        var error = false;
        try
        {
            var removed = await GhostDevices.RemoveAsync(ids);
            message = Txt.GhostRemoved(removed, ids.Count);
            error = removed < ids.Count;
        }
        catch (ElevationCancelledException ex) { message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("Removing devices failed", ex);
            message = ex.Message;
            error = true;
        }
        finally { _busy = false; }
        await RefreshAsync();
        Report(message, error);
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
    void Fail(string message) => Report(message, true);
}
