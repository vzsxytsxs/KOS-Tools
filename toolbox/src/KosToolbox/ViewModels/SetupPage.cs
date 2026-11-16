using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Debloat;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>A program on the page, or a version of it (Parent points to the card; versions are chosen in a dialog).</summary>
public sealed class SetupAppViewModel : ObservableObject
{
    readonly SetupPageViewModel _page;
    InstalledProgram? _installed;
    bool _checked;

    public SetupAppViewModel(SetupApp app, SetupPageViewModel page, SetupAppViewModel? parent = null)
    {
        App = app;
        _page = page;
        Parent = parent;
        Variants = app.Variants.Select(v => new SetupAppViewModel(v, page, this)).ToList();
    }

    public SetupApp App { get; }
    public SetupAppViewModel? Parent { get; }
    public IReadOnlyList<SetupAppViewModel> Variants { get; }
    public bool HasVariants => Variants.Count > 0;
    public string Name => App.Name;
    public string Description => App.Description;
    public bool CanSelect => App.StoreUrl is null;
    public string SearchText => $"{Name} {Description} {App.Category} {string.Join(' ', Variants.Select(v => v.Name))}";
    public string Icon => $"pack://application:,,,/KosToolbox;component/Assets/Setup/{App.Icon ?? App.Id}.png";

    public InstalledProgram? Installed
    {
        get => _installed;
        set
        {
            if (!Set(ref _installed, value)) return;
            Refresh();
            Parent?.Refresh();
        }
    }

    public bool IsInstalled => HasVariants ? Variants.Any(v => v.IsInstalled) : _installed is not null;

    string? _existing;

    /// <summary>Portable already downloaded to the user's Downloads folder: the button opens it instead of downloading again.</summary>
    public string? ExistingFile
    {
        get => _existing;
        set { if (Set(ref _existing, value)) Refresh(); }
    }

    public string Status =>
        App.StoreUrl is not null ? Txt.SetupStoreOnly
        : App.Portable ? (_existing is { } f ? Txt.SetupPortableDownloaded(Path.GetFileName(f)) : Txt.SetupPortable)
        : HasVariants ? (_checked && Variants.Any(v => v.IsChecked) ? Txt.SetupSelectedVariants(Names(Variants.Where(v => v.IsChecked)))
            : IsInstalled ? Txt.SetupInstalled(Names(InstalledVariants)) : Txt.SetupNotInstalled)
        : _installed is { } p ? Txt.SetupInstalled(p.Version) : Txt.SetupNotInstalled;

    public string ActionText => App.StoreUrl is not null ? Txt.SetupOpenStore : HasVariants ? Txt.SetupChoose
        : IsInstalled ? Txt.SetupReinstall : App.Portable ? (_existing is not null ? Txt.Open : Txt.SetupDownload) : Txt.SetupInstall;

    /// <summary>
    /// Checked for "Install selected". On a program with versions, checking opens the choice dialog; if nothing is
    /// chosen, the checkbox goes back to unchecked.
    /// </summary>
    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (value == _checked) return;
            if (value && HasVariants && !_page.ChooseVariants(this, forInstall: false))
            {
                // The CheckBox has already checked itself: restores the value after the binding update finishes.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Raise(nameof(IsChecked)));
                return;
            }
            if (!value) foreach (var v in Variants) v.IsChecked = false;
            _checked = value;
            Raise();
            Refresh();
            Parent?.Refresh();
            if (Parent is null) _page.Selected(this);
        }
    }

    public void Refresh()
    {
        Raise(nameof(IsInstalled));
        Raise(nameof(Status));
        Raise(nameof(ActionText));
    }

    /// <summary>With single choice, the lower versions modify the upper ones (SpotX over Spotify): only the last one installed counts.</summary>
    IEnumerable<SetupAppViewModel> InstalledVariants =>
        App.SingleVariant ? Variants.Where(v => v.IsInstalled).TakeLast(1) : Variants.Where(v => v.IsInstalled);

    static string Names(IEnumerable<SetupAppViewModel> apps) => string.Join(", ", apps.Select(a => a.Name));
}

public sealed record SetupCategoryViewModel(string Title, IReadOnlyList<SetupAppViewModel> Apps);

/// <summary>A download in the Downloads tab: link → file → signature → installer.</summary>
public sealed class DownloadViewModel(SetupAppViewModel app) : ObservableObject
{
    string _state = Txt.SetupQueued;
    string? _detail, _signer, _target;
    double _percent;
    bool _busy = true, _installing, _error, _deleted;

    public SetupAppViewModel App { get; } = app;
    public string Name => App.Name;
    public CancellationTokenSource Cts { get; private set; } = new();
    public string? File { get; set; }
    /// <summary>What gets opened: the downloaded file or the installer extracted from the .zip.</summary>
    public string? Target { get => _target; set { if (Set(ref _target, value)) { Raise(nameof(CanRun)); Raise(nameof(CanRetry)); } } }

    public string State { get => _state; private set => Set(ref _state, value); }
    public string? Detail { get => _detail; set => Set(ref _detail, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string? Signer { get => _signer; set { Set(ref _signer, value); Raise(nameof(SignerText)); } }
    public string? SignerText => _signer is null ? null : Txt.SetupSignedBy(_signer);
    public bool IsError { get => _error; private set => Set(ref _error, value); }
    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanCancel)); Raise(nameof(CanRun)); Raise(nameof(CanRetry)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool IsInstalling { get => _installing; set { if (Set(ref _installing, value)) Raise(nameof(CanCancel)); } }
    public bool CanCancel => _busy && !_installing;
    public bool CanRun => !_busy && Target is not null;
    public bool CanRetry => !_busy && Target is null && !_deleted;
    public string RunText => App.App.Portable ? Txt.Open : Txt.SetupRun;

    public void Step(string state) { State = state; IsError = false; }

    public void Report(DownloadProgress p)
    {
        if (p.Total is > 0) Percent = 100.0 * p.Received / p.Total.Value;
        Detail = Txt.SetupProgress(p.Received, p.Total, p.BytesPerSecond, p.Aria2);
    }

    public void Begin()
    {
        Cts = new CancellationTokenSource();
        Percent = 0;
        IsBusy = true;
        Step(Txt.SetupQueued);
    }

    public void Finish(string state, bool error = false)
    {
        State = state;
        IsError = error;
        IsInstalling = false;
        IsBusy = false;
        if (!error && Target is not null) Percent = 100;
    }

    /// <summary>
    /// Deletes the installer (and, for a .zip, the extracted folder). Portable programs stay: the file is the program.
    /// Returns the error, or null.
    /// </summary>
    public string? DeleteFiles()
    {
        if (File is null || App.App.Portable) return null;
        try { Installer.Delete(File, Target); }
        catch (Exception ex)
        {
            Log.Warn($"Setup: deleting {File} failed: {ex.Message}");
            return ex.Message;
        }
        Log.Info($"Setup: deleted {File}");
        File = null;
        _deleted = true;
        Target = null;
        return null;
    }
}

/// <summary>
/// Install page: common programs for a new PC, downloaded from the vendor. The installer only opens if the digital signature
/// is valid (or the user confirms); one installer at a time.
/// </summary>
public sealed class SetupPageViewModel : PageViewModel
{
    const string AppsTab = "apps", UpdatesTab = "updates", StoreTab = "store";
    static readonly SemaphoreSlim DownloadSlots = new(3), InstallerSlot = new(1);
    readonly List<SetupAppViewModel> _all;
    IReadOnlyList<SetupCategoryViewModel> _categories = Array.Empty<SetupCategoryViewModel>();
    string _search = "";
    bool _showDownloads;

    public SetupPageViewModel() : base("install", Txt.PageInstall, "", Txt.PageInstallSubtitle)
    {
        _all = SetupCatalog.Apps.Select(a => new SetupAppViewModel(a, this)).ToList();
        Tabs = new[]
        {
            new OptionItem(AppsTab, Txt.TabSetupApps) { IsSelected = true }, new OptionItem(UpdatesTab, Txt.TabSetupUpdates),
            new OptionItem(StoreTab, Txt.TabSetupStore),
        };
        Updates = new SetupUpdatesViewModel(app => _all.SelectMany(a => a.Variants.Prepend(a)).FirstOrDefault(a => a.App == app), Start);
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
        InstallCommand = new RelayCommand(p => Install((SetupAppViewModel)p!));
        // Programs with versions are included with the versions chosen in the dialog.
        InstallSelectedCommand = new RelayCommand(() => Start(_all.Where(a => a.IsChecked).SelectMany(a => a.HasVariants ? a.Variants.Where(v => v.IsChecked) : new[] { a }).ToList()),
            () => _all.Any(a => a.IsChecked));
        ClearSelectionCommand = new RelayCommand(() => { foreach (var a in _all) a.IsChecked = false; }, () => _all.Any(a => a.IsChecked));
        CancelCommand = new RelayCommand(p => ((DownloadViewModel)p!).Cts.Cancel(), p => p is DownloadViewModel { CanCancel: true });
        RunCommand = new AsyncCommand(p => RunAgainAsync((DownloadViewModel)p!), p => p is DownloadViewModel { CanRun: true });
        RetryCommand = new RelayCommand(p => { var d = (DownloadViewModel)p!; d.Begin(); _ = ProcessAsync(d); }, p => p is DownloadViewModel { CanRetry: true });
        RemoveCommand = new RelayCommand(p => Remove(new[] { (DownloadViewModel)p! }), p => p is DownloadViewModel { IsBusy: false });
        ClearFinishedCommand = new RelayCommand(() => Remove(Downloads.Where(d => !d.IsBusy).ToList()), () => Downloads.Any(d => !d.IsBusy));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        HeaderCommand = new RelayCommand(() => { if (ShowDownloads) SelectTab(AppsTab); else ShowDownloadsView(); });
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        Downloads.CollectionChanged += (_, _) => { Raise(nameof(HasDownloads)); RaiseActive(); };
        Filter();
    }

    public IReadOnlyList<OptionItem> Tabs { get; }
    public bool ShowApps => Tabs[0].IsSelected;
    public bool ShowUpdates => Tabs[1].IsSelected;
    public bool ShowStore => Tabs[2].IsSelected;
    public SetupUpdatesViewModel Updates { get; }
    public StoreInstallViewModel Store { get; } = new();
    public bool ShowDownloads { get => _showDownloads; private set => Set(ref _showDownloads, value); }
    public override ICommand? HeaderCommand { get; }
    public override string HeaderGlyph => "\uE896";
    public override string HeaderTooltip => Txt.TabSetupDownloads;
    public IReadOnlyList<SetupCategoryViewModel> Categories { get => _categories; private set => Set(ref _categories, value); }
    public ObservableCollection<DownloadViewModel> Downloads { get; } = new();
    public bool HasDownloads => Downloads.Count > 0;
    public string SelectedText => Txt.SetupSelectedCount(_all.Count(a => a.IsChecked));
    public string ActiveText => Txt.SetupActiveCount(Downloads.Count(d => d.IsBusy));
    public string FolderText => Txt.SetupFolder(Downloader.Folder, Downloader.PortableFolder);
    public string Aria2Text => Txt.SetupAria2(Downloader.Aria2);

    public ICommand SelectTabCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand InstallSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearFinishedCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Filter(); }
    }

    public override async Task RefreshAsync()
    {
        Detect(await Task.Run(InstalledPrograms.List));
        Raise(nameof(Aria2Text));
    }

    void Detect(IReadOnlyList<InstalledProgram> programs)
    {
        foreach (var a in _all.SelectMany(a => a.Variants.Prepend(a)))
        {
            a.Installed = SetupCatalog.Installed(a.App, programs);
            a.ExistingFile = Downloader.ExistingPortable(a.App);
        }
    }

    /// <summary>Portable already downloaded: opens it (re-checking the signature, as the file may have changed).</summary>
    async Task OpenPortableAsync(SetupAppViewModel app, string file)
    {
        if (!System.IO.File.Exists(file)) { app.ExistingFile = Downloader.ExistingPortable(app.App); return; }
        // Without a signature it only opens what the catalog knows the developer publishes that way.
        try
        {
            var run = await Task.Run(() => Installer.Guard(file));
            var signer = await Task.Run(() => Installer.Signer(app.App, run));
            if (signer is null && !app.App.Unsigned) { Installer.Discard(run); Toasts.Show($"{Path.GetFileName(file)}: {Txt.SetupUnsigned}", ToastKind.Error); return; }
            Log.Info($"Setup: opening {run}");
            await Installer.RunAsync(run, app.App.Arguments, portable: true, CancellationToken.None);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { Toasts.Show($"{app.Name}: {ex.Message}", ToastKind.Error); }
    }

    void Filter()
    {
        var q = _search.Trim();
        Categories = _all.Where(a => q.Length == 0 || MainViewModel.Matches(a.SearchText, q))
            .GroupBy(a => a.App.Category).Select(g => new SetupCategoryViewModel(g.Key, g.ToList())).ToList();
    }

    internal void SelectTab(string key)
    {
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        ShowDownloads = false;
        RaiseTabs();
        if (key == UpdatesTab) MainViewModel.Fire(Updates.EnsureCheckedAsync());
    }

    void ShowDownloadsView()
    {
        foreach (var t in Tabs) t.IsSelected = false;
        ShowDownloads = true;
        RaiseTabs();
    }

    void RaiseTabs()
    {
        Raise(nameof(ShowApps));
        Raise(nameof(ShowUpdates));
        Raise(nameof(ShowStore));
    }

    void RaiseActive() => Raise(nameof(ActiveText));

    /// <summary>A program was checked: re-checks whether it is already installed (the card shows the version).</summary>
    internal void Selected(SetupAppViewModel app)
    {
        if (app.IsChecked && !app.HasVariants) app.Installed = SetupCatalog.Installed(app.App, InstalledPrograms.List()); // with versions, the dialog already checked
        Raise(nameof(SelectedText));
        CommandManager.InvalidateRequerySuggested();
    }

    void Install(SetupAppViewModel app)
    {
        if (app.App.StoreUrl is { } store) { SelectTab(StoreTab); Store.Open(store); return; }
        if (app.ExistingFile is { } existing) { MainViewModel.Fire(OpenPortableAsync(app, existing)); return; }
        if (!app.HasVariants) Start(new List<SetupAppViewModel> { app });
        else if (ChooseVariants(app, forInstall: true)) Start(app.Variants.Where(v => v.IsChecked).ToList());
    }

    /// <summary>
    /// Versions dialog (Discord, Spotify): checks what is already installed, shows it next to each version and checks the
    /// chosen ones. False if cancelled (the previous choice stays as it was).
    /// </summary>
    internal bool ChooseVariants(SetupAppViewModel app, bool forInstall)
    {
        Detect(InstalledPrograms.List());
        var items = app.Variants.Select(v => new ChoiceItem(v.App.Category, v.Name, v.Installed is { } p ? Txt.SetupInstalled(p.Version) : null, v.Icon, v)
            { IsChecked = v.IsChecked }).ToList();
        if (!ChoiceDialog.Choose(Txt.SetupChooseTitle(app.Name), app.Description, items, forInstall ? Txt.SetupInstall : Txt.SetupSelect, app.App.SingleVariant))
            return false;
        foreach (var i in items) ((SetupAppViewModel)i.Tag).IsChecked = i.IsChecked;
        return true;
    }

    /// <summary>Puts the chosen ones in the Downloads tab; already installed ones too (the installer updates or repairs).</summary>
    void Start(List<SetupAppViewModel> apps)
    {
        apps.RemoveAll(a => a.App.StoreUrl is not null || Downloads.Any(d => d.App == a && d.IsBusy));
        if (apps.Count == 0) return;
        foreach (var a in apps)
        {
            a.IsChecked = false;
            if (a.Parent is { } parent) parent.IsChecked = false;
            var d = new DownloadViewModel(a);
            d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadViewModel.IsBusy)) RaiseActive(); };
            Downloads.Insert(0, d);
            _ = ProcessAsync(d);
        }
        ShowDownloadsView();
    }

    async Task ProcessAsync(DownloadViewModel d)
    {
        var app = d.App.App;
        var ct = d.Cts.Token;
        if (app.Choco is { } package) { await ChocoAsync(d, package); return; }
        try
        {
            await DownloadSlots.WaitAsync(ct);
            try
            {
                d.Step(Txt.SetupResolving);
                var url = await Downloader.ResolveAsync(app, ct);
                d.Step(Txt.SetupDownloading);
                var progress = new Progress<DownloadProgress>(d.Report); // created here: reports arrive on the UI thread
                var folder = app.Portable ? Downloader.PortableFolder : Downloader.Folder;
                var (file, aria2) = await Task.Run(() => Downloader.DownloadAsync(url, app.FileName, folder, progress, ct), ct);
                d.File = file;
                Log.Info($"Setup: downloaded {app.Name} from {url} to {file}{(aria2 ? " (aria2)" : "")}");
            }
            finally { DownloadSlots.Release(); }

            d.Step(Txt.SetupVerifying);
            d.Target = await Task.Run(() => Installer.Guard(Installer.Prepare(app, d.File)), ct);
            d.Signer = await Task.Run(() => Installer.Signer(app, d.Target), ct);
            if (d.Signer is null)
            {
                Log.Warn($"Setup: {d.Target} has no valid signature; not opened.");
                Installer.Discard(d.Target);
                if (app.Unsigned) d.Finish(Txt.SetupUnsignedByDeveloper);
                else d.Finish(Txt.SetupUnsigned, error: true);
                return;
            }
            await InstallAsync(d);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { d.Finish(Txt.SetupCancelled); }
        catch (Exception ex)
        {
            Log.Error($"Setup: {app.Name} failed", ex);
            d.Finish(Txt.SetupFailed(Message(ex)), error: true);
        }
    }

    /// <summary>
    /// Programs with a Chocolatey package: Chocolatey itself first (its signed MSI, the first time), then "choco upgrade",
    /// one at a time like the installers; the result is checked in Programs and Features.
    /// </summary>
    async Task ChocoAsync(DownloadViewModel d, string package)
    {
        var app = d.App.App;
        d.Step(Txt.SetupWaitingInstaller);
        try { await InstallerSlot.WaitAsync(d.Cts.Token); }
        catch (OperationCanceledException) { d.Finish(Txt.SetupCancelled); return; }
        try
        {
            if (Chocolatey.Exe is null) d.Step(Txt.ChocoInstalling);
            var progress = new Progress<DownloadProgress>(d.Report);
            var exe = await Task.Run(() => Chocolatey.EnsureAsync(progress, d.Cts.Token));
            d.IsInstalling = true; // a running choco is not cancelled by the toolbox
            d.Step(Txt.ChocoInstallingPackage(package));
            var code = await Chocolatey.InstallAsync(exe, package, CancellationToken.None);
            d.App.Installed = SetupCatalog.Installed(app, await Task.Run(InstalledPrograms.List));
            if (d.App.IsInstalled) d.Finish(Txt.SetupInstalledDone);
            else if (Chocolatey.Succeeded(code)) d.Finish(Txt.ChocoDoneNotDetected);
            else d.Finish(Txt.ChocoFailed(code), error: true);
        }
        catch (OperationCanceledException) when (d.Cts.IsCancellationRequested) { d.Finish(Txt.SetupCancelled); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { d.Finish(Txt.SetupInstallerCancelled, error: true); }
        catch (Exception ex)
        {
            Log.Error($"Setup: {app.Name} through Chocolatey failed", ex);
            d.Finish(Txt.SetupFailed(Message(ex)), error: true);
        }
        finally { InstallerSlot.Release(); }
    }

    /// <summary>Opens the installer (one at a time) and, when it closes, checks whether the program ended up installed.</summary>
    async Task InstallAsync(DownloadViewModel d)
    {
        var app = d.App.App;
        d.Step(Txt.SetupWaitingInstaller);
        try { await InstallerSlot.WaitAsync(d.Cts.Token); }
        catch (OperationCanceledException) { d.Finish(Txt.SetupCancelled); return; }
        d.IsInstalling = true; // an open installer is not cancelled by the toolbox
        try
        {
            d.Step(app.Portable ? Txt.Open + "…" : Txt.SetupInstalling);
            Log.Info($"Setup: opening {d.Target}");
            var code = await Installer.RunAsync(d.Target!, app.Arguments, app.Portable, CancellationToken.None);
            d.App.Installed = SetupCatalog.Installed(app, await Task.Run(InstalledPrograms.List));
            if (app.Portable) d.App.ExistingFile = d.File; // the card button now opens this file (through Guard again)
            // Installed: the installer is no longer needed and leaves the folder. If it did not install, it stays to be opened again.
            var deleteError = d.App.IsInstalled ? d.DeleteFiles() : null;
            d.Finish(app.Portable ? Txt.SetupOpened
                : d.App.IsInstalled ? (deleteError is null ? Txt.SetupInstalledDone : Txt.SetupInstalledKept(deleteError))
                : code is null or 0 ? Txt.SetupInstallerClosed : Txt.SetupInstallerExit(code.Value));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { d.Finish(Txt.SetupInstallerCancelled, error: true); }
        catch (Exception ex)
        {
            Log.Error($"Setup: opening {d.Target} failed", ex);
            d.Finish(Txt.SetupFailed(ex.Message), error: true);
        }
        finally { InstallerSlot.Release(); }
    }

    /// <summary>Open again (or for the first time, if it had no signature): re-checks the signature, as the file may have changed.</summary>
    async Task RunAgainAsync(DownloadViewModel d)
    {
        if (!System.IO.File.Exists(d.Target)) { d.Target = null; d.Finish(Txt.SetupFailed(Txt.ToolMissing(d.File ?? "")), error: true); return; }
        // A portable starts again from the downloaded file: the protected copy Guard makes is the one checked and opened.
        if (d.App.App.Portable && d.File is { } downloaded && System.IO.File.Exists(downloaded)) d.Target = await Task.Run(() => Installer.Guard(downloaded));
        d.Signer = await Task.Run(() => Installer.Signer(d.App.App, d.Target));
        if (d.Signer is null && !d.App.App.Unsigned) { Installer.Discard(d.Target); d.Finish(Txt.SetupUnsigned, error: true); return; }
        d.Begin();
        await InstallAsync(d);
    }

    /// <summary>Removes from the list and deletes the installer; what could not be deleted (still in use) stays, with the reason.</summary>
    void Remove(IReadOnlyList<DownloadViewModel> items)
    {
        var failed = new List<string>();
        foreach (var d in items)
        {
            var name = d.File;
            if (d.DeleteFiles() is { } error) failed.Add($"{Path.GetFileName(name)}: {error}");
            else Downloads.Remove(d);
        }
        if (failed.Count > 0) Toasts.Show(Txt.SetupDeleteFailed(string.Join("\n", failed)), ToastKind.Error);
    }

    static string Message(Exception ex) => ex is System.Net.Http.HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code} {code}" : ex.Message;

    static void OpenFolder()
    {
        try
        {
            // Created like the downloads create it: elevated, an admin-only folder (a plain CreateDirectory would let users add files to it).
            Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(Downloader.EnsureFolder())) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }
}
