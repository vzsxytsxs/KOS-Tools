using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Power;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed record PlanItem(Guid Id, string Name, bool IsActive)
{
    public string Display => IsActive ? Txt.PlanActive(Name) : Name;
    public override string ToString() => Display;
}

/// <summary>Values of the same setting in another plan (comparison mode).</summary>
public sealed record PlanValue(string Plan, uint? Ac, uint? Dc);

public sealed record PowerCompareLine(string Text, bool Differs);

/// <summary>A setting of the selected plan, with separate pending values for AC and DC.</summary>
public sealed class PowerSettingViewModel : ObservableObject
{
    readonly PowerPageViewModel _page;
    uint? _pendingAc, _pendingDc;
    string _text = "";
    string? _error;
    IReadOnlyList<PlanValue>? _others;

    public PowerSettingViewModel(PowerPageViewModel page, PowerSettingInfo info)
    {
        _page = page;
        Info = info;
        _text = Value?.ToString(CultureInfo.InvariantCulture) ?? "";
        CustomValueCommand = new RelayCommand(() => _page.CustomValue(this), () => !_page.IsBusy && IsChoice);
        DiscardCommand = new RelayCommand(() => { Discard(); _page.UpdatePending(); }, () => !_page.IsBusy && (IsModified || HasError));
        RemoveValueCommand = new AsyncCommand(() => _page.RemoveValueAsync(this), () => !_page.IsBusy && IsStoredHere && !IsModifiedHere);
        ToggleHiddenCommand = new AsyncCommand(() => _page.ToggleHiddenAsync(this), () => !_page.IsBusy);
    }

    public PowerSettingInfo Info { get; private set; }
    public ICommand CustomValueCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RemoveValueCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public bool IsHidden => Info.Hidden;
    public string HiddenMenuText => Info.Hidden ? Txt.PowerShowInWindows : Txt.PowerHideInWindows;
    /// <summary>The plan stores its own value in the displayed mode (otherwise the plan's own default applies).</summary>
    public bool IsStoredHere => _page.IsAc ? Info.StoredAc : Info.StoredDc;
    public bool IsDefaultHere => !IsStoredHere && Current is not null;

    /// <summary>Re-read of a setting after an operation, keeping the pending edits.</summary>
    public void Update(PowerSettingInfo info)
    {
        Info = info;
        Refresh();
        Raise(string.Empty);
    }

    /// <summary>Values in the other plans; null when comparison is off.</summary>
    public IReadOnlyList<PlanValue>? Others
    {
        get => _others;
        set { _others = value; RaiseComparison(); }
    }

    /// <summary>Compares the saved value (not the pending one), so the filter does not hide the row while editing.</summary>
    public bool Differs => _others?.Any(o => (_page.IsAc ? o.Ac : o.Dc) != Current) == true;
    public IReadOnlyList<PowerCompareLine> Comparison => _others?.Select(o =>
    {
        var v = _page.IsAc ? o.Ac : o.Dc;
        return new PowerCompareLine(Txt.PowerCompareLine(o.Plan, Format(v)), v != Current);
    }).ToList() ?? (IReadOnlyList<PowerCompareLine>)Array.Empty<PowerCompareLine>();

    void RaiseComparison()
    {
        Raise(nameof(Others));
        Raise(nameof(Differs));
        Raise(nameof(Comparison));
    }
    public string Name => Info.Name;
    public string Description => Info.Description;
    public bool HasDescription => Info.Description.Length > 0;
    public bool IsChoice => Info.Kind == PowerValueKind.Choice && Current is not null;
    public bool IsRange => Info.Kind == PowerValueKind.Range && Current is not null;
    public bool IsReadOnly => !IsChoice && !IsRange;
    public IReadOnlyList<PowerChoice> Choices => Info.Choices;
    public string RangeHint => Txt.PowerRangeHint(Info.Min, Info.Max, Info.Increment, Info.Units);
    public string Units => Info.Units;
    public string Technical => $"{Info.Subgroup:D} / {Info.Setting:D}";

    uint? Current => _page.IsAc ? Info.Ac : Info.Dc;
    uint? Pending => _page.IsAc ? _pendingAc : _pendingDc;
    uint? Value => Pending ?? Current;

    public bool IsModified => _pendingAc is not null || _pendingDc is not null;
    /// <summary>Changed in the displayed mode (AC or DC).</summary>
    public bool IsModifiedHere => Pending is not null;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    public PowerChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set { if (value is not null) SetPending(value.Value); }
    }

    /// <summary>Current value that is not among the allowed ones (the list selects no item).</summary>
    public string ChoicePlaceholder => Value is { } v ? Txt.PowerValueNotListed(v) : Txt.ReadError;

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            if (!PowerPlans.TryParseValue(_text, out var v)) Error = Txt.PowerEnterNumber;
            else if (PowerPlans.Validate(Info, v) is { } problem) Error = problem;
            else { Error = null; SetPending(v); }
            _page.UpdatePending();
        }
    }

    /// <summary>Read-only value: option label, number with unit, or the read error.</summary>
    public string ReadOnlyText => Current is null ? Txt.ReadErrorWith(Info.ReadError ?? Txt.Unknown) : Format(Current);
    public string? ReadOnlyReason => Current is null ? null : Info.ReadOnlyReason ?? (Info.Kind == PowerValueKind.ReadOnly ? Txt.PowerNoMetadata : null);

    /// <summary>Option label, or number with unit; "—" when the read failed.</summary>
    string Format(uint? value) => value is not { } v ? "—"
        : Choices.FirstOrDefault(c => c.Value == v)?.Label ?? (Info.Units.Length > 0 ? $"{v} {Info.Units}" : v.ToString(CultureInfo.InvariantCulture));

    public uint? ShownValue => Value;

    /// <summary>Value typed in the ⋯ menu (may fall outside the list).</summary>
    public void SetCustom(uint v) => SetPending(v);

    void SetPending(uint v)
    {
        var pending = v == Current ? (uint?)null : v;
        if (_page.IsAc) _pendingAc = pending; else _pendingDc = pending;
        Raise(nameof(SelectedChoice));
        Raise(nameof(ChoicePlaceholder));
        Raise(nameof(IsModifiedHere));
        Raise(nameof(IsModified));
        _page.UpdatePending();
    }

    /// <summary>Switches between AC and DC: the text and the list show the other mode's value.</summary>
    public void Refresh()
    {
        _text = Value?.ToString(CultureInfo.InvariantCulture) ?? "";
        _error = null;
        foreach (var p in new[] { nameof(Text), nameof(Error), nameof(HasError), nameof(SelectedChoice), nameof(ChoicePlaceholder),
                     nameof(IsModifiedHere), nameof(IsModified), nameof(ReadOnlyText), nameof(IsChoice), nameof(IsRange), nameof(IsReadOnly),
                     nameof(IsStoredHere), nameof(IsDefaultHere) })
            Raise(p);
        RaiseComparison();
    }

    public void Discard()
    {
        _pendingAc = _pendingDc = null;
        Refresh();
    }

    public IEnumerable<PowerWrite> Writes(Guid scheme)
    {
        if (_pendingAc is { } ac) yield return new PowerWrite { Scheme = scheme, Subgroup = Info.Subgroup, Setting = Info.Setting, Ac = true, Value = ac, Custom = IsCustom(ac) };
        if (_pendingDc is { } dc) yield return new PowerWrite { Scheme = scheme, Subgroup = Info.Subgroup, Setting = Info.Setting, Ac = false, Value = dc, Custom = IsCustom(dc) };
    }

    bool IsCustom(uint v) => Info.Kind == PowerValueKind.Choice && Choices.All(c => c.Value != v);

    public bool Matches(string q) => MainViewModel.Matches(Name, q) || MainViewModel.Matches(Description, q) || Technical.Contains(q, StringComparison.OrdinalIgnoreCase);
}

public sealed class PowerGroupViewModel(string name, bool hidden, IReadOnlyList<PowerSettingViewModel> settings) : ObservableObject
{
    bool _expanded;
    IReadOnlyList<PowerSettingViewModel> _visible = settings;

    public string Name { get; } = name;
    public bool Hidden { get; } = hidden;
    public IReadOnlyList<PowerSettingViewModel> Settings { get; } = settings;
    public IReadOnlyList<PowerSettingViewModel> Visible { get => _visible; set => Set(ref _visible, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>
/// Power page: KOS profiles (catalog) and the editor of the selected plan. The edited plan is
/// separate from the active plan. Changes stay pending until Apply and are confirmed by re-reading the plan.
/// </summary>
public sealed class PowerPageViewModel : PageViewModel
{
    PlanItem? _plan;
    bool _ac = true, _advanced, _busy, _isError, _compare, _onlyDifferent;
    string _search = "", _planDescription = "";
    string? _message, _loadError;
    int _pendingCount;
    IReadOnlyList<PowerGroupViewModel> _groups = Array.Empty<PowerGroupViewModel>();
    IReadOnlyList<PowerGroupViewModel> _visibleGroups = Array.Empty<PowerGroupViewModel>();

    public PowerPageViewModel(IEnumerable<ICatalogItem> profiles) : base("power", Txt.PagePower, "", Txt.PagePowerSubtitle)
    {
        Profiles = profiles.GroupBy(i => i.Group ?? "").Select(g => new GroupViewModel(g.Key, g.ToList())).ToList();
        Modes = new[] { new OptionItem("ac", Txt.PowerAc) { IsSelected = true }, new OptionItem("dc", Txt.PowerDc) };
        SelectModeCommand = new RelayCommand(p => IsAc = (string)p! == "ac");
        ActivateCommand = new AsyncCommand(ActivateAsync, () => !_busy && _plan is { IsActive: false });
        ApplyCommand = new AsyncCommand(() => ApplyAsync(), () => !_busy && _pendingCount > 0 && !AllSettings.Any(s => s.HasError));
        DiscardCommand = new RelayCommand(Discard, () => !_busy && _pendingCount > 0);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        SaveCopyCommand = new AsyncCommand(SaveCopyAsync, () => !_busy && _plan is not null);
        RenameCommand = new AsyncCommand(RenameAsync, () => !_busy && _plan is not null);
        DeletePlanCommand = new AsyncCommand(DeleteAsync, () => !_busy && _plan is not null);
        ImportCommand = new AsyncCommand(ImportAsync, () => !_busy);
        ExportCommand = new AsyncCommand(ExportAsync, () => !_busy && _plan is not null);
        RestoreDefaultsCommand = new AsyncCommand(RestoreDefaultsAsync, () => !_busy);
    }

    public override bool ScrollsItself => true;
    public IReadOnlyList<GroupViewModel> Profiles { get; }
    public IReadOnlyList<OptionItem> Modes { get; }
    public ObservableCollection<PlanItem> Plans { get; } = new();
    public ICommand SelectModeCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SaveCopyCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeletePlanCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }

    IEnumerable<PowerSettingViewModel> AllSettings => _groups.SelectMany(g => g.Settings);
    public IReadOnlyList<PowerGroupViewModel> Groups { get => _visibleGroups; private set { Set(ref _visibleGroups, value); Raise(nameof(NoMatches)); } }
    public bool NoMatches => _groups.Count > 0 && _visibleGroups.Count == 0;
    public string? LoadError { get => _loadError; private set { Set(ref _loadError, value); Raise(nameof(HasLoadError)); } }
    public bool HasLoadError => _loadError is not null;
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public int PendingCount { get => _pendingCount; private set { if (Set(ref _pendingCount, value)) { Raise(nameof(HasPending)); Raise(nameof(PendingText)); } } }
    public bool HasPending => _pendingCount > 0;
    public string PendingText => Txt.PowerPending(_pendingCount);
    public string PlanStatus => _plan is null ? "" : _plan.IsActive ? Txt.PlanIsActive : Txt.PlanNotActive;
    public string PlanDescription { get => _planDescription; private set => Set(ref _planDescription, value); }
    string ModeName => _ac ? Txt.PowerAc : Txt.PowerDc;

    public bool IsAc
    {
        get => _ac;
        set
        {
            if (!Set(ref _ac, value)) return;
            foreach (var m in Modes) m.IsSelected = (m.Key == "ac") == value;
            foreach (var s in AllSettings) s.Refresh();
            if (_compare && _onlyDifferent) Refilter();
        }
    }

    public bool ShowAdvanced { get => _advanced; set { if (Set(ref _advanced, value)) Refilter(); } }

    /// <summary>Shows, on each row, the value of the same setting in the other plans (read-only).</summary>
    public bool Compare
    {
        get => _compare;
        set { if (Set(ref _compare, value)) MainViewModel.Fire(LoadCompareAsync()); }
    }

    public bool OnlyDifferent { get => _onlyDifferent; set { if (Set(ref _onlyDifferent, value)) Refilter(); } }
    public string SearchText { get => _search; set { if (Set(ref _search, value ?? "")) Refilter(); } }

    public PlanItem? SelectedPlan
    {
        get => _plan;
        set
        {
            if (value is null || value.Id == _plan?.Id) return;
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            // During a write the switch is refused, so the same edits are not applied twice.
            if (_busy) { dispatcher.BeginInvoke(() => Raise(nameof(SelectedPlan))); return; }
            if (_pendingCount > 0)
            {
                // As on the other pages: switching discards unapplied edits and warns.
                Toasts.Show(Txt.PendingDiscarded(_pendingCount), ToastKind.Info);
                Discard();
            }
            Select(value);
            MainViewModel.Fire(LoadPlanAsync());
        }
    }

    void Select(PlanItem plan)
    {
        _plan = plan;
        Raise(nameof(SelectedPlan));
        Raise(nameof(PlanStatus));
        CommandManager.InvalidateRequerySuggested();
    }

    public override async Task RefreshAsync()
    {
        foreach (var item in Profiles.SelectMany(g => g.Items)) await item.RefreshAsync();
        await LoadPlansAsync();
        // Pending edits are not discarded when returning to the page.
        if (_pendingCount == 0) await LoadPlanAsync();
    }

    async Task LoadPlansAsync()
    {
        try
        {
            var plans = await Task.Run(PowerPlans.List);
            var keep = _plan?.Id;
            Plans.Clear();
            foreach (var p in plans) Plans.Add(new PlanItem(p.Id, p.Name, p.IsActive));
            var kept = Plans.FirstOrDefault(p => p.Id == keep);
            // The edited plan disappeared (removed or changed outside the toolbox): the edits must not go to another plan.
            if (kept is null && _pendingCount > 0) Discard();
            var selected = kept ?? Plans.FirstOrDefault(p => p.IsActive) ?? Plans.FirstOrDefault();
            if (selected is not null) Select(selected);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the power plans failed", ex);
            LoadError = Txt.PowerPlansFailed(ex.Message);
        }
    }

    async Task LoadPlanAsync()
    {
        if (_plan is not { } plan) return;
        try
        {
            var subgroups = await Task.Run(() => PowerPlans.Read(plan.Id));
            var description = await Task.Run(() => PowerPlans.Description(plan.Id));
            if (_plan?.Id != plan.Id) return;
            PlanDescription = description;
            var expanded = _groups.Where(g => g.IsExpanded).Select(g => g.Name).ToHashSet();
            _groups = subgroups.Select(sg => new PowerGroupViewModel(sg.Name, sg.Hidden,
                sg.Settings.Select(s => new PowerSettingViewModel(this, s)).ToList()) { IsExpanded = expanded.Contains(sg.Name) }).ToList();
            Refilter();
            UpdatePending();
            LoadError = null;
            if (_compare) await LoadCompareAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Reading power plan {plan.Id} failed", ex);
            LoadError = Txt.PowerPlanReadFailed(plan.Name, ex.Message);
        }
    }

    void Refilter()
    {
        var q = _search.Trim();
        var list = new List<PowerGroupViewModel>();
        foreach (var g in _groups)
        {
            if (g.Hidden && !_advanced) continue;
            var visible = g.Settings.Where(s => (_advanced || !s.Info.Hidden) && (q.Length == 0 || s.Matches(q))
                && (!_compare || !_onlyDifferent || s.Differs)).ToList();
            g.Visible = visible;
            if (visible.Count == 0) continue;
            if (q.Length > 0) g.IsExpanded = true;
            list.Add(g);
        }
        Groups = list;
    }

    internal void UpdatePending()
    {
        PendingCount = AllSettings.Count(s => s.IsModified);
        CommandManager.InvalidateRequerySuggested();
    }

    void Discard()
    {
        foreach (var s in AllSettings) s.Discard();
        UpdatePending();
        Message = null;
    }

    async Task ActivateAsync()
    {
        var plan = _plan!;
        IsBusy = true;
        IsError = false;
        try
        {
            await Task.Run(() => PowerPlans.Activate(plan.Id));
            var active = await Task.Run(PowerPlans.ActiveScheme);
            Log.Info($"Power plan activated: {plan.Name} ({plan.Id})");
            IsError = active != plan.Id;
            Message = IsError ? Txt.PlanNotConfirmed : Txt.PlanActivated(plan.Name);
        }
        catch (Exception ex)
        {
            Log.Error("Activating the power plan failed", ex);
            Message = ex.Message;
            IsError = true;
        }
        finally { IsBusy = false; }
        await LoadPlansAsync();
    }

    /// <summary>Writes the edits, re-reads the plan and compares each written value with the one read.</summary>
    async Task<bool> ApplyAsync()
    {
        if (_busy || _plan is not { } plan) return false;
        var writes = AllSettings.SelectMany(s => s.Writes(plan.Id)).ToList();
        if (writes.Count == 0) return true;
        IsBusy = true;
        IsError = false;
        Message = Txt.Applying;
        try
        {
            await PowerPlans.WriteAsync(writes);
            var failed = await Task.Run(() => writes.Where(w =>
            {
                try { return PowerPlans.ReadValue(w.Scheme, w.Subgroup, w.Setting, w.Ac) != w.Value; }
                catch { return true; }
            }).ToList());
            IsError = failed.Count > 0;
            Message = IsError ? Txt.PowerNotConfirmed(failed.Count, writes.Count) : Txt.PowerSaved(writes.Count);
            await LoadPlanAsync(); // the screen shows what Windows has now, not what was requested
            return !IsError;
        }
        catch (ElevationCancelledException ex)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Saving power settings failed", ex);
            Message = ex.Message;
            IsError = true;
            return false;
        }
        finally { IsBusy = false; }
    }

    // ---------- Comparison ----------

    async Task LoadCompareAsync()
    {
        var rows = AllSettings.ToList();
        if (!_compare || _plan is not { } plan)
        {
            foreach (var s in rows) s.Others = null;
            Refilter();
            return;
        }
        var others = Plans.Where(p => p.Id != plan.Id).ToList();
        var keys = rows.Select(s => (s.Info.Subgroup, s.Info.Setting)).ToList();
        var values = await Task.Run(() => keys.Select(k => (IReadOnlyList<PlanValue>)others.Select(p => new PlanValue(p.Name,
            PowerPlans.TryReadValue(p.Id, k.Subgroup, k.Setting, true), PowerPlans.TryReadValue(p.Id, k.Subgroup, k.Setting, false))).ToList()).ToList());
        if (!_compare || _plan?.Id != plan.Id) return;
        for (var i = 0; i < rows.Count; i++) rows[i].Others = values[i];
        Refilter();
    }

    // ---------- Manage plans ----------

    /// <summary>Plan operations reload the page: pending edits must be applied or discarded first.</summary>
    bool BlockedByPending()
    {
        if (_pendingCount == 0) return false;
        Toasts.Show(Txt.PowerPendingFirst, ToastKind.Warning);
        return true;
    }

    /// <summary>Runs the operation (UAC if needed), re-reads Windows to confirm and shows the result.</summary>
    async Task<bool> RunOpAsync(PowerPlanOp op, string done)
    {
        IsBusy = true;
        IsError = false;
        Message = Txt.Applying;
        var started = DateTime.UtcNow;
        try
        {
            await PowerPlans.RunAsync(op);
            var ok = await Task.Run(() => PowerPlans.Verify(op, started));
            IsError = !ok;
            Message = ok ? done : Txt.PowerOpNotConfirmed;
            return ok;
        }
        catch (ElevationCancelledException ex)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Power plan operation {op.Op} failed", ex);
            Message = ex.Message;
            IsError = true;
            return false;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Re-reads the plan list (even after a partial failure) and opens the requested plan, if it exists.</summary>
    async Task ReloadAsync(Guid? select = null)
    {
        await LoadPlansAsync();
        if (select is { } id && Plans.FirstOrDefault(p => p.Id == id) is { } item) Select(item);
        await LoadPlanAsync();
    }

    async Task SaveCopyAsync()
    {
        if (BlockedByPending() || _plan is not { } plan) return;
        var (ok, name, _) = Dialog.Prompt(Txt.PlanSaveCopy, Txt.SaveCopyPrompt, Txt.CopyName(plan.Name), PowerPlans.NameProblem);
        if (!ok || PowerPlans.NameProblem(name) is not null) return;
        var id = Guid.NewGuid();
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.Duplicate, Scheme = plan.Id, NewScheme = id, Name = name }, Txt.PlanCopied(name));
        await ReloadAsync(id);
    }

    async Task RenameAsync()
    {
        if (BlockedByPending() || _plan is not { } plan) return;
        var (ok, name, _) = Dialog.Prompt(Txt.RenamePlanTitle, Txt.RenamePlanPrompt, plan.Name, PowerPlans.NameProblem);
        if (!ok || PowerPlans.NameProblem(name) is not null) return;
        var (okDescription, description, _) = Dialog.Prompt(Txt.PlanDescriptionTitle, Txt.PlanDescriptionPrompt, _planDescription, PowerPlans.DescriptionProblem);
        if (!okDescription || PowerPlans.DescriptionProblem(description) is not null) return;
        // Only what changed is rewritten: in Windows plans the original text is translated (MUI).
        var newName = name == plan.Name ? null : name;
        var newDescription = description == _planDescription ? null : description;
        if (newName is null && newDescription is null) return;
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.Rename, Scheme = plan.Id, Name = newName, Description = newDescription },
            newName is null ? Txt.PlanDescriptionChanged(plan.Name) : Txt.PlanRenamed(newName));
        await ReloadAsync();
    }

    async Task DeleteAsync()
    {
        if (BlockedByPending() || _plan is not { } plan) return;
        if (plan.IsActive) { Toasts.Show(Txt.PowerDeleteActive, ToastKind.Warning); return; }
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.Delete, Scheme = plan.Id }, Txt.PlanDeleted(plan.Name));
        await ReloadAsync();
    }

    async Task ImportAsync()
    {
        if (BlockedByPending()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Txt.ImportPlanTitle, Filter = Txt.PowFilter, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        if (PowerPlans.PowFileProblem(dialog.FileName, mustExist: true) is { } problem) { IsError = true; Message = problem; return; }
        var id = Guid.NewGuid();
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.Import, NewScheme = id, Path = dialog.FileName }, Txt.PlanImported(Path.GetFileName(dialog.FileName)));
        await ReloadAsync(id);
    }

    async Task ExportAsync()
    {
        if (BlockedByPending() || _plan is not { } plan) return;
        var invalid = Path.GetInvalidFileNameChars();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Txt.ExportPlanTitle,
            Filter = Txt.PowFilter,
            FileName = new string(plan.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".pow",
        };
        if (dialog.ShowDialog() != true) return;
        if (PowerPlans.PowFileProblem(dialog.FileName, mustExist: false) is { } problem) { IsError = true; Message = problem; return; }
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.Export, Scheme = plan.Id, Path = dialog.FileName }, Txt.PlanExported(dialog.FileName));
    }

    async Task RestoreDefaultsAsync()
    {
        if (BlockedByPending()) return;
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.RestoreDefaults }, Txt.PlansRestored);
        await ReloadAsync();
    }

    // ---------- ⋯ menu of a setting ----------

    internal void CustomValue(PowerSettingViewModel row)
    {
        var (ok, text, _) = Dialog.Prompt(Txt.PowerCustomTitle, Txt.PowerCustomPrompt(row.Name, ModeName),
            row.ShownValue?.ToString(CultureInfo.InvariantCulture) ?? "", t => PowerPlans.TryParseValue(t, out _) ? null : Txt.PowerEnterNumber);
        if (ok && PowerPlans.TryParseValue(text, out var value)) row.SetCustom(value);
    }

    internal async Task RemoveValueAsync(PowerSettingViewModel row)
    {
        if (_plan is not { } plan) return;
        var info = row.Info;
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.RemoveValue, Scheme = plan.Id, Subgroup = info.Subgroup, Setting = info.Setting, Ac = _ac },
            Txt.PowerValueRemoved(row.Name));
        await RereadAsync(plan, row);
    }

    internal async Task ToggleHiddenAsync(PowerSettingViewModel row)
    {
        var hide = !row.Info.Hidden;
        // The setting inherits ATTRIBUTE_HIDE from the subgroup: showing only it would have no effect.
        if (!hide && _groups.FirstOrDefault(g => g.Settings.Contains(row)) is { Hidden: true } group)
        {
            Toasts.Show(Txt.PowerSubgroupIsHidden(row.Name, group.Name), ToastKind.Warning);
            return;
        }
        if (_plan is not { } plan) return;
        var info = row.Info;
        await RunOpAsync(new PowerPlanOp { Op = PowerPlanOp.SetHidden, Subgroup = info.Subgroup, Setting = info.Setting, Hidden = hide },
            Txt.PowerAttributeChanged(row.Name, hide));
        await RereadAsync(plan, row);
        Refilter();
    }

    /// <summary>Re-reads only the changed setting, without losing the pending edits of the others.</summary>
    async Task RereadAsync(PlanItem plan, PowerSettingViewModel row)
    {
        try
        {
            var info = row.Info;
            var fresh = await Task.Run(() => PowerPlans.ReadSetting(plan.Id, info.Subgroup, info.Setting));
            if (_plan?.Id == plan.Id) row.Update(fresh);
        }
        catch (Exception ex)
        {
            Log.Error($"Reading power setting {row.Info.Setting} failed", ex);
            IsError = true;
            Message = ex.Message;
        }
    }
}
