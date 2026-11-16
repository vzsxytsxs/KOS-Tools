using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Storage;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class FolderRowViewModel(DirNode node, long parentSize)
{
    public DirNode Node { get; } = node;
    public string Name => Node.Name;
    public string Size => Txt.Size(Node.Size);
    public double Share => parentSize == 0 ? 0 : 100.0 * Node.Size / parentSize;
    public string ShareText => $"{Share:0.0} %";
    public string Files => Node.Files.ToString("N0");
}

public sealed class LargeFileRowViewModel(LargeFile file)
{
    public string Path => file.Path;
    public string Name => System.IO.Path.GetFileName(file.Path);
    public string Folder => System.IO.Path.GetDirectoryName(file.Path) ?? "";
    public string Size => Txt.Size(file.Size);
}

public sealed class JunkRowViewModel(JunkCategory category) : ObservableObject
{
    bool _checked = category.DefaultChecked;
    JunkMeasure? _measure;
    public JunkCategory Category { get; } = category;
    public string Name => Category.Name;
    public string Description => Category.Description;
    public bool NeedsAdmin => Category.MachineWide;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public JunkMeasure? Measure { get => _measure; set { Set(ref _measure, value); Raise(nameof(SizeText)); } }
    public string SizeText => _measure is { } m ? Txt.CleanupSize(m.Bytes, m.Files, m.Complete) : Txt.SizeNotMeasured;
}

/// <summary>
/// Storage: size of each folder (read-only) and cleanup of
/// disposable files by category, with confirmation and a before/after measurement.
/// </summary>
public sealed class StoragePageViewModel : PageViewModel
{
    const string DiskTab = "disk", CleanupTab = "cleanup";
    IReadOnlyList<OptionItem> _drives = Array.Empty<OptionItem>();
    OptionItem? _drive;
    DirNode? _current;
    IReadOnlyList<FolderRowViewModel> _folders = Array.Empty<FolderRowViewModel>();
    IReadOnlyList<LargeFileRowViewModel> _largest = Array.Empty<LargeFileRowViewModel>();
    FolderRowViewModel? _selectedFolder;
    LargeFileRowViewModel? _selectedFile;
    CancellationTokenSource? _cts;
    string? _progress, _message;
    bool _isError, _busy;

    public StoragePageViewModel() : base("storage", Txt.PageStorage, "", Txt.PageStorageSubtitle)
    {
        Tabs = new[] { new OptionItem(DiskTab, Txt.TabDiskUsage) { IsSelected = true }, new OptionItem(CleanupTab, Txt.TabCleanup) };
        SelectTabCommand = new RelayCommand(p =>
        {
            foreach (var t in Tabs) t.IsSelected = t.Key == (string)p!;
            Raise(nameof(ShowDisk));
            Raise(nameof(ShowCleanup));
        });
        Junk = JunkCleaner.Categories.Select(c => new JunkRowViewModel(c)).ToList();
        ScanCommand = new AsyncCommand(ScanAsync, () => !_busy && _drive is not null);
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !_busy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _cts is not null);
        UpCommand = new RelayCommand(() => Show(_current?.Parent), () => _current?.Parent is not null);
        OpenFolderCommand = new RelayCommand(p => { if ((p ?? _selectedFolder) is FolderRowViewModel r && r.Node.Children is not null) Show(r.Node); },
            p => (p ?? _selectedFolder) is FolderRowViewModel { Node.Children: not null });
        ShowFolderCommand = new RelayCommand(() => Reveal((_selectedFolder?.Node ?? _current)!.Path, file: false), () => _current is not null);
        ShowFileCommand = new RelayCommand(p => { if ((p ?? _selectedFile) is LargeFileRowViewModel f) Reveal(f.Path, file: true); },
            p => (p ?? _selectedFile) is LargeFileRowViewModel);
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync, () => !_busy);
        CleanCommand = new AsyncCommand(CleanAsync, () => !_busy);
        CompressCommand = new AsyncCommand(() => CompressAsync(_algorithm), () => !_busy && _current is not null);
        UncompressCommand = new AsyncCommand(() => CompressAsync(null), () => !_busy && _current is not null);
    }

    // ---------- compression ----------
    string _algorithm = "XPRESS8K";
    public IReadOnlyList<string> Algorithms => FolderCompression.Algorithms;
    public string Algorithm { get => _algorithm; set { if (value is not null) Set(ref _algorithm, value); } }
    public ICommand CompressCommand { get; }
    public ICommand UncompressCommand { get; }

    /// <summary>The selected folder (or the one shown), compressed with the chosen algorithm or uncompressed; sizes on disk before and after.</summary>
    async Task CompressAsync(string? algorithm)
    {
        if ((_selectedFolder?.Node ?? _current)?.Path is not { } folder) return;
        if (FolderCompression.Problem(folder) is { } problem) { Report($"{folder}: {problem}", true); return; }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        Report($"{name}: {(algorithm is null ? "uncompressing" : $"compressing with {algorithm}")}… (it can take several minutes; the game must be closed)", false);
        try
        {
            var progress = new Progress<int>(n => ProgressText = $"{n:N0} files processed");
            var (before, after, code) = await FolderCompression.RunAsync(folder, algorithm, progress);
            var saved = before.OnDisk - after.OnDisk;
            Report($"{name}: {Txt.Size(before.OnDisk)} on disk before, {Txt.Size(after.OnDisk)} now" +
                (saved > 0 ? $" ({Txt.Size(saved)} smaller, {100.0 * saved / Math.Max(1, before.OnDisk):0} %)." : saved < 0 ? $" ({Txt.Size(-saved)} larger)." : ".") +
                (code != 0 ? $" compact ended with code {code}: files in use or without access were left as they were." : ""), code != 0 && saved == 0);
            if (algorithm is not null && await Task.Run(() => FolderCompression.UsesDirectStorage(folder)))
                Toasts.Show($"{name} uses DirectStorage: compressed files can't use BypassIO, so loading can get slower. Uncompress it if loading suffers.", ToastKind.Warning);
            if (algorithm is not null) Toasts.Show("A game update rewrites the files it changes uncompressed: compress the folder again after updates.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error($"Compression of {folder} failed", ex);
            Report(ex.Message, true);
        }
        finally
        {
            _busy = false;
            ProgressText = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public override bool ScrollsItself => true;
    public IReadOnlyList<OptionItem> Tabs { get; }
    public bool ShowDisk => Tabs[0].IsSelected;
    public bool ShowCleanup => Tabs[1].IsSelected;
    public ICommand SelectTabCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ShowFolderCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand CleanCommand { get; }

    public IReadOnlyList<OptionItem> Drives { get => _drives; private set => Set(ref _drives, value); }
    public OptionItem? SelectedDrive { get => _drive; set { if (value is not null) Set(ref _drive, value); } }
    public IReadOnlyList<FolderRowViewModel> Folders { get => _folders; private set => Set(ref _folders, value); }
    public IReadOnlyList<LargeFileRowViewModel> Largest { get => _largest; private set => Set(ref _largest, value); }
    public FolderRowViewModel? SelectedFolder { get => _selectedFolder; set => Set(ref _selectedFolder, value); }
    public LargeFileRowViewModel? SelectedFile { get => _selectedFile; set => Set(ref _selectedFile, value); }
    public string? CurrentText => _current is { } c ? Txt.FolderSummary(c.Path, c.Size, c.Files) : null;
    public bool HasResult => _current is not null;
    public bool IsScanning => _cts is not null;
    public string? ProgressText { get => _progress; private set => Set(ref _progress, value); }
    public IReadOnlyList<JunkRowViewModel> Junk { get; }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public override Task RefreshAsync()
    {
        var keep = _drive?.Key;
        Drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => new OptionItem(d.RootDirectory.FullName, Txt.DriveLabel(d.Name, d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize)))
            .ToList();
        SelectedDrive = _drives.FirstOrDefault(d => d.Key == keep) ?? _drives.FirstOrDefault(d => d.Key.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase)) ?? _drives.FirstOrDefault();
        return Task.CompletedTask;
    }

    // ---------- disk usage ----------
    void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Txt.ChooseFolder };
        if (dialog.ShowDialog() != true) return;
        var item = new OptionItem(dialog.FolderName, dialog.FolderName);
        Drives = _drives.Where(d => d.Key != item.Key).Append(item).ToList();
        SelectedDrive = item;
        MainViewModel.Fire(ScanAsync());
    }

    async Task ScanAsync()
    {
        if (_drive?.Key is not { } root || _cts is not null) return;
        using var cts = _cts = new CancellationTokenSource();
        _busy = true;
        Raise(nameof(IsScanning));
        Report(null, false);
        CommandManager.InvalidateRequerySuggested();
        var progress = new Progress<(long Files, string Folder)>(p => { if (_cts is not null) ProgressText = Txt.Scanning(p.Files, p.Folder); });
        var clock = Stopwatch.StartNew();
        try
        {
            var r = await Task.Run(() => DiskScanner.Scan(root, progress, cts.Token));
            Largest = r.Largest.Select(f => new LargeFileRowViewModel(f)).ToList();
            Show(r.Root);
            Report(Txt.ScanDone(r.Files, r.Root.Size, r.Skipped, clock.Elapsed), false);
        }
        catch (OperationCanceledException) { Report(Txt.ScanCancelled, false); }
        catch (Exception ex)
        {
            Log.Error($"Disk scan of {root} failed", ex);
            Report(Txt.ScanFailed(ex.Message), true);
        }
        finally
        {
            _cts = null;
            _busy = false;
            ProgressText = null;
            Raise(nameof(IsScanning));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    void Show(DirNode? node)
    {
        if (node is null) return;
        _current = node;
        Folders = node.Children?.Select(c => new FolderRowViewModel(c, node.Size)).ToList() ?? (IReadOnlyList<FolderRowViewModel>)Array.Empty<FolderRowViewModel>();
        Raise(nameof(CurrentText));
        Raise(nameof(HasResult));
        CommandManager.InvalidateRequerySuggested();
    }

    static void Reveal(string path, bool file)
    {
        try
        {
            var args = file ? "/select," + ProcessRunner.Quote(path) : ProcessRunner.Quote(path);
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    // ---------- cleanup ----------
    async Task AnalyzeAsync()
    {
        _busy = true;
        Report(Txt.Measuring, false);
        CommandManager.InvalidateRequerySuggested();
        try { await MeasureAsync(Junk); Report(null, false); }
        catch (Exception ex)
        {
            Log.Error("Measuring junk files failed", ex);
            Report(ex.Message, true);
        }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }

    static async Task MeasureAsync(IEnumerable<JunkRowViewModel> rows)
    {
        foreach (var row in rows) row.Measure = await Task.Run(() => JunkCleaner.Measure(row.Category));
    }

    async Task CleanAsync()
    {
        var rows = Junk.Where(r => r.IsChecked).ToList();
        if (rows.Count == 0) { Report(Txt.CleanupNothingSelected, false); return; }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            Report(Txt.Measuring, false);
            await MeasureAsync(rows);
            var before = rows.ToDictionary(r => r, r => r.Measure!);
            var machine = rows.Where(r => r.NeedsAdmin).Select(r => r.Category.Id).ToList();
            Report(Txt.Cleaning, false);
            string? error = null;
            try
            {
                await Task.Run(() => { foreach (var r in rows.Where(r => !r.NeedsAdmin)) JunkCleaner.Clean(r.Category); });
                if (machine.Count > 0) await JunkCleaner.CleanMachineAsync(machine);
            }
            catch (ElevationCancelledException ex) { error = ex.Message; }
            catch (Exception ex)
            {
                Log.Error("Cleanup failed", ex);
                error = Txt.CleanupFailed(ex.Message);
            }
            // The result shown comes from a fresh measurement, not from what the cleanup claims to have done.
            await MeasureAsync(rows);
            var freed = rows.Sum(r => Math.Max(0, before[r].Bytes - r.Measure!.Bytes));
            var left = rows.Sum(r => r.Measure!.Files);
            Report(error is null ? Txt.CleanupDone(freed, left) : error + " " + Txt.CleanupDone(freed, left), error is not null);
        }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
}
