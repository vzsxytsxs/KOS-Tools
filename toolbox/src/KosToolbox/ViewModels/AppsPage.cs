using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class ProgramRowViewModel(InstalledProgram program, Action? checkedChanged = null) : ObservableObject
{
    bool _isChecked;
    public InstalledProgram Program { get; } = program;
    /// <summary>Picked for "Uninstall checked".</summary>
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) checkedChanged?.Invoke(); } }

    public string Name => Program.Name;
    public string Publisher => Program.Publisher ?? "";
    public string Version => Program.Version ?? "";
    public string Size => Program.SizeKb is { } kb ? Txt.Size(kb * 1024) : "";
    public string Installed => Program.InstallDate?.ToString("d") ?? "";
    public string SearchText => $"{Name} {Publisher} {Version}";
}

public sealed class StoreAppRowViewModel(StoreApp app)
{
    public StoreApp App { get; } = app;
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Version => App.Version;
    public string Kind => App.Kind switch { StoreAppKind.System => Txt.KindSystem, StoreAppKind.Framework => Txt.KindFrameworks, _ => Txt.KindApps };
    public string SearchText => $"{Name} {Publisher} {App.FamilyName}";
}

public sealed class FeatureRowViewModel(OptionalFeature feature)
{
    public OptionalFeature Feature { get; } = feature;
    public string Name => Feature.Name;
    public string State => Feature.State;
    public string SearchText => $"{Name} {State}";
}

/// <summary>
/// Apps: installed programs (removed by the program's own uninstaller)
/// and current-user Store packages (removed through the PackageManager). Each action is verified by re-reading the list.
/// </summary>
public sealed class AppsPageViewModel : PageViewModel
{
    const string ProgramsTab = "programs", StoreTab = "store", FeaturesTab = "features";
    List<ProgramRowViewModel> _allPrograms = new();
    List<StoreAppRowViewModel> _allApps = new();
    List<FeatureRowViewModel> _allFeatures = new();
    IReadOnlyList<FeatureRowViewModel> _features = Array.Empty<FeatureRowViewModel>();
    FeatureRowViewModel? _selectedFeature;
    OptionalFeatureInfo? _featureInfo;
    bool _featuresLoaded;
    IReadOnlyList<ProgramRowViewModel> _programs = Array.Empty<ProgramRowViewModel>();
    IReadOnlyList<StoreAppRowViewModel> _apps = Array.Empty<StoreAppRowViewModel>();
    ProgramRowViewModel? _selectedProgram;
    StoreAppRowViewModel? _selectedApp;
    LeftoversViewModel? _leftovers;
    OptionItem _kind;
    string _search = "";
    string? _message, _programsError, _storeError, _featuresError;
    bool _isError, _busy, _loading;

    public AppsPageViewModel() : base("apps", Txt.PageApps, "", Txt.PageAppsSubtitle)
    {
        Tabs = new[] { new OptionItem(ProgramsTab, Txt.TabPrograms) { IsSelected = true }, new OptionItem(StoreTab, Txt.TabStoreApps), new OptionItem(FeaturesTab, Txt.TabWindowsFeatures) };
        SelectTabCommand = new RelayCommand(p =>
        {
            foreach (var t in Tabs) t.IsSelected = t.Key == (string)p!;
            Raise(nameof(ShowPrograms));
            Raise(nameof(ShowStore));
            Raise(nameof(ShowFeatures));
            Raise(nameof(CountText));
            if (ShowFeatures && !_featuresLoaded && Elevation.IsAdmin) MainViewModel.Fire(LoadFeaturesAsync());
        });
        Kinds = new[] { new OptionItem(nameof(StoreAppKind.App), Txt.KindApps), new OptionItem(nameof(StoreAppKind.System), Txt.KindSystem),
            new OptionItem(nameof(StoreAppKind.Framework), Txt.KindFrameworks), new OptionItem("", Txt.KindAll) };
        _kind = Kinds[0];
        UninstallCommand = new AsyncCommand(UninstallAsync, () => !_busy && _selectedProgram is not null);
        UninstallCheckedCommand = new AsyncCommand(UninstallCheckedAsync, () => !_busy && CheckedCount > 0);
        ForceRemoveCommand = new AsyncCommand(ForceRemoveAsync, () => !_busy && _selectedProgram is not null);
        OpenLocationCommand = new RelayCommand(OpenLocation, () => _selectedProgram?.Program.InstallLocation is { Length: > 0 } l && Directory.Exists(l));
        RemoveAppCommand = new AsyncCommand(RemoveAppAsync, () => !_busy && _selectedApp is not null);
        SearchStoreCommand = new RelayCommand(() => Shell("ms-windows-store://search/?query=" + Uri.EscapeDataString(_search.Trim())));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        TurnOnCommand = new AsyncCommand(() => SetFeatureAsync(true), () => !_busy && _selectedFeature is { Feature.IsEnabled: false });
        TurnOffCommand = new AsyncCommand(() => SetFeatureAsync(false), () => !_busy && _selectedFeature is { Feature.IsEnabled: true });
    }

    public override bool ScrollsItself => true;
    public IReadOnlyList<OptionItem> Tabs { get; }
    public bool ShowPrograms => Tabs[0].IsSelected;
    public bool ShowStore => Tabs[1].IsSelected;
    public bool ShowFeatures => Tabs[2].IsSelected;
    public bool FeaturesNeedAdmin => !Elevation.IsAdmin;
    public ICommand TurnOnCommand { get; }
    public ICommand TurnOffCommand { get; }
    public IReadOnlyList<FeatureRowViewModel> Features { get => _features; private set { Set(ref _features, value); Raise(nameof(CountText)); } }
    public string? FeaturesError { get => _featuresError; private set => Set(ref _featuresError, value); }
    public string? FeatureDetail => _selectedFeature is null ? null : _featureInfo is { } i
        ? Txt.FeatureDetail(i.DisplayName, i.Description, i.Restart) : _selectedFeature.Name;
    public FeatureRowViewModel? SelectedFeature
    {
        get => _selectedFeature;
        set { if (!Set(ref _selectedFeature, value)) return; _featureInfo = null; Raise(nameof(FeatureDetail)); if (value is not null) MainViewModel.Fire(LoadInfoAsync(value)); }
    }
    public IReadOnlyList<OptionItem> Kinds { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand UninstallCheckedCommand { get; }
    public int CheckedCount => _allPrograms.Count(p => p.IsChecked);
    public string UninstallCheckedText => $"Uninstall checked ({CheckedCount})";
    public bool HasChecked => CheckedCount > 0;

    void CheckedChanged()
    {
        Raise(nameof(CheckedCount));
        Raise(nameof(UninstallCheckedText));
        Raise(nameof(HasChecked));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// One after another: silently when the uninstaller allows it (quiet command, MSI, Inno Setup, NSIS), else its own wizard; each
    /// confirmed by re-reading the list. Then one leftovers search for all the programs that were removed.
    /// </summary>
    async Task UninstallCheckedAsync()
    {
        var targets = _allPrograms.Where(r => r.IsChecked).Select(r => r.Program).ToList();
        if (targets.Count == 0) return;
        await RunAsync(Txt.WaitingForUninstaller, async () =>
        {
            var traces = new List<ProgramTrace>();
            var problems = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                var silent = await Task.Run(() => InstalledPrograms.Silent(p)) is not null;
                Report($"{p.Name} ({i + 1} of {targets.Count}): {(silent ? "uninstalling silently…" : "finish its uninstaller…")}", false);
                var trace = await Task.Run(() => Core.Apps.Leftovers.Trace(p));
                try { await InstalledPrograms.UninstallAsync(p, silent: true); }
                catch (Exception ex) { problems.Add($"{p.Name}: {ex.Message}"); continue; }
                if (await Task.Run(() => InstalledPrograms.StillInstalled(p))) problems.Add($"{p.Name} is still listed.");
                else traces.Add(trace);
            }
            await RefreshAsync();
            var found = 0;
            if (traces.Count > 0)
            {
                Report(Txt.ScanningLeftovers, false);
                found = await ShowLeftoversAsync(traces, force: false);
            }
            var done = $"{traces.Count} of {targets.Count} removed" + (found > 0 ? $", {found} leftover(s) found: check what to delete." : ".");
            return (problems.Count == 0 ? done : $"{done} {string.Join(" ", problems)}", problems.Count > 0);
        });
    }
    public ICommand ForceRemoveCommand { get; }
    public ICommand OpenLocationCommand { get; }
    /// <summary>Leftovers to review (covers the list while open); null = closed.</summary>
    public LeftoversViewModel? Leftovers { get => _leftovers; private set => Set(ref _leftovers, value); }
    public ICommand RemoveAppCommand { get; }
    public ICommand SearchStoreCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public IReadOnlyList<ProgramRowViewModel> Programs { get => _programs; private set { Set(ref _programs, value); Raise(nameof(CountText)); } }
    public IReadOnlyList<StoreAppRowViewModel> Apps { get => _apps; private set { Set(ref _apps, value); Raise(nameof(CountText)); } }
    public ProgramRowViewModel? SelectedProgram { get => _selectedProgram; set => Set(ref _selectedProgram, value); }
    public StoreAppRowViewModel? SelectedApp { get => _selectedApp; set => Set(ref _selectedApp, value); }
    /// <summary>Remove for every user and deprovision (administrator); also allows system apps.</summary>
    public bool RemoveForAllUsers { get; set; }
    public OptionItem SelectedKind { get => _kind; set { if (value is not null && Set(ref _kind, value)) Filter(); } }
    public string CountText => ShowPrograms ? Txt.ProgramsCount(_programs.Count, _allPrograms.Count)
        : ShowStore ? Txt.PackagesCount(_apps.Count, _allApps.Count) : Txt.FeaturesCount(_features.Count, _allFeatures.Count);
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Filter(); }
    }

    public string? ProgramsError { get => _programsError; private set => Set(ref _programsError, value); }
    public string? StoreError { get => _storeError; private set => Set(ref _storeError, value); }

    // The two lists are independent: a failure in one (e.g. AppXSvc disabled in KOS) does not hide the other.
    public override async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        var programs = Task.Run(InstalledPrograms.List);
        var apps = Task.Run(StoreApps.List);
        try
        {
            _allPrograms = (await programs).Select(p => new ProgramRowViewModel(p, CheckedChanged)).ToList();
            CheckedChanged();
            ProgramsError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading installed programs failed", ex);
            ProgramsError = Txt.AppsReadFailed(ex.Message);
        }
        try
        {
            _allApps = (await apps).Select(a => new StoreAppRowViewModel(a)).ToList();
            StoreError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading Store apps failed", ex);
            _allApps = new();
            StoreError = ex.HResult == ServiceDisabled ? Txt.StoreServiceDisabled : Txt.AppsReadFailed(ex.Message);
        }
        Filter();
        IsLoading = false;
        if (_featuresLoaded) await LoadFeaturesAsync();
    }

    // ---------- optional Windows features (dism.exe) ----------
    async Task LoadFeaturesAsync()
    {
        var keep = _selectedFeature?.Name;
        IsLoading = true;
        try
        {
            _allFeatures = (await OptionalFeatures.ListAsync()).Select(f => new FeatureRowViewModel(f)).ToList();
            FeaturesError = null;
            _featuresLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("Reading Windows features failed", ex);
            FeaturesError = Txt.AppsReadFailed(ex.Message);
        }
        finally { IsLoading = false; }
        Filter();
        SelectedFeature = _features.FirstOrDefault(f => f.Name == keep);
    }

    async Task LoadInfoAsync(FeatureRowViewModel row)
    {
        try { var info = await OptionalFeatures.InfoAsync(row.Name); if (_selectedFeature == row) { _featureInfo = info; Raise(nameof(FeatureDetail)); } }
        catch (Exception ex) { Log.Warn($"Feature info for {row.Name}: {ex.Message}"); }
    }

    async Task SetFeatureAsync(bool enable)
    {
        if (_selectedFeature?.Feature is not { } f) return;
        await RunAsync(Txt.ChangingFeature, async () =>
        {
            var reboot = await OptionalFeatures.SetAsync(f.Name, enable);
            await LoadFeaturesAsync(); // verified by re-reading the DISM list
            var now = _allFeatures.FirstOrDefault(x => x.Name == f.Name)?.Feature;
            return now is not null && now.IsEnabled == enable ? (Txt.FeatureChanged(f.Name, enable, reboot || now.IsPending), false)
                : (Txt.FeatureNotConfirmed(f.Name, now?.State ?? Txt.Unknown), true);
        });
    }

    const int ServiceDisabled = unchecked((int)0x80070422); // ERROR_SERVICE_DISABLED

    void Filter()
    {
        var q = _search.Trim();
        bool Match(string text) => q.Length == 0 || MainViewModel.Matches(text, q);
        Programs = _allPrograms.Where(p => Match(p.SearchText)).ToList();
        Features = _allFeatures.Where(f => Match(f.SearchText)).ToList();
        Apps = _allApps.Where(a => (_kind.Key.Length == 0 || a.App.Kind.ToString() == _kind.Key) && Match(a.SearchText)).ToList();
    }

    async Task UninstallAsync()
    {
        if (_selectedProgram?.Program is not { } p) return;
        await RunAsync(Txt.WaitingForUninstaller, async () =>
        {
            // Before: after the uninstaller runs, the folder and the list of .exe files are gone.
            var trace = await Task.Run(() => Core.Apps.Leftovers.Trace(p));
            await InstalledPrograms.UninstallAsync(p);
            await RefreshAsync();
            if (InstalledPrograms.StillInstalled(p)) return (Txt.StillListed(p.Name), true);
            Message = Txt.ScanningLeftovers;
            var found = await ShowLeftoversAsync(trace, force: false);
            return (found > 0 ? Txt.UninstalledLeftovers(p.Name, found) : Txt.UninstalledClean(p.Name), false);
        });
    }

    /// <summary>Broken or missing uninstaller: the same search, with the program's folder and entry, without running anything.</summary>
    async Task ForceRemoveAsync()
    {
        if (_selectedProgram?.Program is not { } p) return;
        await RunAsync(Txt.ScanningLeftovers, async () =>
        {
            var found = await ShowLeftoversAsync(await Task.Run(() => Core.Apps.Leftovers.Trace(p)), force: true);
            return (found > 0 ? Txt.ForceRemovalFound(p.Name, found) : Txt.NothingFound(p.Name), false);
        });
    }

    Task<int> ShowLeftoversAsync(ProgramTrace trace, bool force) => ShowLeftoversAsync(new[] { trace }, force);

    async Task<int> ShowLeftoversAsync(IReadOnlyList<ProgramTrace> traces, bool force)
    {
        var items = await Task.Run(() => LeftoversViewModel.Scan(traces, force));
        if (items.Count > 0) Leftovers = new LeftoversViewModel(traces, force, items, () => { Leftovers = null; MainViewModel.Fire(RefreshAsync()); });
        return items.Count;
    }

    async Task RemoveAppAsync()
    {
        if (_selectedApp?.App is not { } a) return;
        await RunAsync(Txt.Removing2, async () =>
        {
            var trace = Core.Apps.Leftovers.Trace(a);
            var allUsers = RemoveForAllUsers;
            if (allUsers) await StoreApps.RemoveForAllUsersAsync(a);
            else await StoreApps.RemoveAsync(a.FullName);
            await RefreshAsync();
            if (StoreApps.Exists(a.FullName) || allUsers && StoreApps.ExistsForAnyUser(a.FullName)) return (Txt.AppStillThere(a.Name), true);
            if (allUsers) return (Txt.AppRemovedAllUsers(a.Name), false);
            Message = Txt.ScanningLeftovers;
            var found = await ShowLeftoversAsync(trace, force: false);
            return (found > 0 ? Txt.AppRemovedLeftovers(a.Name, found) : Txt.AppRemoved(a.Name), false);
        });
    }

    async Task RunAsync(string busyText, Func<Task<(string Message, bool Error)>> op)
    {
        _busy = true;
        Report(busyText, false);
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var (message, error) = await op();
            Report(message, error);
        }
        catch (Exception ex)
        {
            Log.Error("App action failed", ex);
            Report(ex.Message, true);
        }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }

    void OpenLocation()
    {
        if (_selectedProgram?.Program.InstallLocation is { } l) Shell("explorer.exe", ProcessRunner.Quote(l));
    }

    static void Shell(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
}
