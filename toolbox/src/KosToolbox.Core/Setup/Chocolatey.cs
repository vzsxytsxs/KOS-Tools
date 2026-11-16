using System.Diagnostics;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Setup;

/// <summary>
/// Chocolatey, for the Setup programs that have a package (<see cref="SetupApp.Choco"/>). It is installed on first use from
/// its own signed MSI (the latest chocolatey/choco release on GitHub); packages install with "choco upgrade id -y", which also
/// installs what is missing, as administrator.
/// </summary>
public static partial class Chocolatey
{
    static readonly int[] Success = { 0, 1641, 3010 };
    /// <summary>Common name of the certificate Chocolatey signs choco.exe with (checked on an installed copy).</summary>
    const string Signer = "Chocolatey Software, Inc";

    /// <summary>
    /// choco.exe in the folder the machine's ChocolateyInstall variable names (read fresh, so a new install is seen at once). Only the
    /// variable the MSI sets: a choco.exe a user drops in ProgramData before Chocolatey is installed doesn't count.
    /// </summary>
    public static string? Exe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ChocolateyInstall", EnvironmentVariableTarget.Machine) is not { Length: > 0 } root) return null;
            var exe = Path.Combine(root, "choco.exe"); // the program itself; bin\choco.exe is a shim that starts it
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>Null when the folder isn't a link and choco.exe belongs to SYSTEM, Administrators or TrustedInstaller.</summary>
    static string? Problem(string exe) =>
        Gpu.GpuTuner.FolderProblem(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, Path.GetDirectoryName(exe)!) ?? Tools.ToolDiscovery.OwnerProblem(exe);

    public static bool Succeeded(int code) => Success.Contains(code);

    /// <summary>
    /// choco finds its folder (config, extensions) through ChocolateyInstall, which the user's own environment can override: run
    /// elevated, it gets the machine's folder, the one whose owner was checked.
    /// </summary>
    static Dictionary<string, string> MachineRoot(string exe) => new() { ["ChocolateyInstall"] = Path.GetDirectoryName(exe)! };

    /// <summary>Installs Chocolatey if it isn't there: downloads the MSI, checks Chocolatey signed it, runs it (UAC when needed).</summary>
    public static async Task<string> EnsureAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (Exe is { } exe) return exe;
        var url = await SetupCatalog.GitHubAsset(Downloader.Http, "chocolatey/choco", @"^chocolatey-[\d.]+\.msi$", ct).ConfigureAwait(false);
        var (file, _) = await Downloader.DownloadAsync(url, "chocolatey.msi", Downloader.Folder, progress, ct).ConfigureAwait(false);
        var signer = Installer.Signer(file);
        if (signer is not Signer) throw new InvalidOperationException(Txt.ChocoUnsigned(signer));
        Log.Info($"Setup: installing Chocolatey from {url} (signed by {signer})");
        var code = await Installer.RunAsync(file, new[] { "/passive", "/norestart" }, portable: false, ct).ConfigureAwait(false);
        return Exe ?? throw new InvalidOperationException(Txt.ChocoNotInstalled(code));
    }

    /// <summary>
    /// Installs or updates a package. Hidden, with the output in the log, when the toolbox is administrator; otherwise in
    /// Chocolatey's own console, after the UAC prompt. Returns choco's exit code.
    /// </summary>
    public static async Task<int> InstallAsync(string exe, string package, CancellationToken ct)
    {
        if (!PackageId().IsMatch(package)) throw new ArgumentException($"Invalid Chocolatey package id: {package}");
        // It runs as administrator: a folder or file a standard user could have replaced is refused.
        if (Problem(exe) is { } problem) throw new InvalidOperationException(problem);
        var args = $"upgrade {package} -y --no-progress";
        if (Elevation.IsAdmin)
        {
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromMinutes(30), ct: ct, environment: MachineRoot(exe)).ConfigureAwait(false);
            Log.Info($"choco {args}: exit code {r.ExitCode}\n{r.Output.Trim()}");
            return r.ExitCode;
        }
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" })
                      ?? throw new InvalidOperationException(Txt.ChocoNotStarted);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        Log.Info($"choco {args}: exit code {p.ExitCode}");
        return p.ExitCode;
    }

    /// <summary>The packages Chocolatey installed that have a newer version ("choco outdated", read only; no administrator needed).</summary>
    public static async Task<IReadOnlyList<(string Id, string Current, string Available, bool Pinned)>> OutdatedAsync(string exe, CancellationToken ct)
    {
        if (Elevation.IsAdmin && Problem(exe) is { } problem) throw new InvalidOperationException(problem);
        var r = await ProcessRunner.RunAsync(exe, "outdated -r --no-progress", timeout: TimeSpan.FromMinutes(5), ct: ct, environment: MachineRoot(exe)).ConfigureAwait(false);
        var list = ParseOutdated(r.Output);
        if (list.Count == 0 && r.ExitCode is not (0 or 2)) throw new InvalidOperationException($"choco outdated failed (code {r.ExitCode}): {r.Output.Trim().Split('\n')[^1]}");
        return list;
    }

    /// <summary>"choco outdated -r" lines: id|current|available|pinned; the warnings around them are skipped.</summary>
    internal static List<(string Id, string Current, string Available, bool Pinned)> ParseOutdated(string output) =>
        output.Split('\n').Select(l => l.Trim().Split('|'))
            .Where(f => f.Length == 4 && PackageId().IsMatch(f[0]) && bool.TryParse(f[3], out _))
            .Select(f => (f[0], f[1], f[2], bool.Parse(f[3]))).ToList();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,99}$")]
    internal static partial Regex PackageId();
}
