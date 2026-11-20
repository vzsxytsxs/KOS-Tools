using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;

namespace KosGovernor.Core;

/// <summary>
/// Video driver restarted or recovered from a hang (TDR): it comes back with clocks, limits and fans at the
/// default. The engine notices (<see cref="GpuResetWatch"/>), turns the fan curve back on right away and reapplies the
/// overclock saved by the toolbox for sign-in (gpu-tuning.json, "Apply at sign-in"), retrying for up to a minute while
/// the card comes back.
/// </summary>
internal sealed class GpuRecovery
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(1), Every = TimeSpan.FromSeconds(5);
    readonly GpuResetWatch _watch = new();
    DateTime? _until;
    DateTime _next;

    /// <summary>Every engine interval.</summary>
    public void Tick(FanCurveHost fans)
    {
        var now = DateTime.UtcNow;
        // Only checks the card's session when there is something to redo; the event log is always read (it's cheap).
        var curve = GovernorFanCurve.Load();
        var signIn = File.Exists(GpuTuner.SignInFile);
        var bus = curve is { Enabled: true, BusId: { } b } ? b : signIn ? GpuTuner.SavedTuning()?.PciBusId : null;
        // The fan curve rewrites the fans every second, so NVAPI's probe is enough for it; reapplying the overclock needs NVML's,
        // which notices every driver restart (NVAPI may just hand out fresh handles) but keeps ~20 MB in the engine.
        if (_watch.Poll(bus, nvml: signIn))
        {
            Log.Warn("The display driver restarted or recovered from a hang: restarting the fan curve and reapplying the saved GPU tuning.");
            fans.Restart();
            if (File.Exists(GpuTuner.SignInFile)) (_until, _next) = (now + Window, now);
        }
        if (_until is not { } until || now < _next) return;
        var code = GpuTuner.RunAtSignIn(); // revalidates and saves; the detailed result goes to the toolbox log
        if (code is ElevatedJob.ExitOk or ElevatedJob.ExitPartial or ElevatedJob.ExitRefused || now > until)
        {
            _until = null;
            Log.Info(code == ElevatedJob.ExitOk ? "GPU tuning reapplied after the driver reset." : $"GPU tuning after the driver reset: code {code} (see the toolbox log).");
        }
        else _next = now + Every;
    }
}
