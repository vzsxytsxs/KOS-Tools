namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public const string ServicesKeyMissing = @"HKLM\SYSTEM\CurrentControlSet\Services couldn't be opened.";
    public const string KeyNotOpened = "the key couldn't be opened";
    public static string ServiceUnreadable(string name, string message) => $"Couldn't read service '{name}': {message}";
    public static string TooFewServices(int count) => $"Only {count} service entries were read; an incomplete snapshot was refused.";
    public const string InvalidSnapshotName = "Use only letters, numbers, dot, underscore and hyphen in the snapshot name.";
    public static string SnapshotMissing(string path) => $"Snapshot not found: {path}";
    public static string SnapshotInvalid(string path, string reason) => $"Invalid snapshot file {path} ({reason}).";
    public static string SnapshotIncomplete(string path) => $"Incomplete snapshot: {path} (fewer than 20 services, or the service count doesn't match).";
    public static string SnapshotNewer(string path, int version) => $"{path} uses format version {version}, which is newer than this toolbox supports.";
    public const string SnapshotValidationFailed = "The snapshot file failed validation; nothing was replaced.";
    public const string RegValidationFailed = "The .reg reference file failed validation; nothing was replaced.";
    public static string UnexpectedStart(string name) => $"Unexpected Start value for {name}.";
    public const string RestoreNeedsNames = "Choose a snapshot and at least one service. Nothing is restored by default.";
    public const string RestoreFromCurrent = "You can't restore from the current state.";
    public static string InvalidServiceName(string name) => $"Invalid service name: {name}";
    public static string ServiceNotInSnapshot(string name) => $"The service {name} isn't in the snapshot.";
    public static string ServiceDeleted(string name) => $"The service {name} no longer exists; deleted services are never recreated.";
    public static string RestoreMismatch(string actual, string expected) => $"Now {actual}, expected {expected}.";
    public const string ServiceMissingState = "Missing (deleted)";
}
