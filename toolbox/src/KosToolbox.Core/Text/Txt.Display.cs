namespace KosToolbox.Core.Text;

/// <summary>Texts for the Display page (EDID and custom resolutions).</summary>
public static partial class Txt
{
    public const string PageDisplay = "Display", PageDisplaySubtitle = "Monitor EDID: custom resolutions, refresh range (VRR) and HDMI audio.";

    // ---------- monitor controls (DDC/CI) ----------
    public const string DdcTitle = "Monitor controls (DDC/CI)", DdcBrightness = "Brightness", DdcContrast = "Contrast", DdcInput = "Input source",
        DdcRestore = "Restore factory settings", DdcRestoreRow = "Monitor's own menu",
        DdcRestoreTip = "Puts every setting of the monitor's own menu back to the factory defaults (brightness, contrast, color, overdrive…).",
        DdcInputTip = "Switches the monitor to another input: if nothing is connected there, this screen goes dark until you switch back with the monitor's buttons.",
        DdcReading = "Reading the monitor over DDC/CI…",
        DdcNotAvailable = "This monitor doesn't answer DDC/CI. Turn DDC/CI on in the monitor's own menu; laptop panels and some adapters and docks don't pass it through.",
        DdcNothingListed = "The monitor answers DDC/CI but lists none of brightness, contrast, input source or factory reset.",
        DdcRestored = "The monitor's settings were put back to the factory defaults.",
        DdcNotConfirmed = "The monitor took the change but didn't report the new value back; check it on screen.",
        DdcInputNotConfirmed = "The monitor reports another input than the one chosen.";
    public static string DdcWriteFailed(string message) => $"The monitor didn't take the change: {message}";
    public const string DisplayNote = "Like CRU: the changes are saved as an EDID override in the registry and only take effect after the display driver restarts or Windows restarts. A timing the monitor can't show leaves the screen black: wait 15 seconds without touching anything, or start Windows in Safe Mode and use Restore monitor EDID.";

    // ---------- core ----------
    public const string EdidInvalid = "This isn't a valid EDID.", EdidNoCta = "This EDID has no CTA-861 extension block, where extra resolutions and HDMI audio are stored.",
        EdidNoSpace = "There's no free space left in the extension block. Remove a resolution first.", EdidBaseTimingFixed = "The native resolution in the base block can't be removed.",
        EdidRangeInvalid = "Enter a minimum and maximum refresh from 1 to 510 Hz, minimum not above maximum.", EdidNoRange = "This EDID has no range limits descriptor to edit.",
        EdidNoBaseSlot = "The base block has no free descriptor. Delete one (or add the resolution to an extension block).",
        EdidCtaFull = "There's no room left in this extension block. Remove a resolution or a data block first.";
    public static string EdidMaxBlocks(int max) => $"An EDID override holds at most {max} extension blocks.";
    /// <summary>"1 resolution", "3 resolutions".</summary>
    public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    // ---------- page ----------
    public const string Monitor = "Monitor", ActiveMonitor = "active", OverridePill = "Override saved", NoMonitors = "No monitor EDID found in the registry.",
        EdidSourceMonitor = "Showing the monitor's own EDID.", EdidSourceOverride = "Showing the saved EDID override (not the monitor's own EDID).";
    public const string Resolutions = "Resolutions", NativePill = "Native", AddResolution = "Add resolution", ColWidth = "Width", ColHeight = "Height", ColRefresh = "Refresh (Hz)",
        TimingMode = "Timing", TimingSameAsNative = "Native timing, new refresh rate", TimingCvtRb = "CVT reduced blanking";
    public const string RefreshRange = "Refresh range (VRR)", SetRange = "Set range", MinHz = "Minimum Hz", MaxHz = "Maximum Hz", NoRangeDescriptor = "No range limits descriptor.",
        HdmiAudio = "HDMI audio", EnableHdmiAudio = "Enable HDMI audio", AudioOn = "Basic audio is on.", AudioOff = "Basic audio is off.";
    public const string SaveOverride = "Save override", RestoreMonitorEdid = "Restore monitor EDID", RestartDisplayDriver = "Restart display driver";
    public static string EdidInfo(string manufacturer, int product, string version, int year, int widthCm, int heightCm, int extensions) =>
        $"{manufacturer} {product:X4} · EDID {version} · {year} · {widthCm} × {heightCm} cm · {extensions} extension block{(extensions == 1 ? "" : "s")}";
    public static string TimingText(DetailedTimingInfo t) => $"{t.Width} × {t.Height} @ {t.Refresh:0.###} Hz · {t.PixelClockMhz:0.00} MHz · total {t.HTotal} × {t.VTotal}";
    public static string RangeText(int minV, int maxV, int maxPclk) => $"{minV}–{maxV} Hz vertical · up to {maxPclk} MHz";
    public static string EdidPending(int changes) => changes == 1 ? "1 change not saved." : $"{changes} changes not saved.";
    public const string EnterWholeNumber = "Enter whole numbers.", EnterRefresh = "Enter a refresh rate, for example 75 or 143.856.",
        EdidNoNative = "This EDID has no native timing to copy; use CVT reduced blanking.";
    public static string EdidChangeAdded(string timing) => $"Add {timing}";
    public static string EdidChangeRemoved(string timing) => $"Remove {timing}";
    public static string EdidChangeRange(int min, int max) => $"Refresh range {min}–{max} Hz";
    public static string TimingTooFast(double mhz) => $"That needs a {mhz:0.##} MHz pixel clock; an EDID timing can hold at most 655.35 MHz.";

    public const string OverrideSaved = "Override saved and verified. Restart the display driver (or Windows) to use it.",
        OverrideRemoved = "Override removed and verified. Restart the display driver (or Windows) to use the monitor's EDID.";
    public static string DisplayRestarted(bool reboot) => reboot ? "Windows needs a restart to finish restarting the display driver." : "Display driver restarted.";
    public static string DisplayReadFailed(string message) => $"The monitors couldn't be read: {message}";
}

/// <summary>Values of a timing for the texts (Txt doesn't reference the Display core).</summary>
public sealed record DetailedTimingInfo(int Width, int Height, double Refresh, double PixelClockMhz, int HTotal, int VTotal);
