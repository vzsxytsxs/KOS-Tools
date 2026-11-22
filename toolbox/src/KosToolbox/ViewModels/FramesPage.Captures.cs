using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Latency;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public enum FrameGraph { FrameTime, Fps, Histogram, Percentiles, Bars }

/// <summary>A saved capture in the Frames list.</summary>
public sealed class CaptureRowViewModel(SavedCapture capture) : ObservableObject
{
    bool _isPicked, _isRenaming;
    string _editName = "";
    public SavedCapture Capture { get; } = capture;
    public string Name => Capture.Name;
    public string Program => Capture.Program;
    public string Date => Capture.Date.ToString("yyyy-MM-dd HH:mm");
    public string Average => Capture.Stats is { } s ? $"{s.AverageFps:0.0} FPS" : "";
    public string Low1 => Capture.Stats is { } s ? $"{s.P1LowFps:0.0} FPS" : "";
    public bool IsPicked { get => _isPicked; set => Set(ref _isPicked, value); }
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value ?? ""); }
    public void Renamed() => Raise(nameof(Name));
}

public sealed record CompareCellViewModel(string Text, string Diff, bool Worse);
public sealed record CompareRowViewModel(string Label, IReadOnlyList<CompareCellViewModel> Cells);

/// <summary>Latency › Frames: saved captures (list, save, rename, delete, CSV, import), the graphs and the comparison.</summary>
public sealed partial class FramesViewModel
{
    FrameReport? _unsaved;
    int _unsavedSeconds;
    DateTime _unsavedDate;
    IReadOnlyList<double> _shown = Array.Empty<double>();
    IReadOnlyList<double> _shownTimes = Array.Empty<double>();
    Choice<FrameGraph> _graph = null!, _compareGraph = null!;
    string? _capturesNote;
    readonly List<CaptureRowViewModel> _tickedRows = new();
    List<SavedCapture> _compared = new();
    IReadOnlyList<CompareRowViewModel> _compareRows = Array.Empty<CompareRowViewModel>();
    IReadOnlyList<IReadOnlyList<Point>>? _compareSeries;
    IReadOnlyList<BarGroup>? _compareBars;

    static FrameStore Store => FrameStore.Default; // read each time: the UI probe moves DataRoot

    void InitCaptures()
    {
        Graphs = new Choice<FrameGraph>[] { new(Txt.FramesGraphFrameTime, FrameGraph.FrameTime), new(Txt.FramesGraphFps, FrameGraph.Fps), new(Txt.FramesGraphHistogram, FrameGraph.Histogram) };
        CompareGraphs = new Choice<FrameGraph>[]
        {
            new(Txt.FramesGraphBars, FrameGraph.Bars), new(Txt.FramesGraphFrameTime, FrameGraph.FrameTime), new(Txt.FramesGraphFps, FrameGraph.Fps),
            new(Txt.FramesGraphPercentiles, FrameGraph.Percentiles), new(Txt.FramesGraphHistogram, FrameGraph.Histogram),
        };
        (_graph, _compareGraph) = (Graphs[0], CompareGraphs[0]);
        SaveCaptureCommand = new RelayCommand(SaveUnsaved, () => _unsaved is not null);
        LoadCaptureCommand = new RelayCommand(p => { if (p is CaptureRowViewModel r) Load(r.Capture); });
        RenameCommand = new RelayCommand(p => { if (p is CaptureRowViewModel r) Rename(r); });
        DeleteCommand = new RelayCommand(p => { if (p is CaptureRowViewModel r) Delete(r); });
        ExportCommand = new RelayCommand(p => { if (p is CaptureRowViewModel r) Export(r.Capture); });
        ImportCommand = new RelayCommand(Import);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CompareCommand = new RelayCommand(() => Compare(_tickedRows.Select(r => r.Capture).ToList()), () => _tickedRows.Count is >= 2 and <= 4);
        CloseCompareCommand = new RelayCommand(() => { _compared = new(); Raise(nameof(IsComparing)); });
    }

    public ObservableCollection<CaptureRowViewModel> Captures { get; } = new();
    public bool HasCaptures => Captures.Count > 0;
    /// <summary>Damaged files left out, and how many are ticked for the comparison.</summary>
    public string? CapturesNote { get => _capturesNote; private set => Set(ref _capturesNote, value); }
    public bool CanSave => _unsaved is not null;
    public bool AutoSave
    {
        get => AppSettings.Current.SaveFramesAutomatically;
        set { AppSettings.Current.SaveFramesAutomatically = value; AppSettings.Current.Save(); Raise(); }
    }
    public ICommand SaveCaptureCommand { get; private set; } = null!;
    public ICommand LoadCaptureCommand { get; private set; } = null!;
    public ICommand RenameCommand { get; private set; } = null!;
    public ICommand DeleteCommand { get; private set; } = null!;
    public ICommand ExportCommand { get; private set; } = null!;
    public ICommand ImportCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;
    public ICommand CompareCommand { get; private set; } = null!;
    public ICommand CloseCompareCommand { get; private set; } = null!;

    // ---------- single capture graphs ----------
    public IReadOnlyList<Choice<FrameGraph>> Graphs { get; private set; } = null!;
    public Choice<FrameGraph> Graph { get => _graph; set { if (value is not null && Set(ref _graph, value)) Plot(); } }
    public PlotStyle PlotStyle => _graph.Value == FrameGraph.Histogram ? PlotStyle.Stem : PlotStyle.Lines;
    public string XTitle => Titles(_graph.Value).X;
    public string YTitle => Titles(_graph.Value).Y;

    static (string X, string Y) Titles(FrameGraph g) => g switch
    {
        FrameGraph.Fps => (Txt.FramesAxisSeconds, Txt.FramesAxisFps),
        FrameGraph.Histogram => (Txt.FramesAxisFrameTime, Txt.FramesAxisShare),
        _ => (Txt.FramesAxisSeconds, Txt.FramesAxisFrameTime),
    };

    void Plot()
    {
        Series = _shown.Count == 0 ? null : new[] { Graphed(_graph.Value, _shown, _shownTimes, null, FrameStats.BinWidth(new[] { _shown })) };
        Raise(nameof(PlotStyle));
        Raise(nameof(XTitle));
        Raise(nameof(YTitle));
    }

    static IReadOnlyList<Point> Graphed(FrameGraph g, IReadOnlyList<double> ms, IReadOnlyList<double> times, IReadOnlyList<IReadOnlyList<double>>? range, double width) => g switch
    {
        FrameGraph.Fps => FrameStats.FpsOverTime(ms).Select(p => new Point(p.Seconds, p.Fps)).ToList(),
        FrameGraph.Histogram => FrameStats.Histogram(ms, width, range).Select(b => new Point(b.Ms + width / 2, 100 * b.Share)).ToList(),
        _ => FrameStats.Thin(times, ms).Select(p => new Point(p.X, p.Y)).ToList(),
    };

    // ---------- the list ----------
    /// <summary>Reads the captures folder again (the page's tab opened, after a change).</summary>
    public void ReloadCaptures()
    {
        var (list, bad) = Store.LoadAll();
        var ticked = _tickedRows.Select(r => r.Capture.Id).ToList();
        _tickedRows.Clear();
        Captures.Clear();
        foreach (var c in list) Captures.Add(new CaptureRowViewModel(c));
        // Ticks survive a reload, in the order they were made.
        foreach (var id in ticked)
            if (Captures.FirstOrDefault(r => r.Capture.Id == id) is { } row) { row.IsPicked = true; _tickedRows.Add(row); }
        foreach (var row in Captures) Hook(row);
        _unreadable = bad;
        Raise(nameof(HasCaptures));
        UpdateNote();
    }

    IReadOnlyList<string> _unreadable = Array.Empty<string>();

    void Hook(CaptureRowViewModel row) => row.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName != nameof(CaptureRowViewModel.IsPicked)) return;
        if (row.IsPicked && !_tickedRows.Contains(row)) _tickedRows.Add(row);
        else if (!row.IsPicked) _tickedRows.Remove(row);
        UpdateNote();
        CommandManager.InvalidateRequerySuggested();
    };

    void UpdateNote() => CapturesNote = string.Join(" ", new[] { _unreadable.Count > 0 ? Txt.FramesUnreadable(_unreadable) : "", HasCaptures ? Txt.FramesPicked(_tickedRows.Count) : "" }.Where(s => s.Length > 0));

    /// <summary>After a capture: kept for Save capture, or saved right away with the automatic option on.</summary>
    void Captured(FrameReport r, int seconds)
    {
        (_unsaved, _unsavedSeconds, _unsavedDate) = (r, seconds, DateTime.Now);
        _compared = new();
        Raise(nameof(IsComparing));
        Raise(nameof(CanSave));
        if (AutoSave) SaveUnsaved();
    }

    void SaveUnsaved()
    {
        if (_unsaved is null) return;
        try
        {
            var c = FrameStore.FromReport(_unsaved, _unsavedSeconds, _unsavedDate);
            Store.Save(c);
            _unsaved = null;
            Raise(nameof(CanSave));
            Toasts.Show(Txt.FramesSaved(c.Name), ToastKind.Success);
            ReloadCaptures();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    public void Load(SavedCapture c)
    {
        _compared = new();
        Raise(nameof(IsComparing));
        var r = c.ToReport();
        Show(r);
        _unsaved = null;
        Raise(nameof(CanSave));
        Status = Txt.FramesLoaded(c.Name, c.Program, c.Date) + (r.PresentModes.Count > 0 ? " " + Status : "");
    }

    void Rename(CaptureRowViewModel row)
    {
        if (!row.IsRenaming) { row.EditName = row.Name; row.IsRenaming = true; return; }
        try
        {
            if (row.EditName.Trim() != row.Name) Store.Rename(row.Capture, row.EditName);
            row.IsRenaming = false;
            row.Renamed();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    void Delete(CaptureRowViewModel row)
    {
        try
        {
            Store.Delete(row.Capture);
            Toasts.Show(Txt.FramesDeleted(row.Name), ToastKind.Success);
            if (_compared.Any(c => c.Id == row.Capture.Id)) { _compared = new(); Raise(nameof(IsComparing)); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Toasts.Show(ex.Message, ToastKind.Error); }
        ReloadCaptures();
    }

    static void Export(SavedCapture c)
    {
        var name = string.Concat(c.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = name + ".csv", Title = Txt.FramesExport };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, FrameStore.ToCsv(c));
            Toasts.Show(Txt.FramesExported(dialog.FileName), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PresentMon CSV or capture (*.csv;*.json)|*.csv;*.json|All files (*.*)|*.*", Title = Txt.FramesImport };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var c = Store.Import(dialog.FileName);
            Toasts.Show(Txt.FramesImported(c.Name), ToastKind.Success);
            ReloadCaptures();
            Load(c);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or InvalidOperationException) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Store.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(Store.Folder)) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    // ---------- comparison ----------
    public bool IsComparing => _compared.Count >= 2;
    public IReadOnlyList<string> CompareNames => _compared.Select((c, i) => (c.Name.Length > 40 ? c.Name[..39] + "…" : c.Name) + (i == 0 ? $" ({Txt.FramesReference})" : "")).ToList();
    public IReadOnlyList<CompareRowViewModel> CompareRows { get => _compareRows; private set => Set(ref _compareRows, value); }
    public IReadOnlyList<Choice<FrameGraph>> CompareGraphs { get; private set; } = null!;
    public Choice<FrameGraph> CompareGraph { get => _compareGraph; set { if (value is not null && Set(ref _compareGraph, value)) PlotComparison(); } }
    public bool IsCompareBars => _compareGraph.Value is FrameGraph.Bars or FrameGraph.Percentiles;
    public IReadOnlyList<IReadOnlyList<Point>>? CompareSeries { get => _compareSeries; private set => Set(ref _compareSeries, value); }
    public IReadOnlyList<BarGroup>? CompareBars { get => _compareBars; private set => Set(ref _compareBars, value); }
    public string CompareXTitle => Titles(_compareGraph.Value).X;
    public string CompareYTitle => _compareGraph.Value switch { FrameGraph.Bars => Txt.FramesAxisFps, FrameGraph.Percentiles => Txt.FramesAxisFrameTime, _ => Titles(_compareGraph.Value).Y };

    /// <summary>Compares 2 to 4 captures, the first being the reference.</summary>
    public void Compare(IReadOnlyList<SavedCapture> captures)
    {
        if (captures.Count is < 2 or > 4) return;
        _compared = captures.ToList();
        var stats = _compared.Select(c => c.Stats ?? FrameStats.Of(c.FrameTimesMs)!).ToList();
        CompareRows = FrameCompare.Table(stats).Select(r => new CompareRowViewModel(r.Label, r.Cells.Select(c => new CompareCellViewModel(
            r.Unit switch { "FPS" => $"{c.Value:0.0} FPS", "ms" => $"{c.Value:0.00} ms", "%" => $"{c.Value:0.0} %", _ => $"{c.Value:N0}" },
            c.DiffPercent is { } d ? Diff(d) : r.Cells[0] == c ? Txt.FramesReference : "–", c.Worse)).ToList())).ToList();
        Raise(nameof(CompareNames));
        Raise(nameof(IsComparing));
        PlotComparison();
    }

    static string Diff(double d) => Math.Abs(d) < 0.05 ? "±0.0 %" : $"{(d > 0 ? "+" : "−")}{Math.Abs(d):0.0} %";

    void PlotComparison()
    {
        if (_compared.Count < 2) return;
        var g = _compareGraph.Value;
        var stats = _compared.Select(c => c.Stats ?? FrameStats.Of(c.FrameTimesMs)!).ToList();
        IReadOnlyList<BarValue> Bars(Func<FrameStats, double> value, string format) => stats.Select((s, i) =>
            new BarValue(value(s), value(s).ToString(format), i == 0 ? null : Diff(100 * (value(s) - value(stats[0])) / value(stats[0])))).ToList();
        if (g == FrameGraph.Bars)
            CompareBars = new[]
            {
                new BarGroup("Average", Bars(s => s.AverageFps, "0.0")), new BarGroup("1 % low", Bars(s => s.P1LowFps, "0.0")), new BarGroup("0.1 % low", Bars(s => s.P01LowFps, "0.0")),
            };
        else if (g == FrameGraph.Percentiles)
            CompareBars = new[]
            {
                new BarGroup("50 %", Bars(s => s.MedianMs, "0.00")), new BarGroup("90 %", Bars(s => s.P90Ms, "0.00")), new BarGroup("95 %", Bars(s => s.P95Ms, "0.00")),
                new BarGroup("99 %", Bars(s => s.P99Ms, "0.00")), new BarGroup("99.9 %", Bars(s => s.P999Ms, "0.00")),
            };
        else
        {
            var all = _compared.Select(c => (IReadOnlyList<double>)c.FrameTimesMs).ToList();
            var width = FrameStats.BinWidth(all);
            CompareSeries = _compared.Select(c => Graphed(g, c.FrameTimesMs, c.TimesS, all, width)).ToList();
        }
        Raise(nameof(IsCompareBars));
        Raise(nameof(CompareXTitle));
        Raise(nameof(CompareYTitle));
    }
}
