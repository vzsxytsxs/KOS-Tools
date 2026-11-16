using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using KosToolbox.Core.Persistence;

namespace KosToolbox.Core.Execution;

/// <summary>
/// A reviewed, allowed KOS script. Only runs if the file exists at the catalog path
/// and has exactly the SHA-256 of the analyzed version; arguments and input come from the catalog, never from the UI.
/// </summary>
public sealed record ScriptRef(string RelativePath, string Sha256)
{
    /// <summary>Fixed arguments (e.g. "1" for scripts that take the option as a parameter).</summary>
    public string Args { get; init; } = "";
    /// <summary>Text sent to standard input for scripts with a "set /p" menu.</summary>
    public string? Stdin { get; init; }
    public bool RequiresAdmin { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Root of the scripts on the installed system: %ProgramData%\TheKOS. Overridable in tests.</summary>
    public static string Root { get => AppPaths.TheKosRoot; set => AppPaths.TheKosRoot = value; }

    public string FullPath => Path.Combine(Root, RelativePath);

    /// <summary>Null if the script can run; otherwise, the reason.</summary>
    public string? Problem()
    {
        if (!File.Exists(FullPath)) return $"Script not found: {FullPath}";
        string hash;
        try { using var f = File.OpenRead(FullPath); hash = Convert.ToHexString(SHA256.HashData(f)); }
        catch (Exception ex) { return $"Couldn't read {FullPath}: {ex.Message}"; }
        if (!string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase))
            return $"The script {RelativePath} changed since the version reviewed for the toolbox (SHA-256 {hash[..12]}…). It won't run until the catalog is updated.";
        return RequiresAdmin ? OwnerProblem() : null;
    }

    /// <summary>
    /// A script run as administrator must belong to the system: if it belonged to a regular user,
    /// they could swap it between the hash check and the run.
    /// </summary>
    string? OwnerProblem()
    {
        try
        {
            var owner = new FileInfo(FullPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner.Value == TrustedInstallerSid)) return null;
            return $"The script {RelativePath} is owned by {owner?.Value ?? "an unknown account"}, not by SYSTEM or Administrators. For safety it won't run as administrator.";
        }
        catch (Exception ex) { return $"Couldn't check the owner of {FullPath}: {ex.Message}"; }
    }

    const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public async Task<ProcessResult> RunAsync(CancellationToken ct = default)
    {
        var problem = Problem();
        if (problem is not null) throw new InvalidOperationException(problem);

        var ext = Path.GetExtension(FullPath).ToLowerInvariant();
        string exe, arguments;
        if (ext is ".cmd" or ".bat")
        {
            exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // /s: cmd strips only the outer quotes, preserving the path with spaces inside quotes.
            arguments = $"/d /s /c \"\"{FullPath}\" {Args}\"";
        }
        else if (ext == ".ps1")
        {
            exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {ProcessRunner.Quote(FullPath)} {Args}";
        }
        else throw new InvalidOperationException($"Unsupported script type: {ext}");

        Log.Info($"Script {RelativePath} {Args}".TrimEnd() + ": started");
        var result = await ProcessRunner.RunAsync(exe, arguments, Path.GetDirectoryName(FullPath), Stdin, Timeout, ct);
        Log.Info($"Script {RelativePath}: exit code {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}\n{result.Output.Trim()}");
        if (result.TimedOut) throw new TimeoutException($"The script {RelativePath} ran longer than {Timeout.TotalMinutes:0} min and was stopped.");
        return result;
    }
}
