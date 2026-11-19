namespace KosToolbox.Core.Text;

/// <summary>Texts for the GPU page's fan curve.</summary>
public static partial class Txt
{
    public const string FanCurveTitle = "Fan curve";
    public const string FanCurveNote = "The fan follows the GPU temperature using the points below, instead of a fixed speed or the card's own automatic curve. You set it up here; KOS Engine (in the tray) runs it, so the toolbox can be closed. It checks the temperature every second; turning it off, exiting KOS Engine or any read error puts the fan back to automatic. If the engine is ended abnormally (for example from Task Manager), the fan keeps its last speed until the driver restarts. Close MSI Afterburner's fan control first, or the two fight over the fan.";
    public const string FanCurveTemp = "Temperature (°C)", FanCurvePercent = "Fan (%)", FanCurveHysteresis = "Hysteresis (°C)",
        FanCurveHysteresisHint = "The speed only drops after the temperature falls this much below where it rose.",
        AddPoint = "Add point",
        FanCurveStopped = "The curve isn't running.",
        FanCurveStarting = "Sent to KOS Engine; starting…",
        FanCurveWaitingGovernor = "KOS Engine isn't running: open it (it stays in the tray) and the curve starts by itself.",
        FanCurveGovernorNotice = "The fan curve is applied by KOS Engine, which runs in the tray. You can close the toolbox, but KOS Engine has to stay open: exiting it from the tray puts the fan back to automatic.\n\nTo keep the curve after restarting Windows, turn on Start with Windows in KOS Governor › Settings.";
    public static string FanCurveRunning(int? temp, int? percent) => $"Running in KOS Engine · {temp?.ToString() ?? "…"} °C → {percent?.ToString() ?? "…"} %";
    public static string FanCurveStoppedWithError(string error) => $"The curve stopped and the fan went back to automatic: {error}";
    public const string FanCurveNeedsAdmin = "The fan curve needs the toolbox running as administrator (Settings › Restart as administrator).",
        FanCurveNoTemperature = "The GPU temperature couldn't be read.",
        FanCurveTempRange = "Temperatures go from 0 to 110 °C.", FanCurveTempOrder = "Each point needs a higher temperature than the one before.",
        FanCurvePercentOrder = "The fan speed can't go down as the temperature goes up.", FanCurveHysteresisRange = "Hysteresis goes from 0 to 15 °C.",
        FanCurveEnterNumbers = "Enter whole numbers for temperature and fan speed.";
    public static string FanCurvePointCount(int max) => $"Use from 2 to {max} points.";
    public static string FanCurvePercentRange(uint min, uint max) => $"Fan speeds go from {min} to {max} % on this card.";
    public static string FanCurveUnsafe(int percent, int temp) => $"For safety, the curve must reach at least {percent} % at or below {temp} °C.";
}
