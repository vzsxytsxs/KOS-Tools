namespace KosToolbox.Core.Startup;

/// <summary>Identifies the file run by a command line, only for the icon and details (nothing is executed).</summary>
public static class CommandLine
{
    public static string? Executable(string? command) => Executable(command, File.Exists);

    internal static string? Executable(string? command, Func<string, bool> exists) => Parse(command, exists)?.File;

    /// <summary>File and arguments of a command line (for running a registered uninstaller).</summary>
    public static (string File, string Args)? Split(string? command) => Parse(command, File.Exists);

    /// <summary>
    /// Quoted path, or the shortest unquoted prefix that matches an existing file
    /// ("C:\Program Files\App\app.exe -x" without quotes). A name without a folder is searched in System32 and in Windows.
    /// </summary>
    internal static (string File, string Args)? Parse(string? command, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[12..]);

        string? candidate = null;
        string args;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            candidate = end > 1 ? text[1..end] : text.Trim('"');
            args = end > 1 ? text[(end + 1)..].Trim() : "";
        }
        else
        {
            var parts = text.Split(' ');
            var used = 1;
            for (var i = 1; i <= parts.Length && candidate is null; i++)
            {
                var prefix = string.Join(' ', parts.Take(i));
                if (exists(prefix)) candidate = prefix;
                else if (exists(prefix + ".exe")) candidate = prefix + ".exe";
                if (candidate is not null) used = i;
            }
            candidate ??= parts[0];
            args = string.Join(' ', parts.Skip(used)).Trim();
            // "rundll32.exe x.dll,Func": the comma isn't part of the file name.
            candidate = candidate.TrimEnd(',');
        }
        if (candidate.Length == 0) return null;
        if (!IsRooted(candidate))
        {
            foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var full = Path.Combine(dir, candidate);
                if (exists(full)) return (full, args);
                if (exists(full + ".exe")) return (full + ".exe", args);
            }
            return null;
        }
        return (candidate, args);
    }

    // Path.IsPathRooted depends on the OS; the rule here is Windows' (also in tests on Linux).
    static bool IsRooted(string p) => p.StartsWith(@"\\", StringComparison.Ordinal) || (p.Length >= 3 && p[1] == ':' && p[2] is '\\' or '/');

    /// <summary>File inside the Windows folder (system component).</summary>
    public static bool InWindowsFolder(string? path)
    {
        if (path is null) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        return windows.Length > 1 && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }
}
