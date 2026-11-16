using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Drivers;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class DriverRowViewModel(DriverPackage package, bool old) : ObservableObject
{
    bool _checked;
    public DriverPackage Package { get; } = package;
    public string Name => Package.OriginalName;
    public string Published => Package.PublishedName;
    public string Provider => Package.Provider;
    public string Class => Package.Class;
    public string Version => Package.Version?.ToString() ?? "";
    public string Date => Package.Date?.ToString("d") ?? "";
    public string Size => Txt.Size(Package.Size);
    public string UsedBy => Package.InUse ? Txt.UsedBy(Package.Devices.Count) : Txt.NotInUse;
    public string Devices => string.Join("\n", Package.Devices);
    public bool IsOld { get; } = old;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public string SearchText => $"{Name} {Published} {Provider} {Class} {Devices}";
}

/// <summary>
/// Drivers: third-party packages in the driver store, old versions flagged, export
/// and delete through the official pnputil, with confirmation and verification by re-reading the driver store.
/// </summary>
public sealed class DriversPageViewModel : PageViewModel
{
    List<DriverRowViewModel> _all = new();
    IReadOnlyList<DriverRowViewModel> _rows = Array.Empty<DriverRowViewModel>();
    DriverRowViewModel? _selected;
    string _search = "";
    string? _message, _loadError;
    bool _isError, _busy, _loading, _uninstall, _force;

    public DriversPageViewModel() : base("drivers", Txt.PageDrivers, "", Txt.PageDriversSubtitle)
    {
        CheckOldCommand = new RelayCommand(() => { foreach (var r in _all) r.IsChecked = r.IsOld; }, () => _all.Any(r => r.IsOld));
        CheckOlderWideCommand = new RelayCommand(() =>
        {
            var older = DriverStore.OlderByProviderAndClass(_all.Select(r => r.Package));
            foreach (var r in _all) r.IsChecked = older.Contains(r.Published);
        }, () => _all.Count > 1);
        UncheckAllCommand = new RelayCommand(() => { foreach (var r in _all) r.IsChecked = false; });
        DeleteCommand = new AsyncCommand(DeleteAsync, () => !_busy && !_loading);
        ExportCommand = new AsyncCommand(ExportAsync, () => !_busy && _selected is not null);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        OpenGpuDriverCommand = new RelayCommand(() => OpenGpuDriver?.Invoke());
    }

    /// <summary>Installing and removing the display driver live on GPU › Driver; set by MainViewModel.</summary>
    public static Action? OpenGpuDriver { get; set; }
    public ICommand OpenGpuDriverCommand { get; }

    public override bool ScrollsItself => true;
    public ICommand CheckOldCommand { get; }
    public ICommand CheckOlderWideCommand { get; }
    public ICommand UncheckAllCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public IReadOnlyList<DriverRowViewModel> Rows { get => _rows; private set { Set(ref _rows, value); Raise(nameof(CountText)); Raise(nameof(IsEmpty)); } }
    public DriverRowViewModel? Selected { get => _selected; set => Set(ref _selected, value); }
    public string CountText => Txt.DriversCount(_rows.Count, _all.Count);
    public bool IsEmpty => !_loading && _loadError is null && _all.Count == 0;
    public bool UninstallFromDevices { get => _uninstall; set => Set(ref _uninstall, value); }
    public bool ForceDelete { get => _force; set => Set(ref _force, value); }
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Filter(); }
    }

    public override async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        try
        {
            var list = await Task.Run(DriverStore.List);
            var old = DriverStore.OldVersions(list);
            _all = list.Select(p => new DriverRowViewModel(p, old.Contains(p.PublishedName))).ToList();
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the driver store failed", ex);
            _all = new();
            LoadError = Txt.DriversReadFailed(ex.Message);
        }
        finally { IsLoading = false; }
        Filter();
    }

    void Filter()
    {
        var q = _search.Trim();
        Rows = q.Length == 0 ? _all : _all.Where(r => MainViewModel.Matches(r.SearchText, q)).ToList();
    }

    async Task DeleteAsync()
    {
        var rows = _all.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0) { Report(Txt.DriversNothingChecked, false); return; }
        await RunAsync(async () =>
        {
            await DriverStore.DeleteAsync(rows.Select(r => r.Published).ToList(), _uninstall, force: _force);
            // Confirmed by the driver store, not by the pnputil exit code.
            var deleted = rows.Count(r => !DriverStore.Exists(r.Published));
            return (Txt.DriversDeleted(deleted, rows.Count), deleted < rows.Count);
        });
    }

    async Task ExportAsync()
    {
        if (_selected is not { } row) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Txt.ExportDriverTitle };
        if (dialog.ShowDialog() != true) return;
        var folder = dialog.FolderName;
        await RunAsync(async () =>
        {
            await DriverStore.ExportAsync(row.Published, folder);
            var ok = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any();
            return ok ? (Txt.DriverExported(row.Name, folder), false) : (Txt.ChangeNotConfirmed, true);
        });
    }

    async Task RunAsync(Func<Task<(string Message, bool Error)>> op)
    {
        _busy = true;
        Report(Txt.Applying, false);
        CommandManager.InvalidateRequerySuggested();
        string message;
        var error = false;
        try { (message, error) = await op(); }
        catch (ElevationCancelledException ex) { message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("Driver store action failed", ex);
            message = ex.Message;
            error = true;
        }
        finally { _busy = false; }
        await RefreshAsync();
        Report(message, error);
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
}
