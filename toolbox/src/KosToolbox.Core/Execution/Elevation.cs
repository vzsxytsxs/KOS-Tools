using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace KosToolbox.Core.Execution;

public sealed class ElevationCancelledException : Exception
{
    public ElevationCancelledException() : base("The UAC prompt was cancelled. Nothing was changed.") { }
}

public static class Elevation
{
    public static bool IsAdmin { get; } = CheckAdmin();

    /// <summary>
    /// The toolbox reopens itself as administrator; in a folder regular users can change (Downloads,
    /// Documents, ProgramData), a file placed next to the .exe could be loaded with those privileges.
    /// </summary>
    public static bool InProtectedFolder =>
        Environment.ProcessPath is { } exe
        && exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How this process's token relates to UAC: 1 no split (UAC off since the last restart, or a standard user), 2 elevated from a split
    /// token, 3 the unelevated half; null if it can't be read. Unlike EnableLUA, it reflects this session: turning UAC off only counts after a restart.
    /// </summary>
    public static int? TokenElevationType()
    {
        using var id = WindowsIdentity.GetCurrent();
        return GetTokenInformation(id.Token, TokenElevationTypeClass, out int type, sizeof(int), out _) ? type : null;
    }

    const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returnLength);

    static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts this same executable elevated (UAC) and waits for it to finish. Throws
    /// <see cref="ElevationCancelledException"/> if the user declines.
    /// </summary>
    public static async Task<int> RunSelfElevatedAsync(IEnumerable<string> args, CancellationToken ct = default, TimeSpan? limit = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The toolbox executable path is unknown.");
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(ProcessRunner.Quote)))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new ElevationCancelledException(); }
        if (p is null) throw new InvalidOperationException("The elevated process didn't start.");
        using (p)
        {
            // Limit above the longest script time in the catalog, so the queue never gets stuck.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wait = limit ?? TimeSpan.FromMinutes(30);
            cts.CancelAfter(wait);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The elevated process didn't finish within {wait.TotalMinutes:0} minutes. Check the log.");
            }
            return p.ExitCode;
        }
    }
}
