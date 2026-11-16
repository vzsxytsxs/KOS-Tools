using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.KosState;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>One difference found by the check; checked ones are put back.</summary>
public sealed class KosItemRow(KosStatePageViewModel page, KosItem item) : ObservableObject
{
    bool _checked = item.Section != KosSection.Toolbox && item.NotFixable is null;
    public KosItem Item { get; } = item;
    public string Title => Item.Title;
    public string Path => Item.Path;
    public string Values => Txt.KosValues(Item.Section, Item.Expected, Item.Current);
    public string Area => Txt.KosAreaName(Item.Area);
    public string? Tooltip => Item.NotFixable ?? (Item.Detail.Length == 0 ? null : Item.Detail);
    public bool CanCheck => Item.NotFixable is null;

    public bool IsChecked
    {
        get => _checked;
        set { if (CanCheck && Set(ref _checked, value)) page.Changed(); }
    }
}

/// <summary>Header of a section; its box checks or unchecks every row in it.</summary>
public sealed class KosSectionRow(KosStatePageViewModel page, KosSection section, IReadOnlyList<KosItemRow> rows) : ObservableObject
{
    public KosSection Section { get; } = section;
    public IReadOnlyList<KosItemRow> Rows { get; } = rows;
    public string Title => $"{Txt.KosSectionTitle(Section)} · {Rows.Count}";
    public string Note => Txt.KosSectionNote(Section);

    public bool? IsChecked
    {
        get
        {
            var on = Rows.Count(r => r.IsChecked);
            return on == 0 ? false : on == Rows.Count(r => r.CanCheck) ? true : null;
        }
        set
        {
            foreach (var r in Rows) r.IsChecked = value == true;
            page.Changed();
        }
    }

    internal void Refresh() => Raise(nameof(IsChecked));
}

/// <summary>sfc or DISM on the Repair Windows tab, with its own progress bar and result.</summary>
public sealed class RepairToolViewModel : ObservableObject
{
    bool _running, _indeterminate = true, _isError;
    double _percent;
    string? _status, _result;

    public RepairToolViewModel(string tool, string title, string command, string description, Func<RepairToolViewModel, Task> run, Func<bool> canRun)
    {
        Tool = tool; Title = title; Command = command; Description = description;
        RunCommand = new AsyncCommand(() => run(this), canRun);
    }

    public string Tool { get; }
    public string Title { get; }
    public string Command { get; }
    public string Description { get; }
    public ICommand RunCommand { get; }
    public bool IsRunning { get => _running; private set => Set(ref _running, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    /// <summary>Until the tool prints its first percent.</summary>
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public string? Result { get => _result; private set => Set(ref _result, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    internal void Start()
    {
        IsRunning = Indeterminate = true;
        Percent = 0;
        Status = Txt.RepairStarting;
        Result = null;
        IsError = false;
    }

    internal void Show(RepairProgress p)
    {
        if (p.Percent is { } v) { Percent = v; Indeterminate = false; }
        Status = Txt.RepairRunning(p);
    }

    internal void Finish(string text, bool error)
    {
        IsRunning = false;
        Status = null;
        Result = text;
        IsError = error;
    }
}

/// <summary>
/// KOS state: checks what Windows Update, sfc or DISM put back (KosCheck, read-only, on every visit) and puts the checked items back
/// (catalog settings through the tweak engine, the rest through KosStateFix). After applying it checks again and reports only
/// what reading back confirmed.
/// </summary>
public sealed class KosStatePageViewModel : PageViewModel
{
    readonly IReadOnlyList<TweakViewModel> _tweaks;
    IReadOnlyList<KosSectionRow> _sections = Array.Empty<KosSectionRow>();
    IReadOnlyList<object> _rows = Array.Empty<object>();
    bool _busy, _applying, _suggestCheck;
    double _progress;
    string? _summary = Txt.KosNotChecked, _reference, _unreadable;
    int _checked;

    public KosStatePageViewModel(IEnumerable<TweakViewModel> tweaks) : base("kos-state", Txt.PageKosState, "", Txt.PageKosStateSubtitle)
    {
        _tweaks = tweaks.ToList();
        CheckCommand = new AsyncCommand(() => CheckAsync(saveIfClean: true), () => !_busy);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !_busy && _checked > 0);
        RestartAsAdminCommand = new RelayCommand(SettingsViewModel.RestartAsAdmin);
        Tabs = new[] { new OptionItem("check", Txt.TabKosCheck) { IsSelected = true }, new OptionItem("repair", Txt.TabWindowsRepair) };
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
        // One at a time: they all work on the component store.
        RepairTools = new[]
        {
            new RepairToolViewModel(WindowsRepair.Sfc, Txt.SfcTitle, Txt.SfcCommand, Txt.SfcDescription, RunRepairAsync, () => !RepairRunning),
            new RepairToolViewModel(WindowsRepair.Dism, Txt.DismTitle, Txt.DismCommand, Txt.DismDescription, RunRepairAsync, () => !RepairRunning),
            new RepairToolViewModel(WindowsRepair.StoreAnalyze, Txt.StoreAnalyzeTitle, Txt.StoreAnalyzeCommand, Txt.StoreAnalyzeDescription, RunRepairAsync, () => !RepairRunning),
            new RepairToolViewModel(WindowsRepair.StoreCleanup, Txt.StoreCleanupTitle, Txt.StoreCleanupCommand, Txt.StoreCleanupDescription, RunRepairAsync, () => !RepairRunning),
            new RepairToolViewModel(WindowsRepair.StoreResetBase, Txt.StoreResetBaseTitle, Txt.StoreResetBaseCommand, Txt.StoreResetBaseDescription, RunRepairAsync, () => !RepairRunning),
        };
        CheckAfterRepairCommand = new AsyncCommand(async () => { SelectTab("check"); await CheckAsync(saveIfClean: true); }, () => !_busy);
    }

    public IReadOnlyList<OptionItem> Tabs { get; }
    public ICommand SelectTabCommand { get; }
    public bool ShowCheck => Tabs[0].IsSelected;
    public bool ShowRepair => Tabs[1].IsSelected;
    public IReadOnlyList<RepairToolViewModel> RepairTools { get; }
    bool RepairRunning => RepairTools.Any(t => t.IsRunning);
    /// <summary>After sfc or DISM ran: they can put back what the KOS removed.</summary>
    public bool SuggestCheck { get => _suggestCheck; private set => Set(ref _suggestCheck, value); }
    public ICommand CheckAfterRepairCommand { get; }

    void SelectTab(string key)
    {
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        Raise(nameof(ShowCheck));
        Raise(nameof(ShowRepair));
    }

    async Task RunRepairAsync(RepairToolViewModel tool)
    {
        tool.Start();
        SuggestCheck = false;
        CommandManager.InvalidateRequerySuggested();
        var job = new RepairJob { Tool = tool.Tool, RunId = Guid.NewGuid().ToString() };
        try
        {
            var engine = TweakEngine.Instance;
            // Through the engine's queue: no other system change meanwhile, and closing the window warns.
            var result = await engine.RunExclusiveAsync(async () =>
            {
                if (Elevation.IsAdmin)
                {
                    var progress = new Progress<RepairProgress>(tool.Show);
                    return await Task.Run(() => WindowsRepair.RunAsync(job, progress));
                }
                var run = engine.RunElevated(new ElevatedJob { Repair = job }, CancellationToken.None);
                while (!run.IsCompleted)
                {
                    if (WindowsRepair.ReadProgress(tool.Tool) is { } p) tool.Show(p);
                    await Task.WhenAny(run, Task.Delay(500));
                }
                await run;
                return WindowsRepair.LoadResult(tool.Tool, job.RunId);
            });
            if (result is null) { tool.Finish(Txt.RepairNoResult, true); return; }
            // sfc's exit code says little; its verdict is the text. DISM's is reliable.
            tool.Finish(Txt.RepairFinished(result), result.Error is not null || result.ExitCode == -1 || result.Tool != WindowsRepair.Sfc && result.ExitCode != 0);
            if (WindowsRepair.SourceMissing(result)) Toasts.Show(Txt.DismSourceHint, ToastKind.Warning);
            if (result.Tool == WindowsRepair.StoreResetBase && result.Error is null && result.ExitCode == 0) Toasts.Show(Txt.StoreResetBaseNote, ToastKind.Warning);
            if (result.Error is null && result.Tool is WindowsRepair.Sfc or WindowsRepair.Dism)
            {
                SuggestCheck = true;
                Toasts.Show(Txt.RepairCheckSuggestion, ToastKind.Info);
            }
        }
        catch (ElevationCancelledException ex) { tool.Finish(ex.Message, true); }
        catch (Exception ex)
        {
            Log.Error($"Windows repair: {tool.Tool} failed", ex);
            tool.Finish(ex.Message, true);
        }
        finally { CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Opens this page (Home's notice after a Windows update).</summary>
    public static Action? Open { get; set; }
    public override bool ScrollsItself => true;
    public ICommand CheckCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    /// <summary>Section headers followed by their rows, in one virtualized list.</summary>
    public IReadOnlyList<object> Rows { get => _rows; private set => Set(ref _rows, value); }
    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    /// <summary>Applying has no measurable progress: the bar just runs.</summary>
    public bool Applying { get => _applying; private set => Set(ref _applying, value); }
    public string? Summary { get => _summary; private set => Set(ref _summary, value); }
    public string? Reference { get => _reference; private set => Set(ref _reference, value); }
    public string? Unreadable { get => _unreadable; private set { Set(ref _unreadable, value); Raise(nameof(ShowRestartAsAdmin)); } }
    public bool ShowRestartAsAdmin => _unreadable is not null && !Elevation.IsAdmin;
    public string CheckedText => Txt.KosCheckedCount(_checked);

    public override Task RefreshAsync() => _busy ? Task.CompletedTask : CheckAsync(saveIfClean: true);

    internal void Changed()
    {
        _checked = _sections.Sum(s => s.Rows.Count(r => r.IsChecked));
        foreach (var s in _sections) s.Refresh();
        Raise(nameof(CheckedText));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task<KosReport?> CheckAsync(bool saveIfClean)
    {
        IsBusy = true;
        Progress = 0;
        Summary = Txt.KosChecking;
        try
        {
            var progress = new Progress<double>(p => Progress = p * 100);
            var report = await Task.Run(() => KosCheck.Run(progress));
            Show(report);
            // Home's notice is about this build until Windows changes it again.
            AppSettings.Current.KosCheckedBuild = KosSnapshots.BuildText(report.MachineNow.Windows);
            AppSettings.Current.Save();
            if (saveIfClean && report.Clean)
            {
                await Task.Run(() => KosStateFix.SaveClean(report));
                Summary += " " + Txt.KosSavedLastGood;
            }
            return report;
        }
        catch (Exception ex)
        {
            Log.Error("KOS state: the check failed", ex);
            Summary = Txt.ReadErrorWith(ex.Message);
            return null;
        }
        finally { IsBusy = false; }
    }

    void Show(KosReport report)
    {
        _sections = report.Items.GroupBy(i => i.Section).OrderBy(g => g.Key)
            .Select(g => new KosSectionRow(this, g.Key, g.Select(i => new KosItemRow(this, i)).ToList())).ToList();
        Rows = _sections.SelectMany(s => s.Rows.Cast<object>().Prepend(s)).ToList();
        Summary = Txt.KosSummary(report.Compared, Count(KosSection.Outside), Count(KosSection.Other), Count(KosSection.Toolbox));
        Reference = Txt.KosReferenceText(report.MachineReference, report.UserReference);
        Unreadable = report.Unreadable > 0 ? Txt.KosUnreadable(report.Unreadable, Elevation.IsAdmin) : null;
        Changed();
        int Count(KosSection s) => report.Items.Count(i => i.Section == s);
    }

    async Task ApplyAsync()
    {
        var chosen = _sections.SelectMany(s => s.Rows).Where(r => r.IsChecked).Select(r => r.Item).ToList();
        if (chosen.Count == 0) return;
        IsBusy = Applying = true;
        var warned = new List<string>(); // one warning at the end, as "KOS defaults" does
        string? error = null;
        try
        {
            foreach (var item in chosen.Where(i => i.TweakId is not null))
            {
                if (_tweaks.FirstOrDefault(t => t.Tweak.Id == item.TweakId) is not { } t) continue;
                await t.EnsureLoadedAsync();
                if (t.KosDefault is not { } option) continue;
                if (await t.ApplyAsync(option, warn: false) && t.NeedsWarning(option)) warned.Add(t.Name);
            }
            var raw = chosen.Where(i => i.TweakId is null).Select(i => i.Id).ToList();
            var engine = TweakEngine.Instance;
            await engine.RunExclusiveAsync(async () => { await KosStateFix.ApplyAsync(raw, engine.RunElevated); return true; });
        }
        catch (ElevationCancelledException ex) { error = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("KOS state: applying failed", ex);
            error = ex.Message;
        }
        finally { IsBusy = Applying = false; }

        var after = await CheckAsync(saveIfClean: false);
        var still = after?.Items.Select(i => i.Id).ToHashSet() ?? chosen.Select(i => i.Id).ToHashSet();
        var ok = chosen.Count(i => !still.Contains(i.Id));
        if (error is not null) Toasts.Show(Txt.KosApplyError(error), ToastKind.Error);
        Toasts.Show(Txt.KosApplied(ok, chosen.Count), ok == chosen.Count ? ToastKind.Success : ToastKind.Error);
        if (warned.Count > 0) Toasts.Show(Txt.KosDefaultsWarned(warned), ToastKind.Warning);
        if (chosen.Any(i => i.Area == KosArea.Defender && !still.Contains(i.Id))) Toasts.Show(Txt.KosDefenderNote, ToastKind.Warning);
        if (chosen.Any(i => i.Id.StartsWith("f:", StringComparison.Ordinal) && !still.Contains(i.Id))) Toasts.Show(Txt.KosRestartToFinish, ToastKind.Info);
    }
}
