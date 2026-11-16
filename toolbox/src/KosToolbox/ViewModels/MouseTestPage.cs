using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Latency;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed record Choice<T>(string Label, T Value);

/// <summary>
/// Latency › Mouse, like MouseTester: Measure (CPI from a known distance), Collect (while the left button is held), Log (until
/// F2 or Stop), the graphs, the interval statistics and CSV. Raw input is registered only while waiting or recording.
/// </summary>
public sealed class MouseTestViewModel : ObservableObject
{
    enum Mode { Idle, ArmedCollect, ArmedMeasure, Collecting, Measuring, Logging }

    readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(250) };
    List<MouseSample> _samples = new();
    RawMouse? _raw;
    Mode _mode;
    long _start;
    double? _cpi;
    string _distance = "10", _status = Txt.MouseIdle;
    Choice<MouseGraph> _graph;
    Choice<PlotStyle> _style;
    IReadOnlyList<IReadOnlyList<Point>>? _series;
    IReadOnlyList<InfoRow> _stats = Array.Empty<InfoRow>();

    public MouseTestViewModel()
    {
        Graphs = new Choice<MouseGraph>[]
        {
            new("Interval vs. time", MouseGraph.Interval), new("Frequency vs. time", MouseGraph.Frequency),
            new("X counts vs. time", MouseGraph.XCounts), new("Y counts vs. time", MouseGraph.YCounts), new("X and Y counts vs. time", MouseGraph.XYCounts),
            new("X velocity vs. time", MouseGraph.XVelocity), new("Y velocity vs. time", MouseGraph.YVelocity), new("X and Y velocity vs. time", MouseGraph.XYVelocity),
            new("X vs. Y (path)", MouseGraph.XvsY),
        };
        Styles = new Choice<PlotStyle>[] { new("Dots", PlotStyle.Dots), new("Lines", PlotStyle.Lines), new("Stems", PlotStyle.Stem) };
        _graph = Graphs[0];
        _style = Styles[0];
        MeasureCommand = new RelayCommand(() => Arm(Mode.ArmedMeasure), () => _mode == Mode.Idle);
        CollectCommand = new RelayCommand(() => Arm(Mode.ArmedCollect), () => _mode == Mode.Idle);
        LogCommand = new RelayCommand(() => { if (_mode == Mode.Logging) Finish(); else Arm(Mode.Logging); }, () => _mode is Mode.Idle or Mode.Logging);
        CancelCommand = new RelayCommand(Stop, () => _mode is Mode.ArmedCollect or Mode.ArmedMeasure);
        SaveCommand = new RelayCommand(Save, () => _mode == Mode.Idle && _samples.Count > 0);
        LoadCommand = new RelayCommand(Load, () => _mode == Mode.Idle);
        _live.Tick += (_, _) => ShowStats();
    }

    public IReadOnlyList<Choice<MouseGraph>> Graphs { get; }
    public IReadOnlyList<Choice<PlotStyle>> Styles { get; }
    public ICommand MeasureCommand { get; }
    public ICommand CollectCommand { get; }
    public ICommand LogCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand LoadCommand { get; }
    public string Distance { get => _distance; set => Set(ref _distance, value ?? ""); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LogText => _mode == Mode.Logging ? Txt.MouseLogStop : Txt.MouseLog;
    public string Cpi => _cpi is { } c ? c.ToString("0", CultureInfo.CurrentCulture) : "—";
    public IReadOnlyList<InfoRow> Stats { get => _stats; private set => Set(ref _stats, value); }
    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => _series; private set => Set(ref _series, value); }
    public PlotStyle PlotStyle => _style.Value;
    public string XTitle => _graph.Value == MouseGraph.XvsY ? "X (counts)" : "Time (ms)";
    public string YTitle => _graph.Value switch
    {
        MouseGraph.Interval => "Interval (ms)",
        MouseGraph.Frequency => "Frequency (Hz)",
        MouseGraph.XVelocity or MouseGraph.YVelocity or MouseGraph.XYVelocity => _cpi is null ? "Velocity (counts/ms)" : "Velocity (m/s)",
        MouseGraph.XvsY => "Y (counts)",
        _ => "Counts",
    };

    public Choice<MouseGraph> Graph { get => _graph; set { if (value is not null && Set(ref _graph, value)) Plot(); } }
    public Choice<PlotStyle> Style { get => _style; set { if (value is not null && Set(ref _style, value)) Raise(nameof(PlotStyle)); } }

    void Arm(Mode mode)
    {
        if (mode == Mode.ArmedMeasure && !(double.TryParse(_distance, NumberStyles.Float, CultureInfo.CurrentCulture, out var cm) && cm is > 0 and <= 200))
        {
            Status = Txt.MouseDistanceInvalid;
            return;
        }
        try
        {
            _raw = new RawMouse(Application.Current.MainWindow!);
            _raw.Report += OnReport;
            _raw.F2 += () => { if (_mode == Mode.Logging) Finish(); };
        }
        catch (Exception ex)
        {
            Log.Warn($"Mouse test: raw input not registered: {ex.Message}");
            Status = ex.Message;
            return;
        }
        _mode = mode;
        if (mode == Mode.Logging) Begin();
        Status = mode switch { Mode.ArmedCollect => Txt.MouseArmedCollect, Mode.ArmedMeasure => Txt.MouseArmedMeasure(_distance), _ => Txt.MouseLogging };
        Changed();
    }

    void Begin()
    {
        _samples = new List<MouseSample>(16384);
        _start = 0;
        _live.Start();
    }

    const ushort LeftDown = 0x0001, LeftUp = 0x0002;

    void OnReport(long stamp, int dx, int dy, ushort buttons)
    {
        if ((buttons & LeftDown) != 0 && _mode is Mode.ArmedCollect or Mode.ArmedMeasure)
        {
            _mode = _mode == Mode.ArmedCollect ? Mode.Collecting : Mode.Measuring;
            Begin();
            Status = _mode == Mode.Measuring ? Txt.MouseMeasuring : Txt.MouseCollecting;
        }
        if (_mode is Mode.Collecting or Mode.Measuring or Mode.Logging && (dx != 0 || dy != 0))
        {
            if (_samples.Count == 0) _start = stamp;
            _samples.Add(new MouseSample((stamp - _start) * 1000.0 / Stopwatch.Frequency, dx, dy));
        }
        if ((buttons & LeftUp) != 0 && _mode is Mode.Collecting or Mode.Measuring) Finish();
    }

    void Finish()
    {
        var measured = _mode == Mode.Measuring;
        Stop();
        if (measured && double.TryParse(_distance, NumberStyles.Float, CultureInfo.CurrentCulture, out var cm))
        {
            _cpi = MouseTest.Cpi(_samples, cm);
            Raise(nameof(Cpi));
        }
        Status = _samples.Count < 2 ? Txt.MouseNothing : measured ? Txt.MouseMeasured(_cpi ?? 0) : Txt.MouseDone(_samples.Count);
        ShowStats();
        Plot();
    }

    /// <summary>Stops listening (page hidden, Cancel, end of a recording).</summary>
    public void Stop()
    {
        _live.Stop();
        _raw?.Dispose();
        _raw = null;
        if (_mode is Mode.ArmedCollect or Mode.ArmedMeasure) Status = Txt.MouseIdle;
        _mode = Mode.Idle;
        Changed();
    }

    void Changed()
    {
        Raise(nameof(LogText));
        CommandManager.InvalidateRequerySuggested();
    }

    void ShowStats()
    {
        if (MouseTest.Stats(_samples) is not { } s) { Stats = Array.Empty<InfoRow>(); return; }
        var ms = CultureInfo.CurrentCulture;
        Stats = new InfoRow[]
        {
            new("Report rate", $"{s.RateHz:N0} Hz"), new("Stable intervals", $"{s.StablePercent:0.0} % within 10 % of the median"),
            new("Median interval", $"{s.MedianMs.ToString("0.000", ms)} ms"), new("Average interval", $"{s.MeanMs.ToString("0.000", ms)} ms"),
            new("Standard deviation", $"{s.StdDevMs.ToString("0.000", ms)} ms"), new("Shortest / longest", $"{s.MinMs.ToString("0.000", ms)} / {s.MaxMs.ToString("0.000", ms)} ms"),
            new("Reports", $"{s.Events:N0} in {s.DurationMs / 1000:0.00} s"), new("Sum X / Y", $"{s.SumX:N0} / {s.SumY:N0} counts"),
            new("Path", $"{s.Path:N0} counts"), new("CPI", Cpi),
        };
    }

    void Plot()
    {
        Series = MouseTest.Series(_samples, _graph.Value, _cpi).Select(s => (IReadOnlyList<Point>)s.Select(p => new Point(p.X, p.Y)).ToList()).ToList();
        Raise(nameof(XTitle));
        Raise(nameof(YTitle));
    }

    void Save()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"mouse-{DateTime.Now:yyyyMMdd-HHmmss}.csv", Title = Txt.MouseSave };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, MouseTest.ToCsv(_samples, _cpi));
            Toasts.Show(Txt.MouseSaved(dialog.FileName), ToastKind.Success);
        }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    /// <summary>A recording from a file (or the UI probe's sample).</summary>
    public void Show(List<MouseSample> samples, double? cpi, string source)
    {
        _samples = samples;
        _cpi = cpi ?? _cpi;
        Raise(nameof(Cpi));
        Status = Txt.MouseLoaded(samples.Count, source);
        ShowStats();
        Plot();
        CommandManager.InvalidateRequerySuggested();
    }

    void Load()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*", Title = Txt.MouseLoad };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var (samples, cpi) = MouseTest.FromCsv(File.ReadAllText(dialog.FileName));
            Show(samples, cpi, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { Toasts.Show(ex.Message, ToastKind.Error); }
    }
}

/// <summary>Latency › Timer: Sleep(1) precision at the resolution Windows gives now, or with a resolution this run asks for.</summary>
public sealed class TimerBenchViewModel : ObservableObject
{
    bool _running;
    Choice<uint> _request;
    Choice<int> _count;
    IReadOnlyList<IReadOnlyList<Point>>? _series;

    public TimerBenchViewModel()
    {
        Requests = new[] { new Choice<uint>(Txt.TimerAsIs, 0) }
            .Concat(new uint[] { 5000, 5040, 5070, 5100, 5200, 5500, 6000, 7500, 10000 }.Select(v => new Choice<uint>($"{v / 10000.0:0.0###} ms", v))).ToList();
        Counts = new[] { 100, 200, 500, 1000 }.Select(n => new Choice<int>($"{n} × Sleep(1)", n)).ToList();
        _request = Requests[0];
        _count = Counts[1];
        RunCommand = new AsyncCommand(RunAsync, () => !_running);
    }

    public IReadOnlyList<Choice<uint>> Requests { get; }
    public IReadOnlyList<Choice<int>> Counts { get; }
    public Choice<uint> Request { get => _request; set => Set(ref _request, value ?? _request); }
    public Choice<int> Count { get => _count; set => Set(ref _count, value ?? _count); }
    public ICommand RunCommand { get; }
    public System.Collections.ObjectModel.ObservableCollection<InfoRow> Runs { get; } = new();
    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => _series; private set => Set(ref _series, value); }
    public void Refresh() => Raise(nameof(Resolution));
    public string Resolution => TimerBench.Query() is { } q ? Txt.TimerNow(q.CurrentMs, q.FinestMs, q.CoarsestMs) : "";

    async Task RunAsync()
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var (request, count) = (_request, _count.Value);
            var run = await Task.Factory.StartNew(() => TimerBench.Run(request.Value, count, CancellationToken.None), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Runs.Insert(0, new InfoRow(Txt.TimerRunLabel(request.Label, run.ResolutionMs), Txt.TimerRunValue(run.MeanMs, run.MinMs, run.MaxMs, run.StdDevMs)));
            Series = new[] { (IReadOnlyList<Point>)run.Samples.Select((v, i) => new Point(i + 1, v)).ToList() };
            Log.Info($"Timer: Sleep(1) × {count} with {request.Label}: mean {run.MeanMs:0.0000} ms, max {run.MaxMs:0.0000} ms, resolution {run.ResolutionMs:0.####} ms.");
        }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
        finally
        {
            _running = false;
            Raise(nameof(Resolution));
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
