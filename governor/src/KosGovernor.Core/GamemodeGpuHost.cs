using KosToolbox.Core.Gpu;

namespace KosGovernor.Core;

/// <summary>
/// GPU profiles for gamemode, chosen on the toolbox's GPU page (gamemode-gpu.json). Gamemode on: saves what the card has, then
/// writes the "on" profile. Gamemode off: writes the "off" profile, or puts back what the card had. A profile with a fan curve
/// runs it instead of the toolbox's curve; one with an automatic or fixed fan stops that curve while it is on.
/// No "on" profile and no "off" profile: the card is never touched. Runs on the engine thread.
/// </summary>
internal sealed class GamemodeGpuHost
{
    readonly FanCurveHost _fans;

    public GamemodeGpuHost(FanCurveHost fans) => _fans = fans;

    public void Switch(bool on)
    {
        try { if (on) TurnOn(); else TurnOff(); }
        catch (Exception ex) { Log.Error($"Gamemode GPU profile ({(on ? "on" : "off")}) failed", ex); }
    }

    void TurnOn()
    {
        if (GamemodeGpu.Load() is not { On: { } profile } config) return;
        if (config.Suspended)
        {
            Log.Warn($"Gamemode GPU profile \"{profile.Name}\" skipped: writing it didn't finish last time (the PC may have crashed). Choose it again on the toolbox's GPU page to turn it back on.");
            return;
        }
        if (GamemodeGpu.Problem(profile) is { } problem) { Log.Warn($"Gamemode GPU profile \"{profile.Name}\" skipped: {problem}"); return; }
        // A state left by a gamemode that didn't end still holds what the card had before that one (an unreadable one doesn't).
        var state = GamemodeGpu.LoadState() is { Before: not null } left ? left : new GamemodeGpuState { Before = GamemodeGpu.Current(GpuTuner.ReadState(profile.Tuning.PciBusId), fan: !_fans.Running) };
        state.Applying = true;
        GamemodeGpu.SaveState(state); // a crash while writing leaves Applying behind: the profile is suspended at the next start
        _fans.Use(profile.Curve ?? new GovernorFanCurve { Enabled = false });
        Write(profile.Tuning, $"profile \"{profile.Name}\"");
        state.Applying = false;
        GamemodeGpu.SaveState(state);
    }

    void TurnOff()
    {
        var state = GamemodeGpu.LoadState();
        bool written;
        if (GamemodeGpu.Load() is { Off: { } profile } && GamemodeGpu.Problem(profile) is null)
        {
            _fans.Use(profile.Curve ?? new GovernorFanCurve { Enabled = false });
            written = Write(profile.Tuning, $"profile \"{profile.Name}\"");
        }
        else if (state?.Before is { } before)
        {
            _fans.UseSaved();
            written = Write(before, "the settings from before gamemode");
        }
        else return;
        // Not written (card not ready yet): the saved state stays, and the next gamemode off tries again.
        if (written && state is not null) GamemodeGpu.SaveState(null);
    }

    /// <summary>Engine start: a gamemode the engine didn't end (closed abnormally, PC crashed) is ended now.</summary>
    public void RecoverAtStart()
    {
        try
        {
            if (GamemodeGpu.LoadState() is not { } state) return;
            if (state.Applying && GamemodeGpu.Load() is { On: { } profile } config)
            {
                config.Suspended = true;
                config.Save();
                Log.Error($"Writing the gamemode GPU profile \"{profile.Name}\" didn't finish (the PC may have crashed): it stays off until chosen again on the toolbox's GPU page.");
            }
            Log.Info("The last gamemode didn't end before the engine closed: putting the GPU settings back.");
            TurnOff();
        }
        catch (Exception ex) { Log.Error("Recovering the GPU settings from before gamemode failed", ex); }
    }

    static bool Write(GpuTuning t, string what)
    {
        try
        {
            var unconfirmed = GpuTuner.Apply(t);
            if (unconfirmed.Count == 0) Log.Info($"Gamemode GPU: {what} written and confirmed.");
            else Log.Warn($"Gamemode GPU: {what} written, but the card doesn't show: {string.Join(", ", unconfirmed)}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Gamemode GPU: writing {what} failed: {ex.Message}");
            return false;
        }
    }
}
