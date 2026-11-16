using System.Globalization;

namespace KosToolbox.Core.Text;

/// <summary>Texts for the GPU page.</summary>
public static partial class Txt
{
    public const string PageGpu = "GPU", PageGpuSubtitle = "NVIDIA monitoring, overclocking, thermal target, V/F curve and fan control.";
    public const string NvmlMissing = "NVIDIA's management library (nvml.dll) wasn't found. It's installed with the NVIDIA driver.";
    public static string GpuInitFailed(string reason, int code) => $"NVIDIA's management library couldn't start: {reason} ({code}). Use Refresh to try again.";
    public const string GpuNoNvidia ="No NVIDIA GPU was found.";
    public static string GpuOtherAdapters(IEnumerable<string> names) =>
        $"Not supported yet (only NVIDIA cards are supported for now): {string.Join(", ", names)}.";
    public static string GpuNotExported(string function) => $"This driver doesn't provide {function}. Updating the NVIDIA driver may enable it.";
    public const string GpuUnnamed = "NVIDIA GPU";
    public static string GpuInvalidDevice(string id) => $"No NVIDIA GPU has the PCI bus id {id}.";
    public static string GpuDriver(string? version) => version is null ? "" : $"Driver {version}";
    public static string GpuReadFailed(string message) => $"The GPU couldn't be read: {message}";
    public const string GpuDriverReset = "The display driver restarted: readings are back. Its clocks, limits and fans went back to defaults; apply your settings again (or turn on Apply at sign-in to have them reapplied by themselves).",
        GpuDriverResetReapplied = "The display driver restarted: readings are back and the settings saved for sign-in were applied again.",
        GpuDriverResetGovernor = "The display driver restarted: readings are back; KOS Governor is applying the settings saved for sign-in again.";

    // ---------- monitoring ----------
    public const string GpuMonitor = "Live readings", GpuMonitorNote = "Updated every second while this page is open.";
    public const string GpuTemperature = "Temperature", GpuCoreClock = "Core clock", GpuMemoryClock = "Memory clock", GpuSmClock = "SM clock",
        GpuLoad = "GPU load", GpuMemoryControllerLoad = "Memory controller load", GpuPowerDraw = "Power draw", GpuVram = "VRAM", GpuPcie = "PCIe link",
        GpuPState = "Dynamic P-state", GpuFanSpeed = "Fan speed", GpuThermalSlowdown = "Thermal slowdown",
        GpuHotspot = "Hotspot temperature", GpuMemoryJunction = "Memory junction", GpuVoltage = "GPU voltage", GpuBandwidth = "PCIe bandwidth";
    public static string GpuFanN(int index) => $"Fan {index + 1}";
    public static string GpuPcieLink(uint? gen, uint? width, uint? genMax, uint? widthMax) =>
        $"Gen {N(gen)} x{N(width)} (max Gen {N(genMax)} x{N(widthMax)})";
    public static string GpuVramUsage(ulong used, ulong total) => FormattableString.Invariant($"{used / 1048576:N0} / {total / 1048576:N0} MB");
    public static string GpuPowerDrawText(uint mw, uint? limitMw) => limitMw is { } l ? $"{W1(mw)} / {W(l)} W" : $"{W1(mw)} W";
    static string W1(uint mw) => (mw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
    static string N(uint? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "?";

    // ---------- gamemode (KOS Governor) ----------
    public const string GpuGamemodeTitle = "Gamemode",
        GpuGamemodeNote = "KOS Governor writes these profiles when its gamemode turns on and off (by hand or automatically with a game). Before writing the gamemode profile it saves what the card has, and \"Back to how it was\" puts that back when gamemode turns off. A profile with a fan curve runs that curve while it is on. The profile's values are copied when you choose it; saving the profile here updates them.",
        GpuGamemodeOn = "When gamemode turns on", GpuGamemodeOff = "When gamemode turns off",
        GpuGamemodeNone = "Don't change the card", GpuGamemodeBack = "Back to how it was",
        GpuGamemodeNeedsAdmin = "Choosing gamemode profiles needs the toolbox running as administrator (Settings › Restart as administrator).";
    public static string GpuGamemodeSaved(string on, string off, string? onValues, string? offValues) =>
        $"KOS Governor gamemode: on → {on}{(onValues is null ? "" : $" ({onValues})")}; off → {off}{(offValues is null ? "" : $" ({offValues})")}.";
    public static string GpuGamemodeInvalid(string profile, string problem) => $"Profile \"{profile}\" can't be used for gamemode on this card: {problem}";
    public static string GpuGamemodeSuspended(string profile) =>
        $"Profile \"{profile}\" is paused: KOS Governor started writing it and never finished (the PC may have crashed). Choose it again to turn it back on.";

    // ---------- tuning ----------
    public const string GpuTuningTitle = "Tuning";
    public const string GpuTuningNote = "Overclocking can make games, apps or the driver crash, and a wrong fan speed can overheat the card. Changes reset when the driver or Windows restarts; \"Apply at sign-in\" writes them again at every sign-in, and opening the toolbox applies the chosen profile. Changing these values needs administrator rights.";
    public const string GpuPowerLimit = "Power limit (W)", GpuCoreOffset = "Core clock offset (MHz)", GpuMemOffset = "Memory clock offset (MHz)", GpuFan = "Fan speed (%)";
    public const string GpuThermalLimit = "Temperature target (°C)", GpuVfCurve = "V/F curve", GpuVfLock = "V/F point lock";
    public const string GpuClockLock = "Clock lock";
    public const string GpuClockLockOutOfRange = "The clock lock must be between 100 and 5000 MHz.";
    public const string GpuThermalNote = "The temperature target changes the driver's target, not the card's shutdown threshold.";
    public const string GpuThermalSlowdownWarning = "Not recommended. Off stops the driver from lowering the clocks when the card gets too hot, so it can run past its temperature target. Leave it on.";
    public const string GpuThermalSlowdownUnavailable = "The driver does not expose thermal slowdown control for this card.";
    public static string GpuStartSlowdownKept(string profile) =>
        $"Profile \"{profile}\" turns thermal slowdown off. Opening the toolbox leaves it on: to turn it off, press Apply on the GPU page.";
    public const string GpuCurveNote = "Choose a voltage point and stage its clock. Apply writes only the selected point deltas; the voltage anchors are fixed by the driver.";
    public const string GpuCurveUnavailable = "The driver does not expose an editable V/F curve for this card.";
    public const string GpuLockUnavailable = "The driver does not expose V/F point locking for this card.";
    public const string GpuThermalUnavailable = "The driver does not expose an adjustable temperature target for this card.";
    public const string GpuCurveInvalid = "A V/F point no longer matches the driver or exceeds its clock range. Refresh the page.";
    public static string GpuThermalOutOfRange(int min, int max) => $"The temperature target must be from {min} to {max} °C.";
    public static string GpuThermalRange(int min, int max, int def) => $"{min}–{max} °C · default {def} °C";
    public static string GpuCurveRange(int min, int max) => $"Per-point offset: {min:+0;-0;0} to {max:+0;-0;0} MHz";
    public const string GpuFanFixed = "Fixed speed";
    public const string GpuMemOffsetNote = "In the units NVML uses for the memory clock domain. Some tools (for example MSI Afterburner) show a different scale, so the same number may not mean the same change.";
    public const string GpuFanFixedNote = "A fixed speed stays until you choose Automatic again or the driver restarts; the card doesn't speed the fan up when it gets hot.";
    public static string GpuWattsRange(uint minMw, uint maxMw, uint defMw) => $"{W(minMw)}–{W(maxMw)} W · default {W(defMw)} W";
    public static string GpuMhzRange(int min, int max) => $"{min:+0;-0;0} to {max:+0;-0;0} MHz";
    public static string GpuPercentRange(uint min, uint max) => $"{min}–{max} % (applied to every fan)";
    public static string W(uint mw) => (mw / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    public const string GpuResetDefaults = "Reset to defaults", GpuResetDefaultsTooltip = "Stages default power and temperature targets, zero offsets, a stock V/F curve and automatic fan control";
    public const string GpuEnterWatts = "Enter the power limit in watts, for example 100 or 92.5.";
    public const string GpuEnterTemperature = "Enter a whole temperature in °C.";
    public const string GpuEnterMhz = "Enter a whole number of MHz, for example 100 or -50.";
    public const string GpuEnterVfMhz = "Enter a positive clock in MHz (up to three decimal places).";
    public const string GpuEnterPercent = "Enter a whole percentage.";
    public static string GpuPending(int count) => count == 1 ? "1 change not applied" : $"{count} changes not applied";
    public static string GpuApplied(int count) => $"{count} value{(count == 1 ? "" : "s")} applied and verified.";
    public static string GpuNotConfirmed(IEnumerable<string> names) => $"The GPU reports different values for: {string.Join(", ", names)}. Details are in the log.";
    public const string GpuNoChanges = "Nothing to apply: these are the current values.";

    // ---------- validation ----------
    public const string GpuPowerFixed = "This card doesn't allow changing its power limit.";
    public const string GpuNoFans = "This card has no fans the driver can control.";
    public static string GpuFanRangeInvalid(uint min, uint max) => $"The driver reported an invalid fan range ({min}–{max} %).";
    public const string GpuOffsetNotSupported = "The driver reports that clock offsets aren't supported on this card through NVML (NVIDIA's public API). Tools like MSI Afterburner use other, private interfaces.";
    public const string GpuOffsetFixed = "The driver reports no adjustable range for this clock.";
    public const string GpuNoOffsetApi = "This driver doesn't offer clock offsets through NVML (nvmlDeviceSetClockOffsets or the older VF offset functions).";
    public const string GpuNothingToApply = "The request has no values to apply.";
    public static string GpuPowerOutOfRange(uint minMw, uint maxMw) => $"The power limit must be from {W(minMw)} to {W(maxMw)} W.";
    public static string GpuOffsetOutOfRange(int min, int max) => $"The offset must be from {min} to {max} MHz.";
    public const string GpuFanMixed = "Choose either automatic fan control or a fixed speed.";
    public static string GpuFanOutOfRange(uint min, uint max) => $"The fan speed must be from {min} to {max} %.";

    // ---------- apply at logon ----------
    public const string GpuSignIn = "Apply at sign-in";
    public const string GpuSignInNote = "Saves the current values (power, temperature, offsets, V/F points and fan) and applies them each time you sign in, through the scheduled task \"KOS Toolbox GPU tuning\". The values are checked against the card's limits every time.";
    public const string GpuSignInNeedsProgramFiles = "\"Apply at sign-in\" runs the toolbox as administrator at every sign-in, so it needs the toolbox installed in Program Files.";
    public const string GpuPendingFirst = "Apply or discard the pending changes first.";
    public const string GpuSignInOn = "Saved. These values are applied at every sign-in.", GpuSignInOff = "The sign-in task was removed.";
    public const string GpuSignInNotConfirmed = "The scheduled task wasn't found after the change. Details are in the log.";
    public const string GpuSignInStillOn = "The scheduled task still exists after the change. Details are in the log.";
    // ---------- profile applied when the toolbox opens ----------
    public static string GpuStartApplied(string profile) => $"GPU profile \"{profile}\" applied.";
    public static string GpuStartFailed(string profile, string? reason) => $"GPU profile \"{profile}\" wasn't applied: {reason}";
    public static string GpuStartInvalid(string profile) => $"GPU profile \"{profile}\" has values this card doesn't accept, so it wasn't applied. Open GPU › Overclock to fix them.";
    public static string GpuStartSkipped(string profile) =>
        $"GPU profile \"{profile}\" wasn't applied: the last time, Windows or the toolbox closed while it was being applied. Apply it on GPU › Overclock to turn this back on.";
    public static string GpuFolderIsLink(string path) => $"{path} is a link to another folder; for safety the GPU settings aren't saved or loaded there.";
}
