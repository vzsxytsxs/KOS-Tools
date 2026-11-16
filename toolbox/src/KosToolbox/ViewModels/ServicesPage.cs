using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Services;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class ServiceDiffRow(ServicesPageViewModel page, ServiceDiff diff) : ObservableObject
{
    bool _checked;
    public ServiceDiff Diff { get; } = diff;
    public string Name => Diff.Name;
    public string InSnapshot => ServiceSnapshots.Describe(Diff.Old);
    public string Current => ServiceSnapshots.Describe(Diff.New);
    public bool Restorable => Diff.Restorable;
    public string? Reason => Diff.Old is null ? Txt.NotInSnapshot : Diff.New is null ? Txt.ServiceDeleted(Name) : null;

    public bool IsChecked
    {
        get => _checked;
        set { if (Restorable && Set(ref _checked, value)) page.UpdateCounts(); }
    }
}

/// <summary>
/// Service snapshots (port of ServiceBackup.ps1 and its interface): create, list, compare with the current state,
/// export CSV and restore only the checked services, with a preview, a safety snapshot and a per-service result.
/// </summary>
public sealed class ServicesPageViewModel : PageViewModel
{
    SnapshotInfo? _selected;
    ListCollectionView _view = new(new List<ServiceDiffRow>());
    List<ServiceDiffRow> _rows = new();
    string _filter = "";
    bool _busy, _isError, _hasProgress, _comparing;
    double _progress;
    string? _message, _loadError, _rowsFor;
    int _serviceCount, _checked, _legacy;

    public ServicesPageViewModel() : base("services", Txt.PageServices, "", Txt.PageServicesSubtitle)
    {
        NewSnapshotCommand = new AsyncCommand(NewSnapshotAsync, () => !_busy);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => !_busy && _checked > 0);
        CheckAllCommand = new RelayCommand(() => Check(true), () => !_busy && _rows.Count > 0);
        UncheckAllCommand = new RelayCommand(() => Check(false), () => !_busy && _checked > 0);
        ExportCommand = new RelayCommand(Export, () => _selected is not null && _rows.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OrganizeCommand = new AsyncCommand(OrganizeAsync, () => !_busy && _legacy > 0);
    }

    public override bool ScrollsItself => true;
    public ObservableCollection<SnapshotInfo> Snapshots { get; } = new();
    public ListCollectionView View { get => _view; private set => Set(ref _view, value); }
    public ICommand NewSnapshotCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand UncheckAllCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OrganizeCommand { get; }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(NoDifferences)); CommandManager.InvalidateRequerySuggested(); } } }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool HasProgress { get => _hasProgress; private set => Set(ref _hasProgress, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? LoadError { get => _loadError; private set { Set(ref _loadError, value); Raise(nameof(HasLoadError)); Raise(nameof(NoDifferences)); } }
    public bool HasLoadError => _loadError is not null;
    public string Folder => ServiceSnapshots.Directory;
    public bool IsEmpty => Snapshots.Count == 0;
    public bool HasLegacy => _legacy > 0;
    public string LegacyText => Txt.LegacySnapshots(_legacy, ServiceSnapshots.ScriptDirectory);
    public string Stats => _selected is null ? "" : Txt.SnapshotStats(_serviceCount, _rows.Count, _checked);
    public bool NoDifferences => _selected is { Problem: null } && _loadError is null && _rows.Count == 0 && !_busy && !_comparing;

    public string FilterText
    {
        get => _filter;
        set { if (Set(ref _filter, value ?? "")) _view.Refresh(); }
    }

    public SnapshotInfo? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(Stats));
            MainViewModel.Fire(CompareAsync());
        }
    }

    public override async Task RefreshAsync()
    {
        var keep = _selected?.Name;
        var (list, legacy) = await Task.Run(() => (ServiceSnapshots.List(), ServiceSnapshots.LegacyFiles().Count));
        Snapshots.Clear();
        foreach (var s in list) Snapshots.Add(s);
        _legacy = legacy;
        Raise(nameof(HasLegacy));
        Raise(nameof(LegacyText));
        Raise(nameof(IsEmpty));
        _selected = null;
        Selected = Snapshots.FirstOrDefault(s => s.Name == keep) ?? Snapshots.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Compares the selected snapshot with the current Registry state.</summary>
    async Task CompareAsync()
    {
        var snap = _selected;
        // When the same snapshot is reloaded (F5, returning to the page), the "Restore" checkmarks are kept.
        var keep = snap is not null && snap.Name == _rowsFor
            ? _rows.Where(r => r.IsChecked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        _comparing = snap is { Problem: null };
        SetRows(new List<ServiceDiffRow>(), 0);
        _rowsFor = null;
        if (snap is null) return;
        if (snap.Problem is not null) { LoadError = snap.Problem; return; }
        try
        {
            var (diffs, count) = await Task.Run(() =>
            {
                var data = ServiceSnapshots.Load(snap.Name);
                return (ServiceSnapshots.Compare(data, ServiceSnapshots.Load(ServiceSnapshots.Current)), data.Services.Count);
            });
            if (_selected != snap) return;
            _comparing = false;
            LoadError = null;
            var rows = diffs.Select(d => new ServiceDiffRow(this, d)).ToList();
            if (keep is not null) foreach (var r in rows) if (keep.Contains(r.Name)) r.IsChecked = true;
            _rowsFor = snap.Name;
            SetRows(rows, count);
        }
        catch (Exception ex)
        {
            Log.Error($"Comparing snapshot {snap.Name} failed", ex);
            if (_selected != snap) return;
            _comparing = false;
            LoadError = ex.Message;
        }
    }

    void SetRows(List<ServiceDiffRow> rows, int serviceCount)
    {
        _rows = rows;
        _serviceCount = serviceCount;
        View = new ListCollectionView(rows) { Filter = o => _filter.Length == 0 || MainViewModel.Matches(((ServiceDiffRow)o).Name, _filter.Trim()) };
        UpdateCounts();
    }

    internal void UpdateCounts()
    {
        _checked = _rows.Count(r => r.IsChecked);
        Raise(nameof(Stats));
        Raise(nameof(NoDifferences));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Check/uncheck acts only on the visible (filtered) rows that can be restored, as in the original interface.</summary>
    void Check(bool value)
    {
        foreach (var r in _view.Cast<ServiceDiffRow>().Where(r => r.Restorable).ToList()) r.IsChecked = value;
    }

    async Task NewSnapshotAsync()
    {
        var suggested = "Custom." + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var (ok, name, full) = Dialog.Prompt(Txt.NewSnapshotTitle, Txt.NewSnapshotPrompt, suggested,
            n => ServiceSnapshots.IsValidName(n) && n != ServiceSnapshots.Current ? null : Txt.InvalidSnapshotName, Txt.FullExportOption);
        if (!ok) return;
        // A name that already exists replaces the snapshot (the previous files are kept as .previous).
        var force = name == ServiceSnapshots.Baseline && File.Exists(Path.Combine(ServiceSnapshots.Directory, name + ".json"));

        await RunAsync(Txt.Capturing, async progress =>
        {
            CaptureResult r;
            try { r = await Task.Run(() => ServiceSnapshots.Capture(name, full, force, progress)); }
            catch (UnauthorizedAccessException) when (!Elevation.IsAdmin)
            {
                HasProgress = false;
                Message = Txt.WaitingForElevation;
                await ElevatedRunner.RunAsync(new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Capture, Name = name, Full = full, Force = force } });
                return (true, Txt.SnapshotCapturedElevated(name));
            }
            return (true, r.KeptExistingBaseline ? Txt.BaselineKept : Txt.SnapshotCaptured(name, r.Count, full, r.SkippedKeys));
        });
        await RefreshAsync();
        Selected = Snapshots.FirstOrDefault(s => s.Name == name) ?? Selected;
    }

    async Task RestoreAsync()
    {
        var snap = _selected;
        var rows = _rows.Where(r => r.IsChecked && r.Restorable).ToList();
        if (snap is null || rows.Count == 0) return;
        var names = rows.Select(r => r.Name).ToList();
        List<ServiceRestoreItem>? items = null;
        string? safety = null;
        await RunAsync(Txt.Restoring, async _ =>
        {
            if (Elevation.IsAdmin)
            {
                var result = await Task.Run(() => ServiceSnapshots.Restore(snap.Name, names));
                safety = result.SafetySnapshot;
                items = result.Items.ToList();
            }
            else
            {
                Message = Txt.WaitingForElevation;
                await ElevatedRunner.RunAsync(new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Restore, Name = snap.Name, Services = names } });
            }
            // Confirmation by re-reading the Registry, service by service; the write result alone is not enough.
            var verified = await Task.Run(() => ServiceSnapshots.Verify(ServiceSnapshots.Load(snap.Name), names));
            items = verified.Select(v => items?.FirstOrDefault(i => i.Name == v.Name) is { Ok: false } failed ? failed : v).ToList();
            var ok = items.Count(i => i.Ok);
            return (ok == items.Count, Txt.RestoreSummary(ok, items.Count));
        });
        // The summary already becomes a notification; what was not restored (and the safety snapshot) goes in an error notification.
        if (items is not null && !items.All(i => i.Ok))
            Toasts.Show(Txt.RestoreReport(items.Select(i => (i.Name, i.Ok, i.Error)), safety), ToastKind.Error);
        await RefreshAsync();
    }

    async Task OrganizeAsync()
    {
        await RunAsync(Txt.Moving, async _ =>
        {
            try { return (true, Txt.LegacyMoved(await Task.Run(ServiceSnapshots.Organize))); }
            catch (UnauthorizedAccessException) when (!Elevation.IsAdmin)
            {
                await ElevatedRunner.RunAsync(new ElevatedJob { Snapshot = new SnapshotJob { Mode = SnapshotJob.Organize } });
                return (true, Txt.LegacyMovedElevated);
            }
        });
        await RefreshAsync();
    }

    void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Txt.ExportCsv,
            FileName = ServiceSnapshots.DiffFileName(_selected!.Name, ServiceSnapshots.Current),
            Filter = Txt.CsvFilter,
            InitialDirectory = Directory.Exists(ServiceSnapshots.Directory) ? ServiceSnapshots.Directory : "",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ServiceSnapshots.ExportCsv(_rows.Select(r => r.Diff), dialog.FileName);
            IsError = false;
            Message = Txt.CsvSaved(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error("CSV export failed", ex);
            IsError = true;
            Message = ex.Message;
        }
    }

    void OpenFolder()
    {
        if (!Directory.Exists(ServiceSnapshots.Directory)) { Toasts.Show(Txt.FolderNotFound(ServiceSnapshots.Directory), ToastKind.Error); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(ServiceSnapshots.Directory)) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Runs an operation with a message and, when available, real progress (number of services read).</summary>
    async Task RunAsync(string busyText, Func<IProgress<(int Done, int Total)>, Task<(bool Ok, string Message)>> op)
    {
        IsBusy = true;
        IsError = false;
        Message = busyText;
        Progress = 0;
        HasProgress = false;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            HasProgress = p.Total > 0;
            Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        });
        try
        {
            var (ok, msg) = await op(progress);
            IsError = !ok;
            Message = msg;
        }
        catch (ElevationCancelledException ex) { Message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error($"Service snapshots: {busyText} failed", ex);
            IsError = true;
            Message = ex.Message;
        }
        finally
        {
            HasProgress = false;
            IsBusy = false;
        }
    }
}
