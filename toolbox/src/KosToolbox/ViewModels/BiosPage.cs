using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Bios;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.ViewModels;

/// <summary>List option of a setting. A class (reference equality): a recycled container does not carry over another row's option.</summary>
public sealed class BiosChoice(string value, string label)
{
    public string Value { get; } = value;
    public string Label { get; } = label;
}

/// <summary>A BIOS setting in the list: the value read, the pending edit and what the row shows.</summary>
public sealed class BiosSettingRow : ObservableObject
{
    readonly BiosPageViewModel _page;
    string? _pending, _error;
    string _text;

    public BiosSettingRow(BiosPageViewModel page, BiosQuestion question)
    {
        _page = page;
        Question = question;
        Choices = question.Kind switch
        {
            BiosKind.Options => question.Options.Select(o => new BiosChoice(o.Value, NvramScript.Describe(question, o.Value))).ToList(),
            BiosKind.Checkbox => new[] { new BiosChoice("0", Txt.Disabled), new BiosChoice("1", Txt.Enabled) },
            _ => Array.Empty<BiosChoice>(),
        };
        _text = question.Current ?? "";
        DiscardCommand = new RelayCommand(Discard, () => CanDiscard);
    }

    public BiosQuestion Question { get; }
    public string Name => Question.Name;
    public string? Help => Question.Help;
    public IReadOnlyList<BiosChoice> Choices { get; }
    public bool IsChoice => Choices.Count > 0;
    public bool IsNumber => Question.Kind == BiosKind.Numeric;
    public bool IsReadOnly => Question.Kind == BiosKind.ReadOnly;
    public string ReadOnlyText => NvramScript.Describe(Question, Question.Current);
    public string NumberHint => Txt.BiosNumberHint(int.TryParse(Question.Width, System.Globalization.NumberStyles.HexNumber, null, out var w) ? w : 0);
    public ICommand DiscardCommand { get; }

    /// <summary>Value to write; null = no change.</summary>
    public string? Pending => _pending;
    public bool IsPending => _pending is not null;
    public string? Value => _pending ?? Question.Current;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(CanDiscard)); } } }
    public bool HasError => _error is not null;
    public bool CanDiscard => IsPending || HasError;
    public bool IsNotDefault => Question.Editable && Question.Default is not null && !NvramScript.SameValue(Question, Value, Question.Default);

    /// <summary>"Was: …" with a pending change and "Default: …" when the BIOS reports the default.</summary>
    public string? Note
    {
        get
        {
            var parts = new List<string>();
            if (IsPending) parts.Add(Txt.BiosWas(NvramScript.Describe(Question, Question.Current)));
            if (Question.Default is not null) parts.Add(Txt.BiosDefault(NvramScript.Describe(Question, Question.Default)));
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public BiosChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => NvramScript.SameValue(Question, c.Value, Value));
        set { if (value is not null && Choices.Contains(value)) SetValue(value.Value); }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            var text = _text.Trim();
            Error = NvramScript.Check(Question, text);
            if (Error is null) SetValue(text);
            _page.UpdatePending();
        }
    }

    void SetValue(string value)
    {
        _pending = NvramScript.SameValue(Question, value, Question.Current) ? null : value;
        Raise(string.Empty);
        _page.UpdatePending();
    }

    public void Discard()
    {
        _pending = null;
        _error = null;
        _text = Question.Current ?? "";
        Raise(string.Empty);
        _page.UpdatePending();
    }
}

/// <summary>Setting that differs between a backup and the last BIOS read.</summary>
public sealed class BiosDiffRow(BiosPageViewModel page, BiosDiff diff) : ObservableObject
{
    bool _checked;
    public BiosDiff Diff { get; } = diff;
    public string Name => Diff.Name;
    public string InBackup => Diff.Old is { } q ? NvramScript.Describe(q, q.Current) : Txt.BiosNotInBackup;
    public string Current => Diff.New is { } q ? NvramScript.Describe(q, q.Current) : Txt.BiosNotInBios;
    public bool Restorable => page.CanRestoreBackup && Diff.Restorable;
    public string? Reason => Diff.Old is null ? Txt.BiosNotInBackup : Diff.New is null ? Txt.BiosNotInBios : Diff.Restorable ? null : Txt.BiosCantRestoreValue;

    public bool IsChecked
    {
        get => _checked;
        set { if (Restorable && Set(ref _checked, value)) page.UpdateChecked(); }
    }
}

/// <summary>
/// BIOS page: reads the settings with SCEWIN (nvram.txt), shows each one for editing as in the SCEWIN GUI and writes only what
/// changed, always with a backup of the in-use state first. The Backups tab compares a backup with the last read and
/// restores the checked settings through the same path as writing.
/// </summary>
public sealed class BiosPageViewModel : PageViewModel
{
    const int FewSettings = 20;
    NvramScript? _script;
    DateTime? _readAt;
    string? _loadError, _status, _scewin, _sourceProblem, _diffFor;
    List<BiosSettingRow> _rows = new();
    ListCollectionView _view = new(new List<BiosSettingRow>());
    string _search = "", _diffFilter = "", _tab = "settings";
    bool _onlyNotDefault, _onlyPending, _busy, _comparing, _loaded;
    int _pendingCount, _errorCount, _checkedCount;
    BiosBackupInfo? _selectedBackup;
    NvramScript? _backupScript;
    List<BiosDiffRow> _diffRows = new();
    ListCollectionView _diffView = new(new List<BiosDiffRow>());
    string? _compareError;

    public BiosPageViewModel() : base("bios", Txt.PageBios, "", Txt.PageBiosSubtitle)
    {
        Tabs = new[] { new GpuTab("settings", Txt.BiosTabSettings, "") { IsSelected = true }, new GpuTab("backups", Txt.BiosTabBackups, "") };
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
        ReadCommand = new AsyncCommand(ReadAsync, () => !_busy && _scewin is not null && _sourceProblem is null);
        ApplyCommand = new AsyncCommand(() => WriteAsync(_rows.Where(r => r.IsPending).ToDictionary(r => r.Question.Key, r => r.Pending!)),
            () => !_busy && _pendingCount > 0 && _errorCount == 0);
        DiscardCommand = new RelayCommand(Discard, () => !_busy && (_pendingCount > 0 || _errorCount > 0));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        OpenFolderCommand = new RelayCommand(() => OpenFolder(Scewin.Folder));
        OpenBackupsCommand = new RelayCommand(() => OpenFolder(Scewin.BackupFolder));
        OpenToolsCommand = new RelayCommand(() =>
        {
            try { ToolDiscovery.OpenFolder(); }
            catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
        });
        CheckAllCommand = new RelayCommand(() => Check(true), () => !_busy && _diffRows.Any(r => r.Restorable));
        UncheckAllCommand = new RelayCommand(() => Check(false), () => !_busy && _checkedCount > 0);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => !_busy && _checkedCount > 0);
    }

    // Drawn chip: the icon font has no suitable one.
    public override string? IconPath =>
        "M7,6 L17,6 Q18,6 18,7 L18,17 Q18,18 17,18 L7,18 Q6,18 6,17 L6,7 Q6,6 7,6 M10,10 L14,10 L14,14 L10,14 Z " +
        "M9,3 L9,6 M12,3 L12,6 M15,3 L15,6 M9,18 L9,21 M12,18 L12,21 M15,18 L15,21 M3,9 L6,9 M3,12 L6,12 M3,15 L6,15 M18,9 L21,9 M18,12 L21,12 M18,15 L21,15";
    public override bool ScrollsItself => true;

    public IReadOnlyList<GpuTab> Tabs { get; }
    public bool IsSettingsTab => _tab == "settings";
    public bool IsBackupsTab => _tab == "backups";
    public ICommand SelectTabCommand { get; }
    public ICommand ReadCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenBackupsCommand { get; }
    public ICommand OpenToolsCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand UncheckAllCommand { get; }
    public ICommand RestoreCommand { get; }

    public string Folder => Scewin.Folder;
    public string BackupFolder => Scewin.BackupFolder;
    public bool HasScewin => _scewin is not null;
    public bool IsBusy => _busy;
    /// <summary>What is running now (reading, writing); null when idle.</summary>
    public string? Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status is not null;

    // ---------- Settings tab ----------

    public ListCollectionView View { get => _view; private set => Set(ref _view, value); }
    // Before the first read of the files, no "not found" flashing.
    public string StatusText => !_loaded ? "" : _script is null || _readAt is null ? Txt.BiosNotRead : Txt.BiosStatus(_readAt.Value, _script.Questions.Count, _script.Crc, _script.Version);
    /// <summary>Card warning: SCEWIN is missing, or the BIOS exposes only a few settings to Windows.</summary>
    public string? Problem => !_loaded ? null : _scewin is null ? Txt.ScewinMissing(ToolDiscovery.Root) : _sourceProblem
        ?? (_script is { Questions.Count: < FewSettings } s ? Txt.BiosFewSettings(s.Questions.Count) : null);
    public string? EmptyText => !_loaded ? null : _loadError ?? (_script is null ? Txt.BiosNotRead : _view.Count == 0 ? Txt.BiosNoMatches : null);
    public string CountText => _script is null ? "" : Txt.BiosCount(_view.Count, _rows.Count);
    public bool HasPending => _pendingCount > 0 || _errorCount > 0;
    public string PendingText => Txt.BiosPending(_pendingCount, _errorCount);

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) RefreshView(); }
    }
    public bool OnlyNotDefault { get => _onlyNotDefault; set { if (Set(ref _onlyNotDefault, value)) RefreshView(); } }
    public bool OnlyPending { get => _onlyPending; set { if (Set(ref _onlyPending, value)) RefreshView(); } }

    // ---------- Backups tab ----------

    public ObservableCollection<BiosBackupInfo> Backups { get; } = new();
    public bool NoBackups => Backups.Count == 0;
    public ListCollectionView DiffView { get => _diffView; private set => Set(ref _diffView, value); }
    public string? CompareError { get => _compareError; private set { if (Set(ref _compareError, value)) Raise(nameof(NoDifferences)); } }
    /// <summary>The backup has the same BIOS layout as the last read (same HIICrc32).</summary>
    public bool CanRestoreBackup => _backupScript is not null && _script is not null && string.Equals(_backupScript.Crc, _script.Crc, StringComparison.OrdinalIgnoreCase);
    public bool NoDifferences => _selectedBackup is { Problem: null } && _script is not null && _compareError is null && !_comparing && _diffRows.Count == 0;
    public string DiffStats => _selectedBackup is null ? "" : Txt.BiosBackupStats(_diffRows.Count, _checkedCount, _readAt);

    public string DiffFilter
    {
        get => _diffFilter;
        set { if (Set(ref _diffFilter, value ?? "")) _diffView.Refresh(); }
    }

    public BiosBackupInfo? SelectedBackup
    {
        get => _selectedBackup;
        set { if (Set(ref _selectedBackup, value)) MainViewModel.Fire(CompareAsync()); }
    }

    // ---------- reading ----------

    public override Task RefreshAsync() => _busy ? Task.CompletedTask : LoadAsync(announceDiscard: true);

    /// <summary>
    /// Loads the nvram.txt from the last read and the list of backups. With the same file as before, the pending edits
    /// are kept; with a new file, the list is rebuilt (and a warning says how many edits were discarded).
    /// </summary>
    async Task LoadAsync(bool announceDiscard)
    {
        var (scewin, sourceProblem, script, readAt, error, backups) = await Task.Run(() =>
        {
            var exe = Scewin.Find();
            NvramScript? s = null;
            DateTime? at = null;
            string? err = null;
            try
            {
                if (File.Exists(Scewin.CurrentFile)) { at = File.GetLastWriteTime(Scewin.CurrentFile); s = NvramScript.Load(Scewin.CurrentFile); }
            }
            catch (Exception ex) { Log.Error("BIOS: reading nvram.txt failed", ex); err = Txt.ReadErrorWith(ex.Message); }
            List<BiosBackupInfo> list;
            try { list = Scewin.Backups(); }
            catch (Exception ex) { Log.Error("BIOS: listing the backups failed", ex); list = new(); }
            return (exe, exe is null ? null : Scewin.SourceProblem(exe), s, at, err, list);
        });

        _loaded = true;
        _scewin = scewin;
        _sourceProblem = sourceProblem;
        _loadError = error;
        if (script is null || readAt != _readAt || _script is null)
        {
            var discarded = _pendingCount;
            _script = script;
            _readAt = readAt;
            _rows = script?.Questions.Select(q => new BiosSettingRow(this, q)).ToList() ?? new();
            View = new ListCollectionView(_rows) { Filter = Matches };
            UpdatePending();
            if (announceDiscard && discarded > 0) Toasts.Show(Txt.PendingDiscarded(discarded), ToastKind.Info);
        }
        foreach (var p in new[] { nameof(HasScewin), nameof(StatusText), nameof(Problem), nameof(EmptyText), nameof(CountText) }) Raise(p);

        var keep = _selectedBackup?.Name;
        Backups.Clear();
        foreach (var b in backups) Backups.Add(b);
        Raise(nameof(NoBackups));
        _selectedBackup = null;
        SelectedBackup = Backups.FirstOrDefault(b => b.Name == keep) ?? Backups.FirstOrDefault();
        CommandManager.InvalidateRequerySuggested();
    }

    bool Matches(object o)
    {
        var r = (BiosSettingRow)o;
        if (_onlyPending && !r.IsPending) return false;
        if (_onlyNotDefault && !r.IsNotDefault) return false;
        var q = _search.Trim();
        return q.Length == 0 || MainViewModel.Matches(r.Name, q) || r.Help is { } h && MainViewModel.Matches(h, q);
    }

    void RefreshView()
    {
        _view.Refresh();
        Raise(nameof(EmptyText));
        Raise(nameof(CountText));
    }

    internal void UpdatePending()
    {
        _pendingCount = _rows.Count(r => r.IsPending);
        _errorCount = _rows.Count(r => r.HasError);
        Raise(nameof(HasPending));
        Raise(nameof(PendingText));
        CommandManager.InvalidateRequerySuggested();
    }

    void Discard()
    {
        foreach (var r in _rows.Where(r => r.CanDiscard)) r.Discard();
        UpdatePending();
    }

    void SelectTab(string key)
    {
        if (key == _tab) return;
        _tab = key;
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        Raise(nameof(IsSettingsTab));
        Raise(nameof(IsBackupsTab));
    }

    // ---------- SCEWIN ----------

    async Task ReadAsync()
    {
        var result = await RunJobAsync(new BiosJob { Mode = BiosJob.Read }, Txt.BiosReading);
        if (result is null) return;
        await LoadAsync(announceDiscard: true);
        if (result.Error is not null) Toasts.Show(result.Error, ToastKind.Error);
        else if (_script is not null) Toasts.Show(Txt.BiosReadDone(_script.Questions.Count), ToastKind.Success);
    }

    async Task RestoreAsync()
    {
        if (_pendingCount > 0) { Toasts.Show(Txt.BiosPendingFirst, ToastKind.Warning); return; }
        await WriteAsync(_diffRows.Where(r => r.IsChecked && r.Restorable).ToDictionary(r => r.Diff.Key, r => r.Diff.Old!.Current!));
    }

    /// <summary>
    /// Writes through the elevated process: it re-reads the BIOS, saves the backup, imports only these questions and re-reads. Here each
    /// question is checked against the nvram.txt of that last read; only what is confirmed counts as written.
    /// </summary>
    async Task WriteAsync(Dictionary<string, string> changes)
    {
        if (_script is not { } script || changes.Count == 0) return;
        var names = changes.Keys.ToDictionary(k => k, k => script.Find(k)?.Name ?? k);
        var result = await RunJobAsync(new BiosJob
        {
            Mode = BiosJob.Apply, Crc = script.Crc, Changes = changes.Select(c => new BiosChange { Key = c.Key, Value = c.Value }).ToList(),
        }, Txt.BiosWriting);
        if (result is null) return;
        await LoadAsync(announceDiscard: false);
        if (result.Error is not null)
        {
            Toasts.Show(result.Error + (result.Backup is null ? "" : " " + Txt.BiosBackupKept(result.Backup)), ToastKind.Error);
            return;
        }
        var failed = changes.Where(c => _script?.Find(c.Key) is not { } q || !NvramScript.SameValue(q, q.Current, c.Value)).Select(c => names[c.Key]).ToList();
        var ok = changes.Count - failed.Count;
        if (ok > 0)
        {
            TweakEngine.Instance.Pending.Mark(Txt.BiosPendingName);
            Toasts.Show(Txt.BiosApplied(ok, changes.Count, result.Backup), ToastKind.Warning); // warning: only takes effect after a restart
        }
        if (failed.Count > 0) Toasts.Show((ok == 0 ? Txt.BiosNothingWritten + " " : "") + Txt.BiosNotApplied(failed, result.Messages), ToastKind.Error);
    }

    /// <summary>
    /// Runs as administrator: directly, if the toolbox already is, or through the elevated process (UAC), which returns the result in a
    /// file in the protected folder. Null = UAC cancelled or error already reported.
    /// </summary>
    async Task<BiosResult?> RunJobAsync(BiosJob job, string busyText)
    {
        job.RunId = Guid.NewGuid().ToString();
        SetBusy(true, busyText);
        try
        {
            return await TweakEngine.Instance.RunExclusiveAsync(async () =>
            {
                if (Elevation.IsAdmin) return await Task.Run(() => Scewin.RunAsync(job));
                await ElevatedRunner.RunAsync(new ElevatedJob { Bios = job });
                return Scewin.LoadResult(job.RunId) ?? new BiosResult { Error = Txt.BiosNoResult(Log.Directory) };
            });
        }
        catch (ElevationCancelledException ex) { Toasts.Show(ex.Message, ToastKind.Info); return null; }
        catch (Exception ex)
        {
            Log.Error($"BIOS {job.Mode} failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
            return null;
        }
        finally { SetBusy(false, null); }
    }

    void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        Status = status;
        Raise(nameof(IsBusy));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- backups ----------

    async Task CompareAsync()
    {
        var backup = _selectedBackup;
        var current = _script;
        // When the same backup is reloaded (F5, returning to the page), the checked items stay.
        var keep = backup?.Name == _diffFor ? _diffRows.Where(r => r.IsChecked).Select(r => r.Diff.Key).ToHashSet() : new HashSet<string>();
        _diffFor = backup?.Name;
        _backupScript = null;
        _comparing = backup is { Problem: null } && current is not null;
        SetDiffRows(new());
        CompareError = backup?.Problem ?? (backup is not null && current is null ? Txt.BiosCompareNeedsRead : null);
        if (!_comparing) return;
        try
        {
            var script = await Task.Run(() => Scewin.LoadBackup(backup!.Name));
            if (_selectedBackup != backup || _script != current) return;
            _backupScript = script;
            _comparing = false;
            CompareError = CanRestoreBackup ? null : Txt.BiosOtherLayout(script.Crc, current!.Crc);
            var rows = NvramScript.Compare(script, current!).Select(d => new BiosDiffRow(this, d)).ToList();
            foreach (var r in rows) if (keep.Contains(r.Diff.Key)) r.IsChecked = true;
            SetDiffRows(rows);
        }
        catch (Exception ex)
        {
            Log.Error($"BIOS: comparing backup {backup!.Name} failed", ex);
            if (_selectedBackup != backup) return;
            _comparing = false;
            CompareError = ex.Message;
        }
    }

    void SetDiffRows(List<BiosDiffRow> rows)
    {
        _diffRows = rows;
        DiffView = new ListCollectionView(rows) { Filter = o => _diffFilter.Trim().Length == 0 || MainViewModel.Matches(((BiosDiffRow)o).Name, _diffFilter.Trim()) };
        UpdateChecked();
    }

    internal void UpdateChecked()
    {
        _checkedCount = _diffRows.Count(r => r.IsChecked);
        Raise(nameof(DiffStats));
        Raise(nameof(NoDifferences));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Check/uncheck acts on the visible (filtered) rows that can be restored, as on the Services page.</summary>
    void Check(bool value)
    {
        foreach (var r in _diffView.Cast<BiosDiffRow>().Where(r => r.Restorable).ToList()) r.IsChecked = value;
    }

    static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) { Toasts.Show(Txt.FolderNotFound(path), ToastKind.Error); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(path)) { UseShellExecute = true })?.Dispose();
    }
}
