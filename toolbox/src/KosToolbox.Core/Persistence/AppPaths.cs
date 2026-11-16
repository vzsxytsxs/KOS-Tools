namespace KosToolbox.Core.Persistence;

public static class AppPaths
{
    /// <summary>Root of the per-user data. Overridable in tests.</summary>
    public static string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOS", "Toolbox");

    /// <summary>The KOS folder under ProgramData, resolved by Windows (doesn't assume the C: drive).</summary>
    public static string TheKosRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TheKOS");

    public static string LogsDirectory => Path.Combine(TheKosRoot, "Logs", "Toolbox");
    public static string Backups => Path.Combine(DataRoot, "backups.json");
    /// <summary>Changes made to catalog settings, before and after (Settings › Change history).</summary>
    public static string History => Path.Combine(DataRoot, "history.json");
    public static string Pending => Path.Combine(DataRoot, "pending-reboot.json");
    public static string Settings => Path.Combine(DataRoot, "settings.json");
    /// <summary>Previous startup type of services and drivers changed on the Startup page.</summary>
    public static string StartupBackups => Path.Combine(DataRoot, "startup-backups.json");
}
