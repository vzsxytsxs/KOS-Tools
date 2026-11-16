namespace KosGovernor.Core;

/// <summary>Daily log in %ProgramData%\TheKOS\Logs\Governor (interface and engine, both elevated). Keeps 30 days.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string Directory => Path.Combine(ConfigStore.Root, "Logs", "Governor");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    public static void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Directory).EnumerateFiles("governor-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30)))
                f.Delete();
        }
        catch { /* folder doesn't exist yet or file in use */ }
    }

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (Gate)
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(Path.Combine(Directory, $"governor-{DateTime.Now:yyyyMMdd}.log"), line);
                    return;
                }
                catch (IOException) { Thread.Sleep(20); } // the other process is writing
                catch (UnauthorizedAccessException) { return; }
            }
    }
}
