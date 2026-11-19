using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Gpu;

public sealed record FanPoint(int TempC, int Percent);

/// <summary>Per-user saved curve (%LocalAppData%\KOS\Toolbox\fan-curve.json).</summary>
public sealed class FanCurveSettings
{
    public string? BusId { get; set; }
    public List<FanPoint> Points { get; set; } = new() { new(40, 40), new(55, 50), new(65, 65), new(75, 85), new(85, 100) };
    public int Hysteresis { get; set; } = 3;
    /// <summary>The warning that the curve runs in KOS Governor (which must stay open) has already been shown.</summary>
    public bool GovernorNoticeShown { get; set; }

    static string FilePath => Path.Combine(AppPaths.DataRoot, "fan-curve.json");
    public static FanCurveSettings Load() => JsonFile.Load<FanCurveSettings>(FilePath);
    public void Save() => JsonFile.Save(FilePath, this);
}

/// <summary>Temperature → fan speed curve: validation, interpolation and hysteresis (pure logic, tested).</summary>
public static class FanCurve
{
    public const int MaxPoints = 8, SafetyTempC = 90, SafetyPercent = 80;

    /// <summary>Null when the curve is safe and fits the driver's range; otherwise the reason.</summary>
    public static string? Problem(IReadOnlyList<FanPoint> points, int hysteresis, uint min, uint max)
    {
        if (points.Count is < 2 or > MaxPoints) return Txt.FanCurvePointCount(MaxPoints);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.TempC is < 0 or > 110) return Txt.FanCurveTempRange;
            if (p.Percent < min || p.Percent > max) return Txt.FanCurvePercentRange(min, max);
            if (i > 0 && p.TempC <= points[i - 1].TempC) return Txt.FanCurveTempOrder;
            if (i > 0 && p.Percent < points[i - 1].Percent) return Txt.FanCurvePercentOrder;
        }
        // Safety: the curve must reach a high speed before the card gets too hot.
        var needed = (int)Math.Min(SafetyPercent, max);
        if (!points.Any(p => p.TempC <= SafetyTempC && p.Percent >= needed)) return Txt.FanCurveUnsafe(needed, SafetyTempC);
        if (hysteresis is < 0 or > 15) return Txt.FanCurveHysteresisRange;
        return null;
    }

    /// <summary>% for a temperature: linear between points; below the first and above the last, the end point's value.</summary>
    public static int Target(IReadOnlyList<FanPoint> points, int tempC)
    {
        if (tempC <= points[0].TempC) return points[0].Percent;
        for (var i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            if (tempC <= b.TempC) return a.Percent + (int)Math.Round((b.Percent - a.Percent) * (tempC - a.TempC) / (double)(b.TempC - a.TempC));
        }
        return points[^1].Percent;
    }

    /// <summary>
    /// Next speed: goes up immediately; only goes down when the temperature falls <paramref name="hysteresis"/> °C below the
    /// temperature at which the current speed was set (keeps the fan from oscillating near a point).
    /// </summary>
    public static (int Percent, int RefTemp) Step(int? current, int refTemp, int tempC, IReadOnlyList<FanPoint> points, int hysteresis)
    {
        var target = Target(points, tempC);
        if (current is not { } c || target > c) return (target, tempC);
        if (target < c && tempC <= refTemp - hysteresis) return (target, tempC);
        return (c, refTemp);
    }
}

/// <summary>
/// Runs the curve (in KOS Engine, as administrator, to write the fan through NVML): reads the
/// temperature every second and adjusts the %. On stop, on closing the engine or on any read/write failure,
/// returns the fans to the driver's automatic mode.
/// </summary>
public sealed class FanCurveController
{
    public static FanCurveController Instance { get; } = new();
    readonly object _gate = new();
    CancellationTokenSource? _cts;
    Task? _loop;

    public bool Running { get { lock (_gate) return _cts is not null; } }
    public string? BusId { get; private set; }
    public int? Temperature { get; private set; }
    public int? Percent { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Raised on the loop thread on every reading and on stop.</summary>
    public event Action? Changed;

    public void Start(string busId, IReadOnlyList<FanPoint> points, int hysteresis)
    {
        if (!Elevation.IsAdmin) throw new InvalidOperationException(Txt.FanCurveNeedsAdmin);
        var s = GpuTuner.ReadState(busId);
        if (s.FanProblem is { } fanProblem) throw new InvalidOperationException(fanProblem);
        if (FanCurve.Problem(points, hysteresis, s.FanMin, s.FanMax) is { } problem) throw new InvalidOperationException(problem);
        Stop();
        lock (_gate)
        {
            var cts = _cts = new CancellationTokenSource();
            BusId = busId;
            LastError = null;
            var copy = points.ToList();
            _loop = Task.Run(() => LoopAsync(busId, copy, hysteresis, cts));
        }
        Log.Info($"Fan curve started on {busId}: {string.Join(", ", points.Select(p => $"{p.TempC}°C={p.Percent}%"))}, hysteresis {hysteresis}°C");
    }

    /// <summary>Stops the loop and waits for it to return the fans to automatic.</summary>
    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
        }
        try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
    }

    async Task LoopAsync(string busId, List<FanPoint> points, int hysteresis, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        int? current = null;
        var refTemp = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var temp = (int)(GpuTuner.ReadTemperature(busId) ?? throw new InvalidOperationException(Txt.FanCurveNoTemperature));
                var (next, nextRef) = FanCurve.Step(current, refTemp, temp, points, hysteresis);
                // Writes on every reading, not only when the % changes: if something returns the fans to the driver's automatic mode
                // (another write to the card, the driver, another program), the curve takes control back the next second.
                GpuTuner.SetFans(busId, (uint)next);
                (current, refTemp) = (next, nextRef);
                Temperature = temp;
                Percent = current;
                Changed?.Invoke();
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Warn($"Fan curve stopped: {ex.Message}");
        }
        finally
        {
            try { GpuTuner.SetFans(busId, null); Log.Info("Fan curve: fans back to automatic."); }
            catch (Exception ex) { Log.Error("Fan curve: couldn't put the fans back to automatic", ex); LastError ??= ex.Message; }
            lock (_gate) { if (ReferenceEquals(_cts, cts)) { _cts = null; _loop = null; } } // a new loop may have already started
            cts.Dispose();
            Temperature = Percent = null;
            Changed?.Invoke();
        }
    }
}
