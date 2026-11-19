using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>Axis tick of a chart: text and position in pixels.</summary>
public sealed record ChartTick(string Label, double Pos);

/// <summary>An editable point of the curve (text, validated on start); draggable on the chart.</summary>
public sealed class FanPointViewModel(int temp, int percent, GpuPageViewModel owner) : ObservableObject, IChartPoint
{
    string _temp = temp.ToString(), _percent = percent.ToString(), _label = "";
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Temp { get => _temp; set { if (Set(ref _temp, value ?? "")) { Raise(nameof(PlotLeft)); owner.CurveChanged(); } } }
    public string Percent { get => _percent; set { if (Set(ref _percent, value ?? "")) { Raise(nameof(PlotTop)); owner.CurveChanged(); } } }
    public double PlotLeft => int.TryParse(_temp, out var t) ? owner.FanX(t) - GpuPageViewModel.DotRadius : -50;
    public double PlotTop => int.TryParse(_percent, out var p) ? owner.FanY(p) - GpuPageViewModel.DotRadius : -50;
    public void Relayout() { Raise(nameof(PlotLeft)); Raise(nameof(PlotTop)); }
    public void DragStart() { }
    public void DragTo(double x, double y) => owner.DragFanPoint(this, x, y);
}

/// <summary>Fan curve of the GPU page: point editor, preview and the controller that runs inside the toolbox.</summary>
public sealed partial class GpuPageViewModel
{
    internal const double DotRadius = 7;
    const double FanMinTemp = 20, FanMaxTemp = 100;
    FanCurveSettings _curve = new();
    string _hysteresis = "3";
    string? _curveError;
    Size _fanPlot = new(600, 220);

    public ObservableCollection<FanPointViewModel> CurvePoints { get; } = new();
    /// <summary>The 8 columns of the table (P1–P8); those without a point yet appear empty.</summary>
    public IReadOnlyList<string> CurveSlots { get; } = Enumerable.Range(1, FanCurve.MaxPoints).Select(i => $"P{i}").ToList();

    /// <summary>Real size of the chart area, reported by the view.</summary>
    public Size FanPlotSize
    {
        get => _fanPlot;
        set
        {
            if (value.IsEmpty || value.Width < 1 || value.Height < 1 || value == _fanPlot) return;
            _fanPlot = value;
            foreach (var p in CurvePoints) p.Relayout();
            Raise(nameof(FanPlotWidth));
            Raise(nameof(FanPlotHeight));
            Raise(nameof(FanXTicks));
            Raise(nameof(FanYTicks));
            Raise(nameof(CurvePreview));
            Raise(nameof(CurveFill));
            Raise(nameof(TemperatureX));
        }
    }
    public double FanPlotWidth => _fanPlot.Width;
    public double FanPlotHeight => _fanPlot.Height;
    public IReadOnlyList<ChartTick> FanXTicks => new[] { 20, 40, 60, 80, 100 }.Select(t => new ChartTick($"{t}°C", FanX(t))).ToList();
    public IReadOnlyList<ChartTick> FanYTicks => new[] { 0, 25, 50, 75, 100 }.Select(p => new ChartTick($"{p}%", FanY(p))).ToList();
    public ICommand AddPointCommand { get; private set; } = null!;
    public ICommand RemovePointCommand { get; private set; } = null!;
    readonly System.Windows.Threading.DispatcherTimer _curveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public string Hysteresis { get => _hysteresis; set { if (Set(ref _hysteresis, value ?? "")) CurveChanged(); } }
    public string? CurveError { get => _curveError; private set => Set(ref _curveError, value); }
    public bool CurveNeedsAdmin => !Elevation.IsAdmin;

    // The curve runs in KOS Engine: the page reads the saved request and the state the engine reports.
    GovernorFanCurve? _governorCurve;
    GovernorFanCurve.State? _governorState;
    bool _engineRunning;
    int? _lastTemp, _lastFan;

    /// <summary>The curve is enabled for this card (even if the governor is closed).</summary>
    bool CurveEnabledHere => _governorCurve is { Enabled: true } c && c.BusId == _device?.BusId;
    public bool CurveRunning => _engineRunning && _governorState is { Running: true } s && s.BusId == _device?.BusId;
    public string CurveStatus =>
        CurveRunning ? Txt.FanCurveRunning(_lastTemp, _lastFan)
        : CurveEnabledHere && !_engineRunning ? Txt.FanCurveWaitingGovernor
        : CurveEnabledHere && _governorState?.Error is { } e ? Txt.FanCurveStoppedWithError(e)
        : CurveEnabledHere ? Txt.FanCurveStarting
        : Txt.FanCurveStopped;

    /// <summary>Re-reads the request and the governor state (on every sensor read, 1 s).</summary>
    void RefreshCurveState(GpuSensors? sensors = null)
    {
        _governorCurve = GovernorFanCurve.Load();
        _governorState = GovernorFanCurve.ReadState();
        _engineRunning = GovernorFanCurve.EngineRunning;
        if (sensors is not null)
        {
            _lastTemp = (int?)sensors.TemperatureC;
            _lastFan = sensors.Fans.FirstOrDefault() is var f and > 0 ? (int)f : null;
        }
        Raise(nameof(CurveRunning));
        Raise(nameof(CurveStatus));
        Raise(nameof(TemperatureX));
        Raise(nameof(ShowFanCurve));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Curve line in the preview (X axis: 20–100 °C; Y axis: 0–100 %).</summary>
    public PointCollection CurvePreview
    {
        get
        {
            var pts = new PointCollection();
            if (ReadPoints(out _) is not { Count: > 0 } list) return pts;
            pts.Add(new Point(FanX(FanMinTemp), FanY(list[0].Percent)));
            foreach (var p in list) pts.Add(new Point(FanX(p.TempC), FanY(p.Percent)));
            pts.Add(new Point(FanX(FanMaxTemp), FanY(list[^1].Percent)));
            return pts;
        }
    }

    /// <summary>Area under the curve (the same line closed by the chart floor).</summary>
    public PointCollection CurveFill
    {
        get
        {
            var pts = CurvePreview;
            if (pts.Count == 0) return pts;
            pts.Add(new Point(_fanPlot.Width, _fanPlot.Height));
            pts.Add(new Point(0, _fanPlot.Height));
            return pts;
        }
    }

    /// <summary>Position of the current temperature on the chart (only while the curve is running).</summary>
    public double TemperatureX => CurveRunning && _lastTemp is { } t ? FanX(t) : -10;

    internal double FanX(double temp) => Math.Clamp((temp - FanMinTemp) / (FanMaxTemp - FanMinTemp), 0, 1) * _fanPlot.Width;
    internal double FanY(double percent) => _fanPlot.Height - Math.Clamp(percent, 0, 100) / 100 * _fanPlot.Height;

    /// <summary>Drag on the chart: temperature clamped between the neighboring points (the curve must rise from left to right).</summary>
    internal void DragFanPoint(FanPointViewModel point, double x, double y)
    {
        var i = CurvePoints.IndexOf(point);
        int Temp(int j) => int.TryParse(CurvePoints[j].Temp, out var t) ? t : 0;
        var temp = (int)Math.Round(FanMinTemp + x / _fanPlot.Width * (FanMaxTemp - FanMinTemp));
        temp = Math.Clamp(temp, (int)FanMinTemp, (int)FanMaxTemp);
        if (i > 0) temp = Math.Max(temp, Temp(i - 1) + 1);
        if (i >= 0 && i < CurvePoints.Count - 1) temp = Math.Min(temp, Temp(i + 1) - 1);
        point.Temp = temp.ToString();
        point.Percent = Math.Clamp((int)Math.Round(100 - y / _fanPlot.Height * 100), 0, 100).ToString();
    }

    void InitCurve()
    {
        _curve = FanCurveSettings.Load();
        _hysteresis = _curve.Hysteresis.ToString();
        foreach (var p in _curve.Points) CurvePoints.Add(new FanPointViewModel(p.TempC, p.Percent, this));
        AddPointCommand = new RelayCommand(AddPoint, () => CurvePoints.Count < FanCurve.MaxPoints);
        RemovePointCommand = new RelayCommand(() => { CurvePoints.RemoveAt(CurvePoints.Count - 1); CurveChanged(); }, () => CurvePoints.Count > 2);
        // With Curve mode selected, each edit restarts the curve (after a pause, because of dragging).
        _curveTimer.Tick += (_, _) => { _curveTimer.Stop(); if (FanCurveSelected) StartCurve(); };
        CurveChanged();
        RefreshCurveState();
    }

    void AddPoint()
    {
        var last = ReadPoints(out _)?.LastOrDefault() ?? new FanPoint(70, 80);
        CurvePoints.Add(new FanPointViewModel(Math.Min(last.TempC + 5, 110), Math.Min(last.Percent + 5, 100), this));
        CurveChanged();
    }

    internal void CurveChanged()
    {
        ReadPoints(out var error);
        CurveError = error;
        for (var i = 0; i < CurvePoints.Count; i++) CurvePoints[i].Label = $"P{i + 1}";
        Raise(nameof(CurvePreview));
        Raise(nameof(CurveFill));
        CommandManager.InvalidateRequerySuggested();
        _curveTimer.Stop();
        _curveTimer.Start();
        TrackUndo();
    }

    /// <summary>Points in typing order; null with the error when any field is not an integer.</summary>
    List<FanPoint>? ReadPoints(out string? error)
    {
        error = null;
        var list = new List<FanPoint>();
        foreach (var p in CurvePoints)
        {
            if (!int.TryParse(p.Temp.Trim(), out var t) || !int.TryParse(p.Percent.Trim(), out var pct)) { error = Txt.FanCurveEnterNumbers; return null; }
            list.Add(new FanPoint(t, pct));
        }
        if (!int.TryParse(_hysteresis.Trim(), out _)) error = Txt.FanCurveEnterNumbers;
        return list;
    }

    void StartCurve()
    {
        if (_device is null) return;
        SetFanMode("curve");
        if (ReadPoints(out var error) is not { } points || error is not null) { CurveError = error ?? Txt.FanCurveEnterNumbers; return; }
        var hysteresis = int.Parse(_hysteresis.Trim());
        if (!Elevation.IsAdmin) { CurveError = Txt.FanCurveNeedsAdmin; return; }
        if (_state is not { } s) return;
        if ((s.FanProblem ?? FanCurve.Problem(points, hysteresis, s.FanMin, s.FanMax)) is { } problem) { CurveError = problem; return; }
        // First time: the curve runs in KOS Engine, which must stay open (in the tray).
        if (!_curve.GovernorNoticeShown)
        {
            Toasts.Show(Txt.FanCurveGovernorNotice, ToastKind.Info);
            _curve.GovernorNoticeShown = true;
        }
        try
        {
            new GovernorFanCurve { Enabled = true, BusId = _device.BusId, Points = points, Hysteresis = hysteresis }.Save();
            _curve.BusId = _device.BusId;
            _curve.Points = points;
            _curve.Hysteresis = hysteresis;
            SaveCurve();
            CurveError = null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Fan curve not saved for KOS Governor: {ex.Message}");
            CurveError = ex.Message;
        }
        RefreshCurveState();
    }

    /// <summary>Turns the curve off in the governor and waits for it to give the fan back to automatic (up to 3 s).</summary>
    void TurnCurveOff()
    {
        if (_governorCurve is not { Enabled: true } c) return;
        try
        {
            c.Enabled = false;
            c.Save();
            for (var i = 0; i < 30 && GovernorFanCurve.EngineRunning && GovernorFanCurve.ReadState() is { Running: true }; i++) Thread.Sleep(100);
        }
        catch (Exception ex)
        {
            Log.Warn($"Turning the fan curve off failed: {ex.Message}");
            CurveError = ex.Message;
        }
        RefreshCurveState();
    }

    void SaveCurve()
    {
        try { _curve.Save(); }
        catch (Exception ex) { Log.Error("Saving the fan curve failed", ex); }
    }

    /// <summary>Writing a fan setting while the curve is running: stops the curve first.</summary>
    void StopCurveFor(GpuTuning changes)
    {
        if (CurveEnabledHere && (changes.FanAuto || changes.FanPercent is not null)) TurnCurveOff();
    }
}
