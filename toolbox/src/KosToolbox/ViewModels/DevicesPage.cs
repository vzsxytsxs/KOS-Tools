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

public sealed class IrqRowViewModel(PnpDevice device, InterruptConfig config)
{
    public PnpDevice Device { get; } = device;
    public InterruptConfig Config { get; } = config;
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
                (_ghostsOnly ? new List<IrqRowViewModel>() : Interrupts.PciDevices().Select(d => new IrqRowViewModel(d, Interrupts.Read(d.InstanceId))).OrderBy(r => r.Class).ThenBy(r => r.Name).ToList(),
                 _ghostsOnly ? GhostDevices.List().OrderBy(d => d.Class).ThenBy(d => d.Name).Select(d => new GhostRowViewModel(d)).ToList() : new List<GhostRowViewModel>()));
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
