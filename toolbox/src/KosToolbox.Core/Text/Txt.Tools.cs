namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public static string ToolMissing(string path) => $"The program no longer exists: {path}";
    public static string ToolOwnerProblem(string path, string? owner) =>
        $"{path} is owned by {owner ?? "an unknown account"}, not by SYSTEM, Administrators or TrustedInstaller. A standard user could have replaced it, and it would open with the toolbox's administrator rights.";
    public static string ToolOwnerUnknown(string path, string message) => $"Couldn't check who owns {path}: {message}";

    // ---------- fixes ----------
    public static string CommandFailed(string command, int code, string output) => $"{command} failed (exit code {code}): {output}";
    public const string RestartingToFirmware = "Restarting into the UEFI firmware settings.";
    public static string NetworkResetDone(int values) =>
        $"Network reset: {values} adapter power and wake overrides removed, Winsock and TCP/IP reset, DNS and ARP caches flushed. Restart to finish.";
    public static string TimeSynced(string servers) => $"Windows Time registered again and synced with {servers}.";
    public static string TimeServiceFixedNoSync(string servers) =>
        $"Windows Time registered again with {servers}, but the sync didn't answer yet. Check the internet connection; Windows retries on its own.";
    public const string BootExecuteAlreadyThere = "BootExecute is already there, so partition tools can schedule their restart job: nothing changed.";
    public const string BootExecuteFixed = "BootExecute is back (empty, so Windows still skips its disk check at boot). Partition tools such as MiniTool Partition Wizard can schedule their restart job again.";
    public const string BootExecuteNotWritten = "BootExecute couldn't be written: reading it back didn't give an empty multi-string.";
    public const string SecConfigMissing = "SecConfig.efi isn't in System32, so the UEFI lock can't be cleared on this installation.";
    public const string VbsNotLocked = "VBS isn't kept on by a UEFI lock: either it's off, or the registry still turns it on. Turn VBS off and restart first; use this only if it still runs after that.";
    public const string NoFreeDriveLetter = "No free drive letter to mount the EFI partition.";
    public const string VbsUnlockScheduled = "Scheduled for the next restart: confirm at boot when SecConfig asks (press the key it shows), and the VBS UEFI lock is cleared.";
}
