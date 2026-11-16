namespace KosToolbox.Core.Text;

public static partial class Txt
{
    // ---------- BIOS page (AMI SCEWIN) ----------
    public const string PageBios = "BIOS";
    public const string PageBiosSubtitle = "Read the BIOS (UEFI) settings with AMI SCEWIN, change them, and keep the previous settings as a backup at every change.";
    public const string BiosTabSettings = "Settings", BiosTabBackups = "Backups";
    public const string BiosRead = "Read BIOS", BiosOpenTools = "Open tools folder", BiosFilter = "Filter settings";
    public const string BiosReadTooltip = "Runs SCEWIN (administrator) and saves every BIOS setting to nvram.txt. Nothing is written to the BIOS.";
    public const string BiosReading = "Reading the BIOS with SCEWIN…", BiosWriting = "Writing to the BIOS with SCEWIN…";
    public static string BiosReadDone(int count) => $"BIOS read: {count} settings saved to nvram.txt.";
    public const string BiosNotRead = "The BIOS hasn't been read yet. Read BIOS saves all its settings to nvram.txt; SCEWIN needs administrator rights.";
    public static string BiosStatus(DateTime at, int count, string? crc, string? version) =>
        $"Read {at:g} · {count} settings · HIICrc32 {crc ?? "?"}{(version is null ? "" : $" · SCEWIN {version}")}";
    public static string BiosFewSettings(int count) =>
        $"SCEWIN found only {count} settings: this BIOS doesn't make the others available to Windows. On some ASUS and ASRock boards, " +
        "turning on Publish HII Resources or turning off Password protection of Runtime Variables in the BIOS makes them available.";
    public const string BiosOnlyNotDefault = "Different from BIOS default", BiosOnlyPending = "Only pending changes";
    public const string BiosNoMatches = "No settings match the filter.";
    public static string BiosCount(int shown, int total) => shown == total ? $"{total} settings" : $"{shown} of {total} settings";
    public static string BiosPending(int n, int invalid) => string.Join(" · ", new[]
    {
        n == 0 ? null : n == 1 ? "1 pending change" : $"{n} pending changes",
        invalid == 0 ? null : invalid == 1 ? "1 invalid value (fix or discard it to apply)" : $"{invalid} invalid values (fix or discard them to apply)",
    }.Where(s => s is not null));
    public const string BiosApplyNote = "Nothing is written before Apply. The toolbox reads the BIOS again, saves the current settings as a backup, writes, and reads again to confirm. Restart for the BIOS to use them.";
    public static string BiosDefault(string value) => $"Default: {value}";
    public static string BiosWas(string value) => $"Was: {value}";
    public const string BiosNotSet = "Not set";
    public const string BiosDiscardSetting = "Discard this change";
    public static string BiosNumberHint(int bytes) => $"Whole number ({bytes} byte{(bytes == 1 ? "" : "s")}). The BIOS checks its own limits when it's written.";
    public const string BiosReadOnlyHint = "Shown only: the toolbox doesn't change this kind of setting.";
    public const string BiosPendingName = "BIOS settings";
    public const string BiosPendingFirst = "Apply or discard the pending changes first.";

    // validation
    public static string BiosNotAnOption(string name) => $"\"{name}\": the value isn't one of the options of this BIOS.";
    public const string BiosCheckboxValue = "Use 0 (disabled) or 1 (enabled).";
    public const string BiosNotANumber = "Type a whole number.";
    public static string BiosOutOfRange(string min, string max) => $"Use a number from {min} to {max}.";
    public static string BiosNotEditable(string name) => $"\"{name}\" can't be changed here.";
    public const string BiosQuestionMissing = "A setting to write doesn't exist in the BIOS anymore. Nothing was written.";
    public const string BiosLayoutChanged = "The BIOS settings layout changed since the last read (another HIICrc32, for example after a BIOS update). Nothing was written; the settings shown now are the current ones.";

    // SCEWIN
    public static string ScewinMissing(string tools) =>
        $"SCEWIN wasn't found in {tools}. Copy SCEWIN_64.exe, amifldrv64.sys and amigendrv64.sys into a folder there (for example tools\\SCEWIN).";
    public static string ScewinFileMissing(string path) => $"{path} is missing; SCEWIN needs its two drivers in the same folder.";
    public static string ScewinIsLink(string path) => $"{path} is a link to another file, so SCEWIN wasn't run. Copy the real file there.";
    public static string ScewinNotSigned(string path) => $"{path} isn't signed by AMI (American Megatrends), so it wasn't run.";
    public const string ScewinTimedOut = "SCEWIN didn't finish in 3 minutes and was stopped.";
    public static string ScewinFailed(int code, string details) => $"SCEWIN failed (exit code {code}). {details}".TrimEnd();
    public const string ScewinInvalidExport = "SCEWIN didn't write a valid nvram.txt (it has no HIICrc32 line).";
    public const string ScewinDriverHint = "SCEWIN couldn't load its driver. Windows blocks it while Core isolation (memory integrity) or the Vulnerable driver blocklist is on (Windows › Security).";
    public const string ScewinNotCompatibleHint = "SCEWIN only works with AMI Aptio BIOSes, and newer boards need a newer SCEWIN.";
    public static string BiosNoResult(string logs) => $"The elevated task didn't report a result. Details are in the log ({logs}).";

    // write result
    public static string BiosApplied(int ok, int total, string? backup) =>
        (ok == total ? $"{total} BIOS setting{(total == 1 ? "" : "s")} written and confirmed by reading the BIOS again. Restart to use {(total == 1 ? "it" : "them")}."
            : $"{ok} of {total} BIOS settings written and confirmed. Restart to use them.")
        + (backup is null ? "" : $" Previous settings saved as {backup}.");
    public static string BiosNotApplied(IEnumerable<string> names, IEnumerable<string> messages) =>
        $"Not written: {string.Join(", ", names)}.{(messages.Any() ? " SCEWIN: " + string.Join(" ", messages) : "")}";
    public const string BiosNothingWritten = "Nothing was written to the BIOS.";
    public static string BiosBackupKept(string backup) => $"The settings from before were saved as {backup}.";

    // backups
    public const string BiosBackupsNote = "Before every write, the toolbox saves the BIOS settings in use as a backup (an nvram.txt that SCEWIN can import). Choose one to compare it with the last read and restore what you check.";
    public const string BiosNoBackups = "No backups yet. The toolbox saves one every time it writes to the BIOS.";
    public static string BiosBackupChanged(IReadOnlyList<string> names) =>
        names.Count == 0 ? "" : $"Before: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? $" (+{names.Count - 3})" : "")}";
    public const string BiosNotBooted = "Saved after another change, before a restart: the PC hasn't used these settings yet.";
    public static string BiosBackupStats(int diffs, int checkedCount, DateTime? current) =>
        $"{diffs} difference{(diffs == 1 ? "" : "s")} · {checkedCount} checked{(current is { } at ? $" · current = read {at:g}" : "")}";
    public static string BiosOtherLayout(string? backup, string? current) =>
        $"This backup is from another BIOS layout (HIICrc32 {backup ?? "?"}, now {current ?? "?"}), so it can't be restored.";
    public const string BiosCompareNeedsRead = "Read the BIOS to compare the backups with the current settings.";
    public const string ColSetting = "Setting", ColInBackup = "In backup";
    public const string BiosNotInBackup = "Not in the backup", BiosNotInBios = "Not in the BIOS now";
    public const string BiosCantRestoreValue = "The value in the backup isn't valid for this setting now.";
    public const string BiosRestoreNote = "Restoring writes the checked settings to the BIOS, the same way as Apply: the current settings become a new backup first. Restart afterwards.";
    public const string BiosRestoreChecked = "Restore checked";
    public const string BiosSettingsCountFormat = "{0} settings";
}
