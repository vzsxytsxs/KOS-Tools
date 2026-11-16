using KosToolbox.Core.Apps;
using KosToolbox.Core.Debloat;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace KosToolbox.Core.KosState;

/// <summary>An installed (for any user) or provisioned package, as the check compares it.</summary>
internal sealed record KosPackage(string Name, string FullName, string FamilyName, string Version, bool System, bool ProvisionedOnly);

/// <summary>The apps, NTLite components, optional features, capabilities and files the KOS removes or sets: read for the check, put back elevated.</summary>
internal static class KosSystemItems
{
    /// <summary>Every user's packages and the provisioned ones as administrator; only the current user's otherwise.</summary>
    public static List<KosPackage> Packages()
    {
        var pm = new PackageManager();
        var list = new List<KosPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Package p, bool provisioned)
        {
            try
            {
                if (p.IsResourcePackage || !seen.Add(p.Id.FullName)) return;
                var v = p.Id.Version;
                list.Add(new KosPackage(p.Id.Name, p.Id.FullName, p.Id.FamilyName, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    p.SignatureKind == PackageSignatureKind.System, provisioned));
            }
            catch (Exception ex) { Log.Warn($"KOS state: a package couldn't be read: {ex.Message}"); }
        }
        foreach (var p in Elevation.IsAdmin ? pm.FindPackages() : pm.FindPackagesForUser("")) Add(p, false);
        if (!Elevation.IsAdmin) return list;
        try { foreach (var p in pm.FindProvisionedPackages()) Add(p, true); }
        catch (Exception ex) { Log.Warn($"KOS state: provisioned packages not read ({ex.Message})"); }
        return list;
    }

    public static List<KosPackage> Matching(IEnumerable<KosPackage> packages, string pattern) =>
        packages.Where(p => KosExpected.Like(pattern, p.Name)).ToList();

    /// <summary>
    /// Removes the matching packages for every user and deprovisions them, as Settings › Apps does for all users (system apps are
    /// marked end-of-life first, as servicing does). Throws with the first failure after trying them all.
    /// </summary>
    public static void RemovePackages(string pattern) => RemovePackages(p => KosExpected.Like(pattern, p.Name));

    static void RemovePackages(Func<KosPackage, bool> match)
    {
        string? error = null;
        foreach (var p in Packages().Where(p => match(p) && !p.ProvisionedOnly))
        {
            try
            {
                StoreApps.RemoveForAllUsersAsync(new StoreApp(p.FullName, p.FamilyName, p.Name, "", p.Version,
                    p.System ? StoreAppKind.System : StoreAppKind.App, null, null)).GetAwaiter().GetResult();
            }
            catch (Exception ex) { error ??= $"{p.Name}: {ex.Message}"; }
        }
        // Still provisioned (new accounts would get it): removed from the image like Remove-AppxProvisionedPackage.
        foreach (var p in Packages().Where(p => match(p) && p.ProvisionedOnly))
        {
            try { RemoveProvisioned(p); }
            catch (Exception ex) { error ??= $"{p.Name}: {ex.Message}"; }
        }
        if (error is not null) throw new InvalidOperationException(error);
    }

    static void RemoveProvisioned(KosPackage p)
    {
        // The full name comes from Windows' own list; checked anyway, since it becomes a DISM argument.
        if (!p.FullName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '~'))
            throw new InvalidOperationException($"Unexpected package name: {p.FullName}");
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var r = ProcessRunner.Run(dism, $"/online /english /remove-provisionedappxpackage /packagename:{p.FullName}", timeout: TimeSpan.FromMinutes(10));
        Log.Info($"dism remove-provisionedappxpackage {p.FullName}: exit {r.ExitCode}");
        if (r.ExitCode != 0) throw new InvalidOperationException(Text.Txt.DismFailed(r.ExitCode, r.Output));
        var marked = new PackageManager().DeprovisionPackageForAllUsersAsync(p.FamilyName).AsTask().GetAwaiter().GetResult();
        if (marked.ExtendedErrorCode is { } e) Log.Warn($"Deprovisioning {p.FamilyName}: {marked.ErrorText?.Trim() ?? e.Message}");
    }

    /// <summary>The list's paths are as on a C: install; Windows may be on another drive.</summary>
    public static string LocalPath(string path) =>
        path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, path[3..]) : path;

    public static bool Exists(KosExpectedFile f)
    {
        var path = LocalPath(f.Path);
        return f.Kind == "folder" ? Directory.Exists(path) : File.Exists(path);
    }

    static readonly string[] DefenderFiles = { "Wd", "wsc", "MsSec", "SecurityHealth", "SecurityAndMaintenance" };

    /// <summary>Defender's own files: protected and in use while it runs, so they can only go with Defender in Safe Mode.</summary>
    public static bool DefenderFile(string path) =>
        DefenderFiles.Any(p => Path.GetFileName(path).StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || path.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes a file or folder of the list, never through a link on its path. TrustedInstaller owns most of them, so it goes through
    /// PowerRun when the KOS has it, as the image build does.
    /// </summary>
    public static void Delete(string listedPath, bool folder)
    {
        var path = LocalPath(listedPath);
        if (folder ? !Directory.Exists(path) : !File.Exists(path)) return;
        var root = Path.GetPathRoot(path)!;
        for (var p = path; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
            if (DebloatFiles.IsLink(p)) throw new InvalidOperationException($"{path}: {p} is a link; nothing was deleted.");
        // A SystemApps folder is an app's (its name is the package family): the app goes first, or it stays registered without files.
        var name = Path.GetFileName(path);
        if (folder && "SystemApps".Equals(Path.GetFileName(Path.GetDirectoryName(path)), StringComparison.OrdinalIgnoreCase)
            && Packages().Any(p => p.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            RemovePackages(p => p.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (TrustedInstaller.Available) TrustedInstaller.DeletePath(path, folder);
        else DebloatFiles.Delete(new[] { path });
        Log.Info($"KOS state: deleted {path}");
    }
}
