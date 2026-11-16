using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Startup;
using Microsoft.Win32;

namespace KosToolbox.Core.Apps;

/// <summary>"Installed apps" program, read from the Uninstall keys (the same ones Control Panel uses).</summary>
public sealed record InstalledProgram(string Id, string Name, string? Publisher, string? Version, long? SizeKb, DateTime? InstallDate,
    string? InstallLocation, string? UninstallString, string? MsiProductCode, string Source, string? Icon)
{
    /// <summary>MSI: msiexec /x with the product code (the MSI UninstallString often opens "modify"); otherwise the registered command.</summary>
    public string? UninstallCommand => MsiProductCode is { } code
        ? $"\"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")}\" /x {code}"
        : UninstallString;

    /// <summary>The entry's own command for removing it without questions (QuietUninstallString), when the installer registered one.</summary>
    public string? QuietUninstallString { get; init; }
}

public static class InstalledPrograms
{
    const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    static readonly (RegistryHive Hive, RegistryView View, string Source)[] Roots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
    };

    public static List<InstalledProgram> List()
    {
        var list = new List<InstalledProgram>();
        foreach (var (hive, view, source) in Roots)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(Key);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(name);
                if (k is null || Read(k, name, $"{source}\\{name}", source) is not { } p) continue;
                list.Add(p);
            }
        }
        // The same program can show up in both the 64 and 32-bit views (reflected key); keep one entry.
        return list.GroupBy(p => (p.Name, p.Version, p.UninstallCommand)).Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Null for entries that Control Panel also hides (components, updates, no name or no uninstaller).</summary>
    internal static InstalledProgram? Read(RegistryKey k, string keyName, string id, string source)
    {
        var name = (k.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name) || k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is string) return null;
        if (k.GetValue("ReleaseType") is string type && type is "Update" or "Hotfix" or "Security Update" or "Update Rollup") return null;
        var uninstall = k.GetValue("UninstallString") as string;
        var msi = k.GetValue("WindowsInstaller") is 1 && Guid.TryParse(keyName, out _) ? keyName : null;
        if (string.IsNullOrWhiteSpace(uninstall) && msi is null) return null;
        DateTime? date = DateTime.TryParseExact(k.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        long? size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null;
        return new InstalledProgram(id, name, k.GetValue("Publisher") as string, k.GetValue("DisplayVersion") as string, size, date,
            k.GetValue("InstallLocation") as string, uninstall, msi, source, k.GetValue("DisplayIcon") as string)
            { QuietUninstallString = k.GetValue("QuietUninstallString") as string };
    }

    /// <summary>
    /// How to remove the program without its wizard: the registered quiet command, msiexec /qn for MSI, or the silent switch of an
    /// uninstaller whose type is recognizable (Inno Setup, NSIS). Null = only the interactive uninstaller. Only for machine-wide entries whose
    /// uninstaller is in a folder only administrators change: a user's own entries (HKCU) could name any command or product, and the
    /// toolbox runs it elevated with no window to show what it removes.
    /// </summary>
    public static (string File, string Args, string Kind)? Silent(InstalledProgram p)
    {
        if (!p.Source.StartsWith("HKLM", StringComparison.Ordinal)) return null;
        if (p.MsiProductCode is { } code) return (Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/x {code} /qn /norestart", "MSI");
        if (CommandLine.Split(p.QuietUninstallString) is { } quiet && File.Exists(quiet.File))
            return AdminFolder(quiet.File) ? (quiet.File, quiet.Args, "quiet command") : null;
        if (CommandLine.Split(p.UninstallString) is not { } u || !File.Exists(u.File) || !AdminFolder(u.File)) return null;
        var kind = UninstallerKind(u.File, () => Head(u.File));
        return kind switch
        {
            "Inno Setup" => (u.File, Join(u.Args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"), kind),
            "NSIS" => (u.File, Join(u.Args, "/S"), kind),
            _ => null,
        };
    }

    /// <summary>Program Files, Windows or the installers' package cache: folders a standard user can't add files to.</summary>
    internal static bool AdminFolder(string file)
    {
        var full = Path.GetFullPath(file);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
        }.Where(f => f.Length > 0).Any(f => full.StartsWith(Path.TrimEndingDirectorySeparator(f) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Inno Setup by its uninstaller's name (unins000.exe), NSIS by the marker its installer data carries.</summary>
    internal static string? UninstallerKind(string file, Func<byte[]> head)
    {
        if (Regex.IsMatch(Path.GetFileName(file), @"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)) return "Inno Setup";
        var data = head();
        return data.AsSpan().IndexOf("NullsoftInst"u8) >= 0 || data.AsSpan().IndexOf("Nullsoft.NSIS"u8) >= 0 ? "NSIS" : null;
    }

    static byte[] Head(string file)
    {
        using var f = File.OpenRead(file);
        var buffer = new byte[(int)Math.Min(f.Length, 16 * 1024 * 1024)];
        f.ReadExactly(buffer);
        return buffer;
    }

    static string Join(string args, string extra) => args.Contains(extra, StringComparison.OrdinalIgnoreCase) ? args : (args + " " + extra).Trim();

    /// <summary>
    /// Runs the program's own uninstaller (interactive) and waits for it to finish. Some copy themselves to another folder
    /// and exit immediately; so what confirms the result is re-reading the list, not the exit code.
    /// </summary>
    public static async Task<int?> UninstallAsync(InstalledProgram p, CancellationToken ct = default, bool silent = false)
    {
        var quiet = silent ? Silent(p) : null;
        if ((quiet is { } q ? (q.File, q.Args) : CommandLine.Split(p.UninstallCommand)) is not { } cmd || !File.Exists(cmd.File))
            throw new InvalidOperationException(Text.Txt.UninstallerMissing(p.UninstallCommand));
        Log.Info($"Uninstall {p.Name}{(quiet is { } k ? $" ({k.Kind}, silent)" : "")}: {cmd.File} {cmd.Args}");
        using var proc = Process.Start(new ProcessStartInfo(cmd.File, cmd.Args) { UseShellExecute = true });
        if (proc is null) return null;
        var started = proc.StartTime;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var code = proc.ExitCode;
        // NSIS and similar keep running from a copy in %TEMP%: wait for the copy, or for the entry to disappear from the list.
        await Execution.ProcessTree.WaitAsync(proc.Id, started, () => !StillInstalled(p), ct).ConfigureAwait(false);
        return code;
    }

    public static bool StillInstalled(InstalledProgram p) => List().Any(x => x.Id == p.Id);

    /// <summary>Program's Uninstall key ("HKLM\SOFTWARE\WOW6432Node\...\Uninstall\X" for the 32-bit view).</summary>
    public static string? KeyPath(InstalledProgram p)
    {
        var name = p.Id.StartsWith(p.Source + "\\", StringComparison.Ordinal) ? p.Id[(p.Source.Length + 1)..] : null;
        if (string.IsNullOrEmpty(name) || name.Contains('\\')) return null;
        return p.Source switch
        {
            "HKLM 64-bit" => $@"HKLM\{Key}\{name}",
            "HKLM 32-bit" => $@"HKLM\SOFTWARE\WOW6432Node\{Key["SOFTWARE\\".Length..]}\{name}",
            "HKCU" => $@"HKCU\Software\{Key["SOFTWARE\\".Length..]}\{name}",
            _ => null,
        };
    }
}
