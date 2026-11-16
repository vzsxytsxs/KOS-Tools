using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace KosToolbox.Core.Setup;

/// <summary>Checks the signature and opens the downloaded installer.</summary>
public static class Installer
{
    /// <summary>The file to open: the downloaded one, or the installer inside the .zip (extracted to a folder beside it).</summary>
    public static string Prepare(SetupApp app, string file)
    {
        if (app.ZipEntry is null) return file;
        using var zip = ZipFile.OpenRead(file);
        var entry = zip.Entries.FirstOrDefault(e => Regex.IsMatch(e.Name, app.ZipEntry, RegexOptions.IgnoreCase))
                    ?? throw new InvalidDataException($"{Path.GetFileName(file)} doesn't contain the installer anymore.");
        var dir = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, entry.Name); // only the entry name: nothing escapes the folder
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Deletes the downloaded file and, if the installer came from a .zip, the folder extracted beside it. Only deletes inside the page's
    /// downloads folder (never the folder itself).
    /// </summary>
    public static void Delete(string file, string? target)
    {
        if (target is not null && !target.Equals(file, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(target) is { } dir
            && IsInsideDownloads(dir) && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        if (IsInsideDownloads(file)) File.Delete(file);
    }

    /// <summary>
    /// Elevated, a file outside the administrators-only downloads folder (a portable program in the user's Downloads) could be swapped by a
    /// program that isn't administrator between the signature check and the start. It is copied to an administrators-only folder first;
    /// the caller checks the signature of the returned path and opens that one.
    /// </summary>
    public static string Guard(string path)
    {
        if (!Execution.Elevation.IsAdmin || IsInsideDownloads(path)) return path;
        var root = GuardFolder;
        Gpu.GpuTuner.PrepareDirectory(Path.GetDirectoryName(root)!);
        Gpu.GpuTuner.PrepareDirectory(root);
        // One folder per call: a later call for the same name can't replace a copy that already passed its check. Old ones go after a day.
        foreach (var old in new DirectoryInfo(root).EnumerateDirectories().Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)))
            try { old.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var dir = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
        var copy = Path.Combine(dir, Path.GetFileName(path));
        File.Copy(path, copy);
        return copy;
    }

    static string GuardFolder => Path.Combine(Persistence.AppPaths.TheKosRoot, "Toolbox", "Portable");

    /// <summary>Deletes a copy <see cref="Guard"/> made that won't be opened (its signature was refused); anything else is left alone.</summary>
    public static void Discard(string? path)
    {
        if (path is null || Path.GetDirectoryName(path) is not { } dir || !string.Equals(Path.GetDirectoryName(dir), GuardFolder, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The signer, when it is the one the catalog requires for this app (<see cref="SetupApp.Publisher"/>); otherwise null, as if unsigned.</summary>
    public static string? Signer(SetupApp app, string path)
    {
        var signer = Signer(path);
        if (app.Publisher is null || signer == app.Publisher) return signer;
        Persistence.Log.Warn($"Setup: {path} is signed by {signer ?? "nobody"}, not {app.Publisher}; refused.");
        return null;
    }

    internal static bool IsInsideDownloads(string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Downloader.Folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the installer and waits for it to close (portable programs only open). Returns the exit code.</summary>
    public static async Task<int?> RunAsync(string path, string[] arguments, bool portable, CancellationToken ct)
    {
        var psi = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".msi" => new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { ArgumentList = { "/i", path } },
            // Script (SpotX): its own console window, so the user can follow it and answer its prompts.
            ".ps1" => new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                { ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path }, WorkingDirectory = Path.GetDirectoryName(path)! },
            _ => new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! },
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null || portable) return null;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>Who signed the file (valid Authenticode, trusted chain); null if it isn't signed or the signature doesn't verify.</summary>
    public static string? Signer(string path)
    {
        if (!Verify(path)) return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return "?"; }
    }

    static bool Verify(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = Marshal.StringToHGlobalUni(path) };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE: no revocation check over the network
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(file.pcwszFilePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath, hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    internal static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
}
