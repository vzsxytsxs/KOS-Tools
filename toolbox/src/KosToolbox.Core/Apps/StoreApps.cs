using KosToolbox.Core.Persistence;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace KosToolbox.Core.Apps;

public enum StoreAppKind { App, System, Framework }

/// <summary>MSIX/Appx package installed for the current user (Microsoft Store and Windows apps).</summary>
public sealed record StoreApp(string FullName, string FamilyName, string Name, string Publisher, string Version, StoreAppKind Kind,
    DateTimeOffset? Installed, string? Location);

/// <summary>Packages of the current user through the Windows PackageManager (the same API as Get-AppxPackage), without PowerShell.</summary>
public static class StoreApps
{
    public static List<StoreApp> List()
    {
        var list = new List<StoreApp>();
        foreach (var p in new PackageManager().FindPackagesForUser(""))
        {
            try
            {
                if (p.IsResourcePackage) continue; // language/scale packages go with the main app
                var kind = p.IsFramework ? StoreAppKind.Framework : p.SignatureKind == PackageSignatureKind.System ? StoreAppKind.System : StoreAppKind.App;
                var v = p.Id.Version;
                list.Add(new StoreApp(p.Id.FullName, p.Id.FamilyName, Safe(() => p.DisplayName) ?? p.Id.Name, Safe(() => p.PublisherDisplayName) ?? "",
                    $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", kind, Safe(() => (DateTimeOffset?)p.InstalledDate), Safe(() => p.InstalledLocation?.Path)));
            }
            catch (Exception ex) { Log.Warn($"Package {p.Id.FullName} couldn't be read: {ex.Message}"); }
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Some broken or updating packages throw when reading the name or folder; the rest of the list continues.
    static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }

    /// <summary>Removes the package only for the current user (does not require administrator). Throws with the Windows text on failure.</summary>
    public static async Task RemoveAsync(string fullName)
    {
        var result = await new PackageManager().RemovePackageAsync(fullName);
        if (result.ExtendedErrorCode is { } error)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorText) ? error.Message : result.ErrorText.Trim());
        Log.Info($"Package removed for the current user: {fullName}");
    }

    public static bool Exists(string fullName) => new PackageManager().FindPackageForUser("", fullName) is not null;

    /// <summary>Installed for any user (administrator).</summary>
    public static bool ExistsForAnyUser(string fullName) => new PackageManager().FindPackage(fullName) is not null;

    const string AllUserStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

    /// <summary>
    /// Removes the package for every user and deprovisions it, so new accounts don't get it either (administrator). A package
    /// Windows doesn't let users remove (system apps) is first marked end-of-life for each user that has it, as servicing does
    /// for retired apps, and as deprovisioned, so a feature update doesn't bring it back.
    /// </summary>
    public static async Task RemoveForAllUsersAsync(StoreApp app)
    {
        if (!Execution.Elevation.IsAdmin) throw new UnauthorizedAccessException(Text.Txt.AllUsersNeedsAdmin);
        var pm = new PackageManager();
        var marks = new List<string>(); // keys created here, removed again if the app stays installed
        void Mark(string path)
        {
            using (var existing = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path)) if (existing is not null) return;
            Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path)?.Dispose();
            marks.Add(path);
        }
        try
        {
            if (app.Kind == StoreAppKind.System)
            {
                foreach (var user in pm.FindUsers(app.FullName)) Mark($@"{AllUserStore}\EndOfLife\{user.UserSecurityId}\{app.FullName}");
                Mark($@"{AllUserStore}\Deprovisioned\{app.FamilyName}");
                Log.Info($"Package marked end-of-life for its users: {app.FullName}");
            }
            var removed = await pm.RemovePackageAsync(app.FullName, RemovalOptions.RemoveForAllUsers);
            if (removed.ExtendedErrorCode is { } error)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(removed.ErrorText) ? error.Message : removed.ErrorText.Trim());
        }
        catch
        {
            foreach (var path in marks) Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(path, throwOnMissingSubKey: false);
            throw;
        }
        var deprovisioned = await pm.DeprovisionPackageForAllUsersAsync(app.FamilyName);
        // Not provisioned is the usual case for apps installed from the Store; only a real error is logged.
        if (deprovisioned.ExtendedErrorCode is { } dError) Log.Warn($"Deprovisioning {app.FamilyName}: {deprovisioned.ErrorText?.Trim() ?? dError.Message}");
        Log.Info($"Package removed for all users: {app.FullName}");
    }
}
