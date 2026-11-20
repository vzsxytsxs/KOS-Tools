using System.IO;
using System.Diagnostics;
using System.Windows.Input;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.KosState;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public abstract class PageViewModel(string key, string title, string glyph, string subtitle) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    /// <summary>Drawn icon (Path data) for when the icon font has no suitable one; null = uses the Glyph.</summary>
    public virtual string? IconPath => null;
    public string Subtitle { get; } = subtitle;
    /// <summary>The page has its own scrollable (virtualized) list; the window does not scroll the whole page.</summary>
    public virtual bool ScrollsItself => false;
    public virtual Task RefreshAsync() => Task.CompletedTask;
    /// <summary>The page's own button in the window header, next to refresh; null = none.</summary>
    public virtual ICommand? HeaderCommand => null;
    public virtual string? HeaderGlyph => null;
    public virtual string? HeaderTooltip => null;
    /// <summary>A KOS app's master switch, in its window header (AppWindow); null = none. Raise MasterOn and MasterText when they change.</summary>
    public virtual ICommand? MasterCommand => null;
    public virtual bool MasterOn => false;
    public virtual string? MasterText => null;
    public virtual string? MasterTooltip => null;
    /// <summary>The page became (or stopped being) the one displayed; used for periodic reads only while visible.</summary>
    public virtual void OnShown(bool shown) { }
}

public sealed class GroupViewModel(string title, IReadOnlyList<ICatalogItem> items) : ObservableObject
{
    bool _isExpanded = true;
    public string Title { get; } = title;
    public IReadOnlyList<ICatalogItem> Items { get; } = items;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

public sealed record TabViewModel(string Title, IReadOnlyList<GroupViewModel> Groups);

/// <summary>Page generated from the catalog: tabs → expandable groups → cards.</summary>
public sealed class CatalogPageViewModel : PageViewModel
{
    TabViewModel? _selectedTab;
    bool _refreshing;

    public CatalogPageViewModel(string key, string title, string glyph, string subtitle,
        IEnumerable<ICatalogItem> items, Func<ICatalogItem, string>? groupOf = null, bool singleTab = false)
        : base(key, title, glyph, subtitle)
    {
        groupOf ??= i => i.Group ?? "";
        Items = items.ToList();
        Tabs = Items.GroupBy(i => singleTab ? "" : i.Tab ?? "")
            .OrderBy(t => Array.IndexOf(Core.Catalog.Pages.WindowsTabs, t.Key))
            .Select(t => new TabViewModel(t.Key, t.GroupBy(groupOf).Select(g => new GroupViewModel(g.Key, g.ToList())).ToList()))
            .ToList();
        _selectedTab = Tabs.FirstOrDefault();
        ApplyKosDefaultsCommand = new AsyncCommand(ApplyKosDefaultsAsync);
    }

    public IReadOnlyList<ICatalogItem> Items { get; }
    public IReadOnlyList<TabViewModel> Tabs { get; }
    public bool HasTabs => Tabs.Count > 1;
    public bool IsEmpty => Items.Count == 0;
    // The tab list is reused across pages and tries to reset the selection when ItemsSource changes.
    public TabViewModel? SelectedTab { get => _selectedTab; set { if (value is not null) Set(ref _selectedTab, value); } }

    IEnumerable<TweakViewModel> Tweaks => Items.OfType<TweakViewModel>();
    public bool HasKosDefaults => Tweaks.Any(t => t.KosDefault is not null);
    public ICommand ApplyKosDefaultsCommand { get; }

    public override async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { foreach (var item in Items) await item.RefreshAsync(); }
        finally { _refreshing = false; }
    }

    public async Task EnsureLoadedAsync()
    {
        foreach (var item in Items) await item.EnsureLoadedAsync();
    }

    async Task ApplyKosDefaultsAsync()
    {
        await EnsureLoadedAsync();
        var todo = Tweaks.Where(t => t.KosDefault is { } d && t.CanChange
            && !(t.State is { Status: TweakStatus.Matched } s && s.Option == d)).ToList();
        if (todo.Count == 0)
        {
            Toasts.Show(Txt.KosDefaultsNothing, ToastKind.Info);
            return;
        }
        var ok = 0;
        var warned = new List<string>(); // a single warning at the end, instead of one per tweak
        foreach (var t in todo)
        {
            if (!await t.ApplyAsync(t.KosDefault!, warn: false)) continue;
            ok++;
            if (t.NeedsWarning(t.KosDefault)) warned.Add(t.Name);
        }
        Toasts.Show(Txt.KosDefaultsResult(ok, todo.Count), ok == todo.Count ? ToastKind.Success : ToastKind.Error);
        if (warned.Count > 0) Toasts.Show(Txt.KosDefaultsWarned(warned), ToastKind.Warning);
    }
}

public enum HomeLevel { Muted, Normal, Warning, Error }

/// <summary>A row under System on Home; selected, its section shows in the inspector. Rows with nothing to say are hidden.</summary>
public sealed class HomeSystemRow(string key, string name, HomeViewModel home) : ObservableObject
{
    string _sub = "";
    HomeLevel _level;
    bool _visible, _selected;

    public string Key => key;
    public string Name => name;
    public HomeViewModel Home => home;
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    /// <summary>The colour of its dot: warning and error brushes only for warnings and errors.</summary>
    public HomeLevel Level { get => _level; set => Set(ref _level, value); }
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Home: a list of the hardware and of the system's state on the left, the selected row's details on the right (CPU when it opens).</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    IReadOnlyList<string> _pending = Array.Empty<string>();
    PostSetupReport? _postSetup;
    string? _kosNotice, _kosCheckedFor;
    object? _selection, _inspector;

    public HomeViewModel() : base("home", Txt.PageHome, "", Txt.PageHomeSubtitle)
    {
        RestartCommand = new RelayCommand(Restart);
        OpenPostSetupCommand = new RelayCommand(() =>
        {
            if (_postSetup is { } p && Directory.Exists(p.Folder))
                Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(p.Folder)) { UseShellExecute = true })?.Dispose();
        });
        OpenKosStateCommand = new RelayCommand(() => KosStatePageViewModel.Open?.Invoke());
        SelectCommand = new RelayCommand(p => { if (p is not null) Select(p); });
        Logs = new LogStatusViewModel();
        Logs.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LogStatusViewModel.HasProblem)) UpdateSystemRows(); };
        SystemRows = new[]
        {
            new HomeSystemRow("kos", Txt.KosHomeTitle, this), new HomeSystemRow("post", Txt.PostSetupTitle, this),
            new HomeSystemRow("pending", Txt.HomePendingTitle, this), new HomeSystemRow("status", Txt.HomeSystemStatus, this),
            new HomeSystemRow("logs", Txt.LogProblemTitle, this),
        };
        InitLive();
        UpdateSystemRows();
        Select(Cpu);
    }

    /// <summary>The list and the inspector scroll on their own, each beside the other.</summary>
    public override bool ScrollsItself => true;
    public IReadOnlyList<HomeSystemRow> SystemRows { get; }
    /// <summary>What the inspector shows: a <see cref="HardwareInspector"/> or a <see cref="HomeSystemRow"/>.</summary>
    public object? Inspector { get => _inspector; private set => Set(ref _inspector, value); }
    public ICommand SelectCommand { get; }

    /// <summary>A hardware row or a system row; the details of a hardware row are read in the background (cached, except disks).</summary>
    internal void Select(object item)
    {
        if (ReferenceEquals(item, _selection)) return;
        _selection = item;
        foreach (var c in Hardware.Concat(Resources)) c.IsSelected = ReferenceEquals(c, item);
        foreach (var r in SystemRows) r.IsSelected = ReferenceEquals(r, item);
        if (item is not MonitorCard card) { Inspector = item; return; }
        var inspector = new HardwareInspector(card);
        Inspector = inspector;
        if (card.LoadDetail is { } load) MainViewModel.Fire(LoadDetailAsync(inspector, load));
    }

    async Task LoadDetailAsync(HardwareInspector inspector, Func<Task<HardwareDetail?>> load)
    {
        var detail = await load();
        if (ReferenceEquals(Inspector, inspector)) inspector.Detail = detail; // another row may have been chosen meanwhile
    }

    void UpdateSystemRows()
    {
        void Row(string key, bool visible, string sub, HomeLevel level)
        {
            var r = SystemRows.First(x => x.Key == key);
            (r.IsVisible, r.Sub, r.Level) = (visible, sub, level);
        }
        Row("kos", ShowKosNotice, _kosNotice ?? "", HomeLevel.Warning);
        Row("post", ShowPostSetup, _postSetup?.Summary ?? "", _postSetup is { Errors: > 0 } ? HomeLevel.Error : HomeLevel.Warning);
        Row("pending", true, Txt.HomePending(_pending.Count), HasPending ? HomeLevel.Normal : HomeLevel.Muted);
        Row("status", true, Txt.HomeStatusCount(SystemStatus.Count), HomeLevel.Muted);
        Row("logs", Logs.HasProblem, Logs.Problem ?? "", HomeLevel.Warning);
        if (_selection is HomeSystemRow { IsVisible: false }) Select(Cpu);
    }

    public IReadOnlyList<string> Pending { get => _pending; private set { Set(ref _pending, value); Raise(nameof(HasPending)); } }
    public bool HasPending => _pending.Count > 0;
    /// <summary>Latest KOS post-install; Home only shows the card when there are warnings or errors.</summary>
    public PostSetupReport? PostSetup { get => _postSetup; private set { Set(ref _postSetup, value); Raise(nameof(ShowPostSetup)); } }
    public bool ShowPostSetup => _postSetup?.HasProblems == true;
    public LogStatusViewModel Logs { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenPostSetupCommand { get; }
    /// <summary>After a Windows update (a new build or update number): how many KOS items it put back; null = nothing to say.</summary>
    public string? KosNotice { get => _kosNotice; private set { Set(ref _kosNotice, value); Raise(nameof(ShowKosNotice)); } }
    public bool ShowKosNotice => _kosNotice is not null;
    public ICommand OpenKosStateCommand { get; }

    public override async Task RefreshAsync()
    {
        var (pending, postSetup) = await Task.Run(() =>
        {
            var names = TweakEngine.Instance.Pending.Current().Keys
                .Select(id => KosCatalog.Find(id)?.Name ?? KosCatalog.Actions.FirstOrDefault(a => a.Id == id)?.Name ?? id)
                .Select(Txt.ChangedByToolbox);
            List<string> windows;
            try { windows = SystemSummary.WindowsRebootReasons(); }
            catch (Exception ex) { windows = new() { Txt.RebootCheckFailed(ex.Message) }; }
            PostSetupReport? report = null;
            try { report = PostSetupLog.Latest(); }
            catch (Exception ex) { Log.Warn($"Home: reading the post-setup logs failed: {ex.Message}"); }
            return (names.Concat(windows).ToList(), report);
        });
        Pending = pending;
        PostSetup = postSetup;
        SystemStatus = await Task.Run(ReadStatus);
        KosNotice = await KosNoticeAsync();
        UpdateSystemRows();
    }

    /// <summary>
    /// Read-only KOS check, once per build in this run, when Windows isn't at the build of the last check on the KOS state page.
    /// The notice stays until that page checks again; with nothing put back, this build counts as checked.
    /// </summary>
    async Task<string?> KosNoticeAsync()
    {
        try
        {
            var now = KosSnapshots.BuildText(KosSnapshots.WindowsBuild());
            var last = AppSettings.Current.KosCheckedBuild;
            if (now is null || last == now) return null;
            if (_kosCheckedFor == now) return _kosNotice;
            _kosCheckedFor = now;
            var report = await Task.Run(() => KosCheck.Run());
            var count = report.Items.Count(i => i.Section != KosSection.Toolbox);
            if (count == 0)
            {
                AppSettings.Current.KosCheckedBuild = now;
                AppSettings.Current.Save();
                return null;
            }
            // Before the first check, the build the last good state (or the setup's baseline) was saved on.
            var before = last ?? KosSnapshots.BuildText((report.MachineReference ?? report.UserReference)?.Snapshot.Windows);
            return Txt.KosHomeNotice(before == now ? null : before, now, count);
        }
        catch (Exception ex)
        {
            Log.Warn($"Home: the KOS state check failed: {ex.Message}");
            return null;
        }
    }

    void Restart()
    {
        Log.Info("Restart requested by the user.");
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
    }
}

/// <summary>State of the log in ProgramData: an English warning when it cannot be written, and the permissions fix.</summary>
public sealed class LogStatusViewModel : OperationViewModel
{
    public LogStatusViewModel()
    {
        Log.ProblemChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { Raise(nameof(Problem)); Raise(nameof(HasProblem)); });
        OpenCommand = new RelayCommand(Open);
        FixCommand = new AsyncCommand(FixAsync, () => !IsBusy && !Elevation.IsAdmin);
    }

    public string Directory => Log.Directory;
    public string? Problem => Log.Problem;
    public bool HasProblem => Log.Problem is not null;
    public bool CanFix => !Elevation.IsAdmin;
    public ICommand OpenCommand { get; }
    public ICommand FixCommand { get; }

    void Open()
    {
        if (!System.IO.Directory.Exists(Log.Directory)) { Toasts.Show(Txt.FolderNotFound(Log.Directory), ToastKind.Error); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(Log.Directory)) { UseShellExecute = true })?.Dispose();
    }

    // The elevated process prepares the folder (Log.Initialize) and grants users permission only on Logs\Toolbox.
    async Task FixAsync() => await RunAsync(async (ct, _) =>
    {
        await ElevatedRunner.RunAsync(new ElevatedJob { PrepareLogs = true }, ct);
        Log.Info("Log folder prepared by the elevated process.");
        return Log.Problem is null ? (true, Txt.LogsFixed) : (false, Log.Problem);
    }, Txt.WaitingForElevation, queued: false);
}

/// <summary>Binary preference of the toolbox itself, shown with the same segmented control as the tweaks.</summary>
public sealed class PreferenceViewModel
{
    public PreferenceViewModel(string name, string description, Func<bool> get, Action<bool> set)
    {
        Name = name;
        Description = description;
        Options = new[] { new OptionItem(Tweak.Off, Txt.Disabled), new OptionItem(Tweak.On, Txt.Enabled) };
        SelectCommand = new RelayCommand(p =>
        {
            set((string)p! == Tweak.On);
            AppSettings.Current.Save();
            Sync(get());
        });
        Sync(get());
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<OptionItem> Options { get; }
    public ICommand SelectCommand { get; }

    void Sync(bool on) { Options[0].IsSelected = !on; Options[1].IsSelected = on; }
}

/// <summary>A change in Settings › Change history: what changed, the option before and after, the values in the tooltip, and Undo.</summary>
public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(HistoryEntry e, bool latest, Func<HistoryEntry, Task> undo)
    {
        var tweak = KosCatalog.Find(e.TweakId);
        string Label(string? key, string fallback) => tweak?.Options.FirstOrDefault(o => o.Key == key)?.Label ?? fallback;
        Time = e.At.ToString("g");
        Name = tweak?.Name ?? e.TweakId;
        Change = e.Option is null ? $"{Txt.HistoryRestoredValues} → {Label(e.After, Txt.Partial)}"
            : $"{Label(e.Before.Option, Txt.HistoryCustom)} → {Label(e.Option, e.Option)}";
        Note = e.Confirmed ? null : Txt.HistoryNotConfirmed;
        // Only the values that changed, by their display name.
        var values = tweak?.Specs.Select(s => s.Setting)
            .Where(s => (e.Before.Values.ContainsKey(s.Key) || e.AfterValues.ContainsKey(s.Key))
                        && e.Before.Values.GetValueOrDefault(s.Key) != e.AfterValues.GetValueOrDefault(s.Key))
            .Select(s => $"{s.Display}: {e.Before.Values.GetValueOrDefault(s.Key) ?? Txt.HistoryAbsent} → {e.AfterValues.GetValueOrDefault(s.Key) ?? Txt.HistoryAbsent}")
            .ToList();
        Values = values is { Count: > 0 } ? string.Join("\n", values) : null;
        CanUndo = latest && tweak is not null;
        UndoCommand = new AsyncCommand(() => undo(e));
    }

    public string Time { get; }
    public string Name { get; }
    public string Change { get; }
    public string? Note { get; }
    public string? Values { get; }
    public bool CanUndo { get; }
    public ICommand UndoCommand { get; }
}

public sealed class SettingsViewModel : PageViewModel
{
    public SettingsViewModel() : base("settings", Txt.PageSettings, "", Txt.PageSettingsSubtitle)
    {
        var s = AppSettings.Current;
        Preferences = new[]
        {
            new PreferenceViewModel(Txt.PrefAnimations, Txt.PrefAnimationsDetail, () => s.EnableAnimations, v => s.EnableAnimations = v),
            new PreferenceViewModel(Txt.PrefMemoryRead, Txt.PrefMemoryReadDetail, () => s.ReadMemoryOnStart, v => s.ReadMemoryOnStart = v),
        };
        Logs = new LogStatusViewModel();
        OpenDataCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(AppPaths.DataRoot)) { UseShellExecute = true })?.Dispose();
        });
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        OpenGitHubCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true })?.Dispose());
    }

    public const string GitHubUrl = "https://github.com/vzsxytsxs";
    const int Shown = 40;
    IReadOnlyList<HistoryRowViewModel> _history = Array.Empty<HistoryRowViewModel>();
    string? _historyMore;

    /// <summary>The latest changes, newest first.</summary>
    public IReadOnlyList<HistoryRowViewModel> History { get => _history; private set { Set(ref _history, value); Raise(nameof(HistoryIsEmpty)); } }
    public bool HistoryIsEmpty => _history.Count == 0;
    public string? HistoryMore { get => _historyMore; private set => Set(ref _historyMore, value); }

    public override async Task RefreshAsync()
    {
        var all = await Task.Run(TweakEngine.Instance.History.Load);
        History = all.AsEnumerable().Reverse().Take(Shown).Select(e => new HistoryRowViewModel(e, ChangeHistory.IsLatest(all, e), UndoAsync)).ToList();
        HistoryMore = all.Count > Shown ? Txt.HistoryMore(all.Count - Shown) : null;
    }

    async Task UndoAsync(HistoryEntry entry)
    {
        // Checked before queueing: a change still running could leave other values than the ones undo compares.
        if (TweakEngine.Instance.IsBusy) { Toasts.Show(Txt.ChangeInProgress, ToastKind.Warning); return; }
        if (KosCatalog.Find(entry.TweakId) is not { } tweak) { Toasts.Show(Txt.HistoryNotInCatalog, ToastKind.Error); return; }
        var r = await TweakEngine.Instance.UndoAsync(tweak, entry);
        Toasts.Show($"{tweak.Name}: {r.Message}", r.Success ? ToastKind.Success : ToastKind.Error);
        if (r.Success && TweakViewModel.WarningFor(tweak, entry.Before.Option) is { } note) Toasts.Show($"{tweak.Name}: {note}", ToastKind.Warning);
        await RefreshAsync();
    }

    public IReadOnlyList<PreferenceViewModel> Preferences { get; }
    public LogStatusViewModel Logs { get; }
    public string Version => Txt.VersionText(typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3));
    public string DataText => Txt.DataFolderDetail(AppPaths.DataRoot);
    public string ScriptsText => Txt.ScriptsFolder(ScriptRef.Root);
    /// <summary>KOS version and the post-install (moved out of Home).</summary>
    public IReadOnlyList<InfoRow> KosInfo => SystemSummary.KosRows();
    public bool ShowLocationWarning => !Elevation.InProtectedFolder;
    public bool CanRestartAsAdmin => !Elevation.IsAdmin;
    public ICommand OpenDataCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public ICommand OpenGitHubCommand { get; }

    internal static void RestartAsAdmin()
    {
        if (TweakEngine.Instance.IsBusy) { Toasts.Show(Txt.ChangeInProgress, ToastKind.Warning); return; }
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Views.SingleInstance.Release(); // the new copy may start before this one has closed
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* UAC cancelled: stays as it is */ }
    }
}
