using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class VfPointItem(GpuVfPoint point, GpuPageViewModel owner) : ObservableObject, IChartPoint
{
    int? _stagedDelta;
    double _plotX, _plotY;
    bool _isSelected;
    public GpuVfPoint Point { get; } = point;
    public int Index => Point.Index;
    public string Voltage => $"{Point.VoltageUv / 1000.0:0.##} mV";
    public long FrequencyKhz => Point.FrequencyKhz + ((_stagedDelta ?? Point.DeltaKhz) - Point.DeltaKhz);
    public string Frequency => $"{FrequencyKhz / 1000.0:0.##} MHz";
    public string Delta => $"{(_stagedDelta ?? Point.DeltaKhz) / 1000.0:+0.##;-0.##;0} MHz";
    public string Tip => $"P{Index} · {Voltage} · {Frequency} ({Delta})";
    public bool Pending => _stagedDelta is not null && _stagedDelta != Point.DeltaKhz;
    public double PlotX => _plotX;
    public double PlotY => _plotY;
    public double PlotLeft => _plotX - GpuPageViewModel.VfMarker / 2;
    public double PlotTop => _plotY - GpuPageViewModel.VfMarker / 2;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public void Position(double x, double y)
    {
        _plotX = x;
        _plotY = y;
        Raise(nameof(PlotLeft));
        Raise(nameof(PlotTop));
    }
    public void Stage(int? delta)
    {
        _stagedDelta = delta;
        Raise(nameof(Frequency));
        Raise(nameof(Delta));
        Raise(nameof(Tip));
        Raise(nameof(Pending));
    }
    public void DragStart() => owner.PressVfPoint(this, Keyboard.Modifiers);
    public void DragTo(double x, double y) => owner.DragVfPoint(this, y);
    public void DragEnd() => owner.EndVfEdit();
}

/// <summary>
/// V/F curve editor, like MSI Afterburner's: click selects, Ctrl+click adds/removes points, Shift+click or
/// Shift+drag on the background selects a range; dragging or the arrow keys move the selection together; points to the right that
/// would end up below a moved point rise up to it (flattened curve); L locks the point's voltage, L again locks the
/// clock and L a third time releases.
/// </summary>
public sealed partial class GpuPageViewModel : IChartRange
{
    internal const double VfMarker = 8;
    readonly Dictionary<int, int> _stagedVf = new();
    PointCollection _vfPreview = new(), _vfStock = new();
    IReadOnlyList<ChartTick> _vfXTicks = Array.Empty<ChartTick>(), _vfYTicks = Array.Empty<ChartTick>();
    Size _vfPlot = new(1000, 300);
    long _vfMinF, _vfMaxF = 1;
    int? _stagedVfLock, _stagedClockLock;
    VfPointItem? _selectedVfPoint;
    Dictionary<int, int>? _editBase;
    double? _rangeStart;
    string _vfTargetText = "";
    string? _vfError;
    bool _vfActive;

    public ObservableCollection<VfPointItem> VfPoints { get; } = new();
    public ICommand StageVfPointCommand { get; private set; } = null!;
    public ICommand ResetVfPointCommand { get; private set; } = null!;
    public ICommand VfCycleLockCommand { get; private set; } = null!;
    public ICommand VfNudgeCommand { get; private set; } = null!;
    public ICommand VfSelectAllCommand { get; private set; } = null!;
    public ICommand VfClearSelectionCommand { get; private set; } = null!;
    public bool VfEnabled => _state?.Curve is { Points.Count: > 1 };
    public string? VfProblem => _state?.CurveProblem;
    public string VfRange => _state?.Curve is { } c ? Txt.GpuCurveRange(c.MinimumDeltaKhz / 1000, c.MaximumDeltaKhz / 1000) : "";

    /// <summary>
    /// Custom curve on. Turning it off marks all points back to the driver curve with the current core offset and
    /// releases the locks; turning it on goes back to what the card has now. In both cases the write happens through Apply.
    /// </summary>
    public bool VfActive
    {
        get => _vfActive;
        set
        {
            if (value == _vfActive || !VfEnabled) return;
            _vfActive = value;
            if (value) RestoreVf(); else ResetVf();
            Raise();
            Raise(nameof(VfLockEnabled));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------- locks (L: voltage → clock → none) ----------
    bool CanLockVoltage => _state?.LockVoltageUv is not null;
    bool CanLockClock => _state?.ClockLockMhz is not null;
    int LockedUv => _stagedVfLock ?? _state?.LockVoltageUv ?? 0;
    int LockedMhz => _stagedClockLock ?? _state?.ClockLockMhz ?? 0;
    public bool VfLockEnabled => _vfActive && (CanLockVoltage || CanLockClock);
    public string? VfLockProblem => CanLockVoltage || CanLockClock ? null : _state?.LockProblem ?? _state?.ClockLockProblem;
    public string VfLockStatus => LockedUv > 0 ? $"Voltage locked to {LockedUv / 1000.0:0.##} mV"
        : LockedMhz > 0 ? $"Clock locked to {LockedMhz} MHz" : "Unlocked";
    public string VfLockButtonText => LockedUv > 0 ? "Lock: voltage" : LockedMhz > 0 ? "Lock: clock" : "Lock: off";
    public bool VfLockVisible => LockedUv > 0;
    public double VfLockX => VfPoints.FirstOrDefault(p => p.Point.VoltageUv == LockedUv)?.PlotX ?? -10;
    public bool VfClockLockVisible => LockedMhz > 0;
    public double VfClockLockY => LockedMhz > 0 ? VfY(LockedMhz * 1000L) : -10;

    // ---------- where the card is now ----------
    int? _liveUv;
    long? _liveKhz;

    /// <summary>Voltage and clock read every second (like the temperature line on the fan curve).</summary>
    public bool VfLiveVisible => _liveUv is > 0 && _liveKhz is > 0 && _state?.Curve?.Points is { Count: > 1 };
    public double VfLiveX => VfLiveVisible && _state?.Curve?.Points is { } p
        ? Math.Clamp(VfX(_liveUv!.Value, p[0].VoltageUv, p[^1].VoltageUv), 0, _vfPlot.Width) : -10;
    public double VfLiveY => VfLiveVisible ? Math.Clamp(VfY(_liveKhz!.Value), 0, _vfPlot.Height) : -10;
    public string VfLiveText => VfLiveVisible ? $"Red: the card now, {_liveKhz!.Value / 1000} MHz at {_liveUv!.Value / 1000.0:0.#} mV." : "";

    void UpdateVfLive(GpuSensors r)
    {
        _liveUv = r.VoltageUv;
        _liveKhz = r.GraphicsMhz is { } mhz ? mhz * 1000L : null;
        RaiseVfLive();
    }

    void RaiseVfLive()
    {
        foreach (var n in new[] { nameof(VfLiveVisible), nameof(VfLiveX), nameof(VfLiveY), nameof(VfLiveText) }) Raise(n);
    }

    // ---------- selection ----------
    List<VfPointItem> Selection => VfPoints.Where(p => p.IsSelected && p.Index > 0).ToList();
    public string VfSelectionLabel => Selection is { Count: > 1 } s ? $"{s.Count} points • P{s[0].Index}–P{s[^1].Index}"
        : SelectedVfPoint is { } p ? $"P{p.Index} • {p.Voltage}" : "Select a point";
    public string VfSelectedTip => SelectedVfPoint is { } p ? $"{p.Voltage} • {ShownText(p)} MHz" : "";
    public string VfSelectedVoltage => SelectedVfPoint is { } p ? (p.Point.VoltageUv / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) : "";
    public double VfSelectedX => SelectedVfPoint?.PlotX ?? -10;
    public double VfSelectedY => SelectedVfPoint?.PlotY ?? -10;
    public bool HasVfSelection => SelectedVfPoint is not null;
    /// <summary>Blue band behind the selected points (when more than one), as in Afterburner.</summary>
    public bool VfSelectionVisible => Selection.Count > 1;
    public double VfSelectionLeft => Selection is { Count: > 1 } s ? Math.Max(0, s[0].PlotX - VfHalfStep) : 0;
    public double VfSelectionWidth => Selection is { Count: > 1 } s ? Math.Min(_vfPlot.Width, s[^1].PlotX + VfHalfStep) - VfSelectionLeft : 0;
    double VfHalfStep => VfPoints.Count > 1 ? _vfPlot.Width / (VfPoints.Count - 1) / 2 : 0;
    public string? VfError { get => _vfError; private set { if (Set(ref _vfError, value)) Changed(); } }

    /// <summary>Main point (the one in the MHz field and the L key); assigning selects only it.</summary>
    public VfPointItem? SelectedVfPoint
    {
        get => _selectedVfPoint;
        set => SelectOnly(value);
    }
    public string VfTargetText { get => _vfTargetText; set { Set(ref _vfTargetText, value ?? ""); VfError = null; } }
    public PointCollection VfPreview => _vfPreview;
    public PointCollection VfStockPreview => _vfStock;
    public IReadOnlyList<ChartTick> VfXTicks => _vfXTicks;
    public IReadOnlyList<ChartTick> VfYTicks => _vfYTicks;
    public double VfPlotWidth => _vfPlot.Width;
    public double VfPlotHeight => _vfPlot.Height;

    /// <summary>Real size of the chart area, reported by the view.</summary>
    public Size VfPlotSize
    {
        get => _vfPlot;
        set
        {
            if (value.IsEmpty || value.Width < 1 || value.Height < 1 || value == _vfPlot) return;
            _vfPlot = value;
            Raise(nameof(VfPlotWidth));
            Raise(nameof(VfPlotHeight));
            UpdateVfPlot();
        }
    }

    void SelectOnly(VfPointItem? item)
    {
        foreach (var point in VfPoints) point.IsSelected = point == item;
        SetPrimary(item);
    }

    void SetPrimary(VfPointItem? item)
    {
        if (Set(ref _selectedVfPoint, item, nameof(SelectedVfPoint))) VfTargetText = item is null ? "" : ShownText(item);
        RaiseVfSelection();
        CommandManager.InvalidateRequerySuggested();
    }

    void RaiseVfSelection()
    {
        foreach (var n in new[] { nameof(VfSelectionLabel), nameof(VfSelectedTip), nameof(VfSelectedVoltage), nameof(VfSelectedX), nameof(VfSelectedY),
                     nameof(HasVfSelection), nameof(VfSelectionVisible), nameof(VfSelectionLeft), nameof(VfSelectionWidth) })
            Raise(n);
    }

    void RaiseVfLocks()
    {
        foreach (var n in new[] { nameof(VfLockStatus), nameof(VfLockButtonText), nameof(VfLockVisible), nameof(VfLockX), nameof(VfClockLockVisible),
                     nameof(VfClockLockY), nameof(VfLockEnabled), nameof(VfLockProblem) })
            Raise(n);
    }

    /// <summary>Click on a point: Ctrl adds/removes, Shift selects from the main point up to it; with no key, a click on an already
    /// selected point keeps the group (to drag them all).</summary>
    internal void PressVfPoint(VfPointItem item, ModifierKeys mods)
    {
        if (mods.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            SetPrimary(item.IsSelected ? item : VfPoints.LastOrDefault(p => p.IsSelected));
        }
        else if (mods.HasFlag(ModifierKeys.Shift) && SelectedVfPoint is { } anchor)
        {
            var (a, b) = (Math.Min(anchor.Index, item.Index), Math.Max(anchor.Index, item.Index));
            foreach (var p in VfPoints) p.IsSelected = p.Index >= a && p.Index <= b;
            SetPrimary(item);
        }
        else if (item.IsSelected) SetPrimary(item);
        else SelectOnly(item);
        _editBase = Deltas();
    }

    // Shift+drag on the chart background selects the points in the range; a plain click on the background goes back to a single point.
    void IChartRange.RangeStart(double x)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { SelectOnly(SelectedVfPoint); return; }
        _rangeStart = x;
        ((IChartRange)this).RangeTo(x);
    }

    void IChartRange.RangeTo(double x)
    {
        if (_rangeStart is not { } start) return;
        var (a, b) = (Math.Min(start, x), Math.Max(start, x));
        foreach (var p in VfPoints) p.IsSelected = p.Index > 0 && p.PlotX >= a - VfHalfStep && p.PlotX <= b + VfHalfStep;
        SetPrimary(VfPoints.LastOrDefault(p => p.IsSelected) ?? SelectedVfPoint);
    }

    void IChartRange.RangeEnd() => _rangeStart = null;

    long StagedKhz(GpuVfPoint p) => p.FrequencyKhz + _stagedVf.GetValueOrDefault(p.Index, p.DeltaKhz) - p.DeltaKhz;

    /// <summary>Preview of the not-yet-applied core offset: the whole curve moves up or down with the slider.</summary>
    long VfShiftKhz => _state?.Core is { } c && _pCore is { } pending ? (pending - c.Current) * 1000L : 0;
    long ShownKhz(GpuVfPoint p) => StagedKhz(p) + VfShiftKhz;
    string ShownText(VfPointItem p) => ((p.FrequencyKhz + VfShiftKhz) / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Called when the pending core offset changes.</summary>
    void CoreShiftChanged()
    {
        FitVfAxis();
        UpdateVfPlot();
        if (SelectedVfPoint is { } s) { _vfTargetText = ShownText(s); Raise(nameof(VfTargetText)); }
    }
    double VfX(int uv, int minV, int maxV) => (uv - minV) * _vfPlot.Width / Math.Max(1, maxV - minV);
    double VfY(long khz) => _vfPlot.Height - (khz - _vfMinF) * _vfPlot.Height / (double)Math.Max(1, _vfMaxF - _vfMinF);

    /// <summary>Frequency axis in multiples of 200 MHz with headroom; fixed during a drag so the chart does not jump.</summary>
    void FitVfAxis()
    {
        if (_state?.Curve?.Points is not { Count: > 1 } points) return;
        const long step = 200_000;
        var low = points.Min(p => Math.Min(p.FrequencyKhz - p.DeltaKhz, ShownKhz(p)));
        var high = points.Max(p => Math.Max(p.FrequencyKhz - p.DeltaKhz, ShownKhz(p)));
        _vfMinF = Math.Max(0, (low - step / 2) / step * step);
        _vfMaxF = (high + step + step / 2) / step * step;
    }

    void UpdateVfPlot()
    {
        var curve = new PointCollection();
        var stock = new PointCollection();
        var xTicks = new List<ChartTick>();
        var yTicks = new List<ChartTick>();
        if (_state?.Curve?.Points is { Count: > 1 } points)
        {
            var minV = points[0].VoltageUv;
            var maxV = points[^1].VoltageUv;
            foreach (var point in points)
            {
                var x = VfX(point.VoltageUv, minV, maxV);
                var y = VfY(ShownKhz(point));
                curve.Add(new Point(x, y));
                stock.Add(new Point(x, VfY(point.FrequencyKhz - point.DeltaKhz)));
                if (point.Index < VfPoints.Count) VfPoints[point.Index].Position(x, y);
            }
            // Spaced ticks so the labels do not touch in narrow windows.
            var mvSpan = (maxV - minV) / 1000.0;
            var mvStep = new[] { 25, 50, 100, 200 }.FirstOrDefault(s => _vfPlot.Width / (mvSpan / s) >= 44, 200);
            for (var mv = (int)Math.Ceiling(minV / 1000.0 / mvStep) * mvStep; mv * 1000 <= maxV; mv += mvStep)
                xTicks.Add(new ChartTick(mv.ToString(), VfX(mv * 1000, minV, maxV)));
            var mhzSpan = (_vfMaxF - _vfMinF) / 1000.0;
            var mhzStep = new[] { 200, 400, 500, 1000 }.FirstOrDefault(s => _vfPlot.Height / (mhzSpan / s) >= 24, 1000);
            for (var mhz = (long)Math.Ceiling(_vfMinF / 1000.0 / mhzStep) * mhzStep; mhz * 1000 <= _vfMaxF; mhz += mhzStep)
                yTicks.Add(new ChartTick(mhz.ToString(), VfY(mhz * 1000)));
        }
        _vfPreview = curve;
        _vfStock = stock;
        _vfXTicks = xTicks;
        _vfYTicks = yTicks;
        Raise(nameof(VfPreview));
        Raise(nameof(VfStockPreview));
        Raise(nameof(VfXTicks));
        Raise(nameof(VfYTicks));
        RaiseVfLocks();
        RaiseVfSelection();
        RaiseVfLive(); // axes and size change the position of the current point
    }

    void InitVf()
    {
        bool CanEdit() => !_busy && _vfActive && VfEnabled && Selection.Count > 0;
        StageVfPointCommand = new RelayCommand(StageVfPoint, () => CanEdit() && SelectedVfPoint is { Index: > 0 });
        ResetVfPointCommand = new RelayCommand(() =>
        {
            if (_state?.Curve is not { } curve) return;
            var deltas = Deltas();
            foreach (var p in Selection) deltas[p.Index] = 0;
            StageMany(VfCurveEdit.Flatten(curve.Points, deltas, curve.MinimumDeltaKhz, curve.MaximumDeltaKhz));
        }, CanEdit);
        VfCycleLockCommand = new RelayCommand(CycleLock, () => !_busy && VfLockEnabled && SelectedVfPoint is not null);
        VfNudgeCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mhz)) { MoveSelection(mhz * 1000L, Deltas()); VfError = null; }
        }, _ => CanEdit());
        VfSelectAllCommand = new RelayCommand(() =>
        {
            foreach (var p in VfPoints) p.IsSelected = p.Index > 0;
            SetPrimary(SelectedVfPoint is { IsSelected: true } s ? s : VfPoints.LastOrDefault());
        }, () => VfEnabled);
        VfClearSelectionCommand = new RelayCommand(() => SelectOnly(SelectedVfPoint), () => VfEnabled);
    }

    void LoadVf()
    {
        _stagedVf.Clear();
        _stagedVfLock = _stagedClockLock = null;
        _editBase = null;
        VfPoints.Clear();
        foreach (var point in _state?.Curve?.Points ?? Array.Empty<GpuVfPoint>()) VfPoints.Add(new VfPointItem(point, this));
        _vfActive = VfPoints.Skip(1).Any(p => p.Point.DeltaKhz != FlatDeltaKhz) || _state?.LockVoltageUv is > 0 || _state?.ClockLockMhz is > 0;
        SelectedVfPoint = VfPoints.Count > 1 ? VfPoints[^1] : null;
        FitVfAxis();
        UpdateVfPlot();
        VfError = null;
        Raise(nameof(VfActive));
        Raise(nameof(VfEnabled));
        Raise(nameof(VfProblem));
        Raise(nameof(VfRange));
    }

    /// <summary>Offset (kHz) of each point as it is now, including what has already been marked.</summary>
    Dictionary<int, int> Deltas() => VfPoints.ToDictionary(p => p.Index, p => _stagedVf.GetValueOrDefault(p.Index, p.Point.DeltaKhz));

    internal void EndVfEdit() => _editBase = null;

    /// <summary>Vertical drag: the point goes to the mouse frequency and the selection moves with it (from how it was at click time).</summary>
    internal void DragVfPoint(VfPointItem item, double y)
    {
        if (!_vfActive || _busy || item.Index == 0 || _state?.Curve is null) return;
        _editBase ??= Deltas();
        if (!item.IsSelected) SelectOnly(item);
        var khz = _vfMinF + (long)((_vfPlot.Height - y) / _vfPlot.Height * (_vfMaxF - _vfMinF));
        khz = Math.Clamp(khz, _vfMinF, _vfMaxF) / 1000 * 1000 - VfShiftKhz; // 1 MHz steps; without the core offset preview
        var newDelta = item.Point.DeltaKhz + khz - item.Point.FrequencyKhz;
        MoveSelection(newDelta - _editBase[item.Index], _editBase);
        VfError = null;
    }

    /// <summary>Moves the selection together from <paramref name="baseDeltas"/>, with Afterburner's flattening (VfCurveEdit.Move).</summary>
    void MoveSelection(long diff, IReadOnlyDictionary<int, int> baseDeltas)
    {
        if (_state?.Curve is not { } curve) return;
        var selected = Selection.Select(p => p.Index).ToHashSet();
        if (selected.Count == 0) return;
        StageMany(VfCurveEdit.Move(curve.Points, baseDeltas, selected, diff, curve.MinimumDeltaKhz, curve.MaximumDeltaKhz));
    }

    /// <summary>MHz field: moves the main point to the typed value; the rest of the selection moves by the same amount.</summary>
    void StageVfPoint()
    {
        if (_state?.Curve is not { } curve || SelectedVfPoint is not { Index: > 0 } selected) return;
        if (!decimal.TryParse(VfTargetText.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz)
            || mhz <= 0 || mhz > 10000 || mhz * 1000 != decimal.Truncate(mhz * 1000))
        { VfError = Txt.GpuEnterVfMhz; return; }
        var shownKhz = (long)(mhz * 1000);
        var targetKhz = shownKhz - VfShiftKhz; // the field shows the curve with the core offset preview
        var delta = (long)selected.Point.DeltaKhz + targetKhz - selected.Point.FrequencyKhz;
        if (targetKhz <= 0 || delta < curve.MinimumDeltaKhz || delta > curve.MaximumDeltaKhz)
        {
            VfError = Txt.GpuCurveRange(curve.MinimumDeltaKhz / 1000, curve.MaximumDeltaKhz / 1000);
            return;
        }
        var current = Deltas();
        MoveSelection(delta - current[selected.Index], current);
        // A typed value may fall outside the current axis (a drag cannot).
        if (shownKhz < _vfMinF || shownKhz > _vfMaxF) { FitVfAxis(); UpdateVfPlot(); }
        VfError = null;
    }

    /// <summary>A single point (profiles, Reset).</summary>
    void StageVf(VfPointItem selected, int delta)
    {
        var deltas = Deltas();
        deltas[selected.Index] = delta;
        StageMany(deltas);
    }

    /// <summary>Marks several points at once and redraws only once.</summary>
    void StageMany(IReadOnlyDictionary<int, int> deltas)
    {
        foreach (var p in VfPoints.Skip(1))
        {
            if (!deltas.TryGetValue(p.Index, out var d)) continue;
            if (d == p.Point.DeltaKhz) _stagedVf.Remove(p.Index); else _stagedVf[p.Index] = d;
            p.Stage(_stagedVf.TryGetValue(p.Index, out var staged) ? staged : null);
        }
        if (SelectedVfPoint is { } s) { _vfTargetText = ShownText(s); Raise(nameof(VfTargetText)); }
        UpdateVfPlot();
        Changed();
    }

    /// <summary>L on the main point: no lock → locks the voltage; voltage on this point → locks its clock; clock → releases.</summary>
    void CycleLock()
    {
        if (SelectedVfPoint is not { } p) return;
        var mhz = (int)((p.FrequencyKhz + VfShiftKhz) / 1000);
        if (LockedUv > 0 && LockedUv != p.Point.VoltageUv) StageLock(p.Point.VoltageUv);
        else if (LockedUv > 0) { StageLock(0); if (CanLockClock) StageClockLock(mhz); }
        else if (LockedMhz > 0) StageClockLock(0);
        else if (CanLockVoltage) StageLock(p.Point.VoltageUv);
        else StageClockLock(mhz);
    }

    void StageLock(int voltageUv)
    {
        _stagedVfLock = voltageUv == _state?.LockVoltageUv ? null : voltageUv;
        RaiseVfLocks();
        Changed();
    }

    void StageClockLock(int mhz)
    {
        if (!CanLockClock) return;
        _stagedClockLock = mhz == _state?.ClockLockMhz ? null : mhz;
        RaiseVfLocks();
        Changed();
    }

    int? CurveLock(bool full) => _stagedVfLock ?? (full ? _state?.LockVoltageUv : null);
    int? ClockLock(bool full) => _stagedClockLock ?? (full ? _state?.ClockLockMhz : null);

    /// <summary>
    /// As in MSI Afterburner, the core offset moves the whole curve. Writing the offset makes the driver put all points
    /// (except 0) at it, erasing the shape of the curve (seen on the GTX 1650 SUPER); so, when the core changes, the curve is sent
    /// whole, shifted by the difference, and GpuTuner writes it after the offset.
    /// shortcut: assumes offset and points are the same control (Pascal/Turing through NVAPI); on a card where the two
    /// are independent, the curve would be shifted twice — verify by reading the points after writing only the offset.
    /// </summary>
    List<GpuVfChange>? CurveChanges(bool full)
    {
        if (_state?.Curve is not { } curve) return null;
        var shift = (int)VfShiftKhz;
        var changes = curve.Points.Skip(1)
            .Where(p => full || shift != 0 || _stagedVf.ContainsKey(p.Index))
            .Select(p => new GpuVfChange(p.Index, p.VoltageUv,
                Math.Clamp(_stagedVf.GetValueOrDefault(p.Index, p.DeltaKhz) + shift, curve.MinimumDeltaKhz, curve.MaximumDeltaKhz)))
            .ToList();
        return changes.Count > 0 ? changes : null;
    }

    /// <summary>Offset of the points on the unmodified driver curve: the current core offset on all of them.</summary>
    int FlatDeltaKhz => (_state?.Core?.Current ?? 0) * 1000;

    /// <summary>Discards what was marked on the curve and goes back to what the card has now.</summary>
    void RestoreVf()
    {
        _stagedVf.Clear();
        _stagedVfLock = _stagedClockLock = null;
        foreach (var point in VfPoints) point.Stage(null);
        if (SelectedVfPoint is { } s) VfTargetText = ShownText(s);
        FitVfAxis();
        UpdateVfPlot();
        Changed();
    }

    void ResetVf()
    {
        if (_state?.Curve is not { } curve) return;
        _vfActive = false;
        Raise(nameof(VfActive));
        var flat = FlatDeltaKhz;
        if (flat >= curve.MinimumDeltaKhz && flat <= curve.MaximumDeltaKhz)
            StageMany(VfPoints.Skip(1).ToDictionary(p => p.Index, _ => flat));
        if (_state?.LockVoltageUv is > 0) StageLock(0);
        if (_state?.ClockLockMhz is > 0) StageClockLock(0);
        RaiseVfLocks();
    }
}
