using System.Windows.Input;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>A program with a newer version; checked ones are updated together.</summary>
public sealed class AppUpdateRowViewModel(AppUpdate update, SetupAppViewModel app) : ObservableObject
{
    bool _checked = true;
    public AppUpdate Update { get; } = update;
    public SetupAppViewModel App { get; } = app;
    public string Name => App.Name;
    public string Icon => App.Icon;
    public string Versions => $"{Update.Installed} → {Update.Available}";
    public string Source => Update.Source == "Chocolatey" ? "Through Chocolatey" : "New installer from the developer";
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) CommandManager.InvalidateRequerySuggested(); } }
}

/// <summary>Install › Updates: the catalog programs with a newer version; updating them goes through the normal download/install queue.</summary>
public sealed class SetupUpdatesViewModel : ObservableObject
{
    readonly Func<SetupApp, SetupAppViewModel?> _find;
    readonly Action<List<SetupAppViewModel>> _start;
    IReadOnlyList<AppUpdateRowViewModel> _rows = Array.Empty<AppUpdateRowViewModel>();
    string? _status;
    bool _checking, _checkedOnce;

    public SetupUpdatesViewModel(Func<SetupApp, SetupAppViewModel?> find, Action<List<SetupAppViewModel>> start)
    {
        _find = find;
        _start = start;
        CheckCommand = new AsyncCommand(CheckAsync, () => !_checking);
        UpdateCommand = new RelayCommand(() => _start(_rows.Where(r => r.IsChecked).Select(r => r.App).ToList()), () => !_checking && _rows.Any(r => r.IsChecked));
    }

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public IReadOnlyList<AppUpdateRowViewModel> Rows { get => _rows; private set { Set(ref _rows, value); Raise(nameof(HasRows)); } }
    public bool HasRows => _rows.Count > 0;
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool IsChecking { get => _checking; private set { if (Set(ref _checking, value)) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>The first time the tab opens it checks by itself (reading only); after that, on the button.</summary>
    public Task EnsureCheckedAsync() => _checkedOnce ? Task.CompletedTask : CheckAsync();

    async Task CheckAsync()
    {
        _checkedOnce = true;
        IsChecking = true;
        Status = "Checking the installed programs against their sources…";
        try
        {
            var (updates, problems) = await Task.Run(async () => await AppUpdates.CheckAsync(InstalledPrograms.List(), CancellationToken.None));
            Rows = updates.Select(u => _find(u.App) is { } app ? new AppUpdateRowViewModel(u, app) : null).OfType<AppUpdateRowViewModel>().ToList();
            Status = (Rows.Count == 0 ? "Every program installed from Setup is up to date." : $"{Rows.Count} program(s) have a newer version.")
                     + (problems.Count > 0 ? $" Not checked: {string.Join("; ", problems)}" : "");
        }
        catch (Exception ex)
        {
            Log.Error("Setup updates: check failed", ex);
            Status = $"The check failed: {ex.Message}";
        }
        finally { IsChecking = false; }
    }
}

public sealed record StorePackageRow(string Name, string Detail);

/// <summary>Install › Microsoft Store: finds a Store app by its link or id and installs its packages without the Store app.</summary>
public sealed class StoreInstallViewModel : ObservableObject
{
    string _link = "";
    StoreProduct? _product;
    string? _status;
    bool _busy, _isError;
    double _percent;
    IReadOnlyList<StorePackageRow> _packages = Array.Empty<StorePackageRow>();
    CancellationTokenSource? _cts;

    public StoreInstallViewModel()
    {
        FindCommand = new AsyncCommand(FindAsync, () => !_busy && StoreDownloader.ProductId(_link) is not null);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_busy && _product is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
    }

    public ICommand FindCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>A Store link (apps.microsoft.com/detail/…) or the product id alone.</summary>
    public string Link { get => _link; set { if (Set(ref _link, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }
    public string? Title => _product?.Title;
    public bool HasProduct => _product is not null;
    public IReadOnlyList<StorePackageRow> Packages { get => _packages; private set => Set(ref _packages, value); }
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? ServicesNote => StoreDownloader.ServicesDisabled ? Txt.StoreServiceDisabled : null;

    /// <summary>Opened from a Setup card that only exists on the Store: fills in its id and looks it up.</summary>
    public void Open(string link)
    {
        Link = link;
        if (!_busy && FindCommand.CanExecute(null)) FindCommand.Execute(null);
    }

    void Show(StoreProduct? product)
    {
        _product = product;
        Raise(nameof(Title));
        Raise(nameof(HasProduct));
        Packages = product?.Packages.Select(p => new StorePackageRow($"{p.Name} {p.Version}",
            $"{(p == product.Main ? "App" : "Framework, if the app needs it")} · {p.Arch} · {Txt.Size(p.Size)}")).ToList() ?? new List<StorePackageRow>();
        CommandManager.InvalidateRequerySuggested();
    }

    void Report(string text, bool error = false) { Status = text; IsError = error; }

    async Task FindAsync()
    {
        var id = StoreDownloader.ProductId(_link)!;
        Raise(nameof(ServicesNote));
        Show(null);
        IsBusy = true;
        Report($"Looking up {id} in the Store…");
        try
        {
            var product = await Task.Run(() => StoreDownloader.FindAsync(id, CancellationToken.None));
            Show(product);
            Report(product.Packages.Count == 1 ? $"{Txt.Size(product.Main.Size)} to download." : $"{Txt.Size(product.Main.Size)} to download, and the frameworks below that the app asks for and this PC doesn't have yet.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Store: looking up {id} failed: {ex.Message}");
            Report(ex is System.Net.Http.HttpRequestException ? $"The Store couldn't be reached: {ex.Message}" : ex.Message, error: true);
        }
        finally { IsBusy = false; }
    }

    async Task InstallAsync()
    {
        if (_product is not { } product) return;
        if (StoreDownloader.ServicesDisabled) { Raise(nameof(ServicesNote)); Report(Txt.StoreServiceDisabled, error: true); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        try
        {
            var step = new Progress<string>(s => { Report(s); Percent = 0; });
            var progress = new Progress<DownloadProgress>(d => { if (d.Total is > 0) Percent = 100.0 * d.Received / d.Total.Value; });
            var installed = await Task.Run(() => StoreDownloader.InstallAsync(product, step, progress, ct), ct);
            Percent = 100;
            var frameworks = installed.Count - 1;
            Report($"{product.Title} is installed{(frameworks > 0 ? $" with {frameworks} framework(s) it needed" : "")}; it's in the Start menu.");
            Toasts.Show($"{product.Title} installed.", ToastKind.Success);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Report("Cancelled."); }
        catch (Exception ex)
        {
            Log.Error($"Store: installing {product.Title} failed", ex);
            Report(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
