namespace KosGovernor.Core;

/// <summary>
/// Automatic gamemode decision (no I/O, testable). Turns on when a game from the list is open; turns off after the
/// last one closes and the delay passes (launchers that reopen the game, crash and relaunch). It only turns off what it
/// turned on itself: turned on by hand stays on; turned off by hand with the game open does not turn back on until no game is open.
/// </summary>
public sealed class AutoGamemode
{
    public enum Step { None, TurnOn, TurnOff }

    DateTime? _noGameSince;

    /// <summary>The current gamemode was turned on by automatic mode.</summary>
    public bool TurnedOnByAuto { get; private set; }
    /// <summary>The user turned it off with a game open: it does not turn back on until no game is open.</summary>
    public bool Suppressed { get; private set; }

    public Step Next(bool gameRunning, bool gamemodeOn, DateTime now, TimeSpan offDelay)
    {
        if (gameRunning)
        {
            _noGameSince = null;
            if (gamemodeOn || Suppressed) return Step.None;
            TurnedOnByAuto = true;
            return Step.TurnOn;
        }
        Suppressed = false;
        if (!gamemodeOn || !TurnedOnByAuto) { _noGameSince = null; return Step.None; }
        _noGameSince ??= now;
        if (now - _noGameSince < offDelay) return Step.None;
        _noGameSince = null;
        TurnedOnByAuto = false;
        return Step.TurnOff;
    }

    /// <summary>Hotkey, tray or interface: the user takes over the gamemode.</summary>
    public void Manual(bool turnedOn, bool gameRunning)
    {
        TurnedOnByAuto = false;
        _noGameSince = null;
        Suppressed = !turnedOn && gameRunning;
    }

    /// <summary>Automatic mode is off in the configuration: the current state is left as it is, not tracked.</summary>
    public void Reset()
    {
        TurnedOnByAuto = false;
        Suppressed = false;
        _noGameSince = null;
    }
}
