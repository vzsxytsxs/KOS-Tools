using KosToolbox.Core.Gpu;

namespace KosGovernor.Core;

/// <summary>
/// Runs in the engine the fan curve configured on the toolbox's GPU page (same controller as the toolbox: reads the
/// temperature every second; on stop or failure, returns the fan to the driver's automatic mode). Rereads the file when
/// it changes; if the curve stopped on its own (driver restarted, card not ready at sign-in), retries every 10 s.
/// </summary>
internal sealed class FanCurveHost
{
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);
    readonly FanCurveController _controller = FanCurveController.Instance;
    DateTime _stamp, _lastAttempt;
    bool _loaded;
    string? _startError;
    (bool, string?, string?)? _written;
    /// <summary>Gamemode GPU profile's fan (<see cref="Use"/>): runs instead of the toolbox's curve.</summary>
    GovernorFanCurve? _override;

    public bool Running => _controller.Running;

    /// <summary>The curve to run now: gamemode's, or the one saved by the toolbox.</summary>
    GovernorFanCurve? Wanted() => _override ?? GovernorFanCurve.Load();

    /// <summary>Runs this curve (or none, with Enabled = false) instead of the toolbox's, starting now, until <see cref="UseSaved"/>.</summary>
    public void Use(GovernorFanCurve curve)
    {
        _override = curve;
        _loaded = true;
        Apply();
    }

    /// <summary>Back to the toolbox's curve, starting now.</summary>
    public void UseSaved()
    {
        if (_override is null) return;
        _override = null;
        Apply();
    }

    /// <summary>Called every engine interval (and when the toolbox says it saved).</summary>
    public void Sync()
    {
        var path = GovernorFanCurve.FilePath;
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_loaded && stamp != _stamp && _override is not null)
        {
            _override = null; // the toolbox's newest choice wins
            Log.Info("Fan curve saved in the toolbox: it replaces the gamemode GPU profile's fan.");
        }
        var changed = !_loaded || stamp != _stamp;
        if (changed || (!_controller.Running && DateTime.UtcNow - _lastAttempt > Retry && Wanted() is { Enabled: true }))
        {
            _loaded = true;
            _stamp = stamp;
            Apply();
        }
        WriteState();
    }

    void Apply()
    {
        _lastAttempt = DateTime.UtcNow;
        _startError = null;
        try
        {
            if (_override is null && GovernorFanCurve.Problem(GovernorFanCurve.FilePath) is { } problem) throw new InvalidOperationException(problem);
            var curve = Wanted();
            if (curve is { Enabled: true, BusId: { } bus })
            {
                _controller.Start(bus, curve.Points, curve.Hysteresis); // revalidates the curve against the card's range
                Log.Info($"Fan curve{(_override is null ? "" : " of the gamemode GPU profile")} running on {bus}.");
            }
            else if (_controller.Running)
            {
                _controller.Stop();
                Log.Info("Fan curve turned off; fans back to automatic.");
            }
        }
        catch (Exception ex)
        {
            _controller.Stop();
            _startError = ex.Message;
            Log.Warn($"Fan curve not started: {ex.Message}");
        }
    }

    void WriteState()
    {
        var state = (_controller.Running, _controller.BusId, _startError ?? _controller.LastError);
        if (_written == state) return;
        try
        {
            GovernorFanCurve.WriteState(new GovernorFanCurve.State { Running = state.Running, BusId = state.BusId, Error = state.Item3 });
            _written = state;
        }
        catch (Exception ex) { Log.Warn($"Fan curve state not saved: {ex.Message}"); }
    }

    /// <summary>Driver restarted: stops the curve (if it was still running) and turns it on again on the next Sync, without waiting for the retry interval.</summary>
    public void Restart()
    {
        if (_controller.Running) _controller.Stop();
        _loaded = false;
    }

    /// <summary>Engine exiting: the fan goes back to automatic (with the engine closed nobody controls it anymore).</summary>
    public void Shutdown()
    {
        if (!_controller.Running) return;
        _controller.Stop();
        _written = null;
        WriteState();
        Log.Info("Engine exiting: fans back to automatic.");
    }
}
