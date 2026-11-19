using System.Globalization;
using KosToolbox.Core.Audio;

namespace KosToolbox.Core.Text;

/// <summary>Texts for KOS Equalizer (its window shows one page; the per-device audio settings are the Audio page's).</summary>
public static partial class Txt
{
    const string OtherEq = EqRegistration.OtherEqualizerName;

    public const string PageEqualizer = "Equalizer",
        PageEqualizerSubtitle = "A parametric or graphic equalizer for each playback device, with a profile for gamemode.";
    public const string EqNone = "No playback device found.", EqNotConnected = "Not connected";

    // ---------- KOS Equalizer on this device ----------
    public const string EqCardTitle = "KOS Equalizer on this device",
        EqStateOn = "On", EqStateOff = "Off", EqStateOther = "Off · " + OtherEq + " is on this device",
        EqStateChanged = "Changed since it was turned on", EqStateUnsupported = "Not available on this device",
        EqTurnOn = "Turn on", EqTurnOff = "Turn off",
        EqBusy = "Working… Windows Audio restarts, so sound stops for a moment.",
        EqRestartNote = "Turning it on or off restarts Windows Audio: sound stops for a moment.",
        EqOtherNote = OtherEq + " is on this device. Turning KOS Equalizer on replaces it; turning KOS Equalizer off puts it back.",
        EqReplacedNote = "Replaced " + OtherEq + " on this device; turning KOS Equalizer off puts it back.",
        EqEffectNone = "The effect hasn't reported yet: it starts with the next sound on this device.",
        EqNotConnectedNote = "This device isn't connected, but KOS Equalizer is still on for it: turn it off here, so its registration goes away.",
        EqOffHint = "KOS Equalizer is off for this device: its profiles are saved and play once it's on.",
        EqClosingAfterChange = "KOS Equalizer closes once the change in progress finishes.";
    public static string EqEffect(string line, DateTime? time) => time is { } t ? $"Effect: {line} ({t:g})" : $"Effect: {line}";
    public static string EqStatusFailed(string error) => $"The state couldn't be read: {error}";
    public static string EqJournalFailed(string error) => $"KOS Equalizer's registration record couldn't be read: {error}";

    public static string EqTurnedOn(string device) => $"KOS Equalizer is on for {device}.";
    public static string EqReplacedToast(string device) => $"{device}: KOS Equalizer replaced {OtherEq}; turning KOS Equalizer off puts it back.";
    public static string EqOnWithError(string device, string error) => $"KOS Equalizer is on for {device}, but: {error}";
    public static string EqTurnOnFailed(string device, string error) => $"Turning KOS Equalizer on for {device} failed: {error}";
    public static string EqTurnedOff(string device, bool otherBack) =>
        $"KOS Equalizer is off for {device}." + (otherBack ? $" {OtherEq} is on it again." : "");
    public static string EqTurnedOffLeft(string device, IEnumerable<string> left) =>
        $"KOS Equalizer is off for {device}. Changed since it was turned on, so left as found: {string.Join(", ", left)}.";
    public static string EqOffWithError(string device, string error) => $"KOS Equalizer is off for {device}, but: {error}";
    public static string EqTurnOffFailed(string device, string error) => $"Turning KOS Equalizer off for {device} failed: {error}";
    public static string EqNotConfirmed(string device, string state) => $"{device}: the change didn't take; it reads back {state}.";

    // ---------- profiles ----------
    public const string EqProfilesTitle = "Profiles", EqActiveLabel = "Active profile", EqFlat = "Flat (no profile)",
        EqNew = "New", EqDuplicate = "Duplicate", EqDelete = "Delete", EqRename = "Rename", EqNameLabel = "Name",
        EqGamemodeLabel = "Gamemode profile", EqGamemodeNone = "None (the active profile stays)",
        EqGamemodeHint = "KOS Engine switches this device to the gamemode profile while gamemode is on, and back afterwards.",
        EqGamemodeNowOn = "Gamemode is on now: the device plays its gamemode profile.",
        EqNewName = "Profile", EqImportedName = "Imported",
        EqNameLength = "A name has 1 to 64 characters.", EqNameTaken = "Another profile has this name.";
    public static string EqCopyName(string name) => $"{name} copy";
    public static string EqConfigFailed(string error) => $"The profiles couldn't be read, so nothing is saved over them: {error}";
    public static string EqActiveSet(string? profile) => $"Active profile: {profile ?? "flat"}.";
    public static string EqGamemodeSet(string? profile) => $"Gamemode profile: {profile ?? "none"}.";
    public static string EqCreated(string profile) => $"Created {profile}.";
    public static string EqDeleted(string profile) => $"Deleted {profile}.";
    public static string EqRenamed(string from, string to) => $"Renamed {from} to {to}.";
    /// <summary>After a save: what the device plays now (the settings file was written and read back), or that it plays once on.</summary>
    public static string EqPlays(string device, bool on, string? profile) =>
        on ? $"{device} plays {profile ?? "flat"}." : $"Saved; it plays once KOS Equalizer is on for {device}.";
    public static string EqApplyFailed(string device, string error) => $"{device}: saving or playing the profile failed: {error}";

    // ---------- editor ----------
    public const string EqEditorTitle = "Editor", EqParametric = "Parametric", EqPreamp = "Preamp",
        EqSuggestedTip = "A preamp that keeps the curve at or below 0 dB, so loud passages don't clip.",
        EqFlatEditor = "Flat: the device plays unchanged. Choose a profile above or create one.",
        EqAddBand = "Add band", EqRemoveBand = "Remove band", EqBandOn = "On", EqType = "Type",
        EqFrequencyHeader = "Frequency (Hz)", EqGainHeader = "Gain (dB)", EqFrequency = "frequency", EqGain = "gain", EqQ = "Q",
        EqNoBands = "No bands yet: Add band puts a peak filter at 1 kHz.", EqResponse = "Frequency response";
    public static string EqGraphicBands(int bands) => $"{bands} bands";
    public static string EqSuggested(double preamp) => $"Suggested ({EqDb(preamp)})";
    public static string EqNotANumber(int band, string field) => $"Band {band}: the {field} isn't a number.";
    public static string EqFilterName(EqFilterType type) => type switch
    {
        EqFilterType.LowShelf => "Low shelf", EqFilterType.HighShelf => "High shelf", EqFilterType.LowPass => "Low pass",
        EqFilterType.HighPass => "High pass", EqFilterType.Notch => "Notch", EqFilterType.BandPass => "Band pass", _ => "Peak",
    };
    public static string EqDb(double db) => $"{EqDbShort(db)} dB";
    public static string EqDbShort(double db) => db.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
    /// <summary>"31.5", "1k", "1.25k", "12.5k".</summary>
    public static string EqHz(double hz) => hz >= 1000 ? (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture) + "k" : hz.ToString("0.#", CultureInfo.InvariantCulture);
    /// <summary>A band's number as the editor shows it (a dot for decimals, like the import format).</summary>
    public static string EqNumber(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    public static string EqGraphicTip(double hz, double db) => $"{EqHz(hz)} Hz: {EqDb(db)}";

    // ---------- import and export ----------
    public const string EqImportTitle = "Import and export", EqImportFile = "Import a file…", EqExport = "Export…",
        EqPasteLabel = "Or paste Equalizer APO or AutoEQ text (Preamp, Filter and GraphicEQ lines):", EqImportPaste = "Import text",
        EqExportHint = "Export writes the active profile as Equalizer APO text.",
        EqFileFilter = "Equalizer text (*.txt)|*.txt|All files (*.*)|*.*",
        EqImportDialogTitle = "Import an equalizer profile", EqExportDialogTitle = "Export the profile",
        EqImportTooBig = "The file is larger than 1 MB: not an equalizer profile.",
        EqImportNothing = "Nothing to import: no Preamp, Filter or GraphicEQ line was understood.";
    public static string EqImported(string profile) => $"Imported {profile}.";
    public static string EqImportFailed(string error) => $"Importing failed: {error}";
    public static string EqImportIgnored(IReadOnlyList<string> lines) =>
        "Left out of the import: " + string.Join("; ", lines.Take(5)) + (lines.Count > 5 ? $"; and {lines.Count - 5} more." : ".");
    public static string EqExported(string profile, string path) => $"Exported {profile} to {path}.";
    public static string EqExportFailed(string error) => $"Exporting failed: {error}";

    // ---------- device settings and the list ----------
    public const string EqDeviceSettings = "Windows audio settings",
        EqRowOn = "KOS Equalizer on", EqRowOff = "Off", EqRowOther = OtherEq, EqRowChanged = "Changed since", EqRowUnsupported = "Not available";
    /// <summary>The list's second line: default, not connected, KOS Equalizer's state and the active profile.</summary>
    public static string EqRowText(bool isDefault, bool connected, string? state, string? profile) => string.Join(" · ", new[]
    {
        isDefault ? AudioDefaultBadge : null, connected ? null : EqNotConnected, state, profile,
    }.Where(s => !string.IsNullOrEmpty(s)));
}
