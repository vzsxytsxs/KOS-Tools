using System.Security.AccessControl;
using System.Security.Principal;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Debloat;

/// <summary>Spotify's desktop install: the app in %AppData%, the updater's folder in %LocalAppData%.</summary>
public sealed record SpotifyInstall(string App, string Local);

public static class SpotifyDebloat
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string[] HelperFiles = { "SpotifyMigrator.exe", "SpotifyStartupTask.exe" };
    static readonly string[] RuntimePatterns = { "chrome_1*.*", "chrome_2*.*", "crash*.*", "d3d*.*", "debug.log", "libegl.dll", "libgle*.*", "snapshot*.*", "vk*.*", "vulkan*.*" };
    const FileSystemRights DeniedRights = FileSystemRights.Delete | FileSystemRights.Read | FileSystemRights.Write;

    public static SpotifyInstall Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify"));

    public static DebloatApp App(SpotifyInstall s) => new()
    {
        Id = "spotify", Name = "Spotify", Icon = "spotify", Processes = new[] { "Spotify" },
        Executable = () => Path.Combine(s.App, "Spotify.exe") is var exe && File.Exists(exe) && !DebloatFiles.IsLink(s.App) && DebloatFiles.IsRealOrMissing(s.Local) ? exe : null,
        Options = Options(s),
    };

    internal static IReadOnlyList<DebloatOption> Options(SpotifyInstall s) => new[]
    {
        new DebloatOption
        {
            Id = "updates", Name = Txt.SpotifyUpdates, Description = Txt.SpotifyUpdatesInfo, ClosesApp = true, Warning = Txt.SpotifyUpdatesWarning,
            Read = () => IsUpdateBlocked(s) ? DebloatState.Applied : DebloatState.NotApplied,
            Apply = _ => Sync(() => BlockUpdates(s)),
            Undo = _ => Sync(() => UnblockUpdates(s)),
        },
        new DebloatOption
        {
            Id = "autostart", Name = Txt.DebloatAutostart, Description = Txt.SpotifyAutostartInfo,
            Read = () => HasRunValue() ? DebloatState.NotApplied : DebloatState.Applied,
            Apply = _ => Sync(() => { using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); run?.DeleteValue("Spotify", throwOnMissingValue: false); }),
        },
        new DebloatOption
        {
            Id = "helpers", Name = Txt.SpotifyHelpers, Description = Txt.SpotifyHelpersInfo, ClosesApp = true,
            Read = () => DebloatFiles.StateOfRemoval(Helpers(s).Count()),
            Apply = _ => Sync(() => DebloatFiles.Delete(Helpers(s))),
        },
        new DebloatOption
        {
            Id = "locales", Name = Txt.DebloatLocales, Description = Txt.DebloatLocalesInfo, ClosesApp = true,
            Read = () => DebloatFiles.StateOfRemoval(ExtraLocales(s).Count()),
            Apply = _ => Sync(() => DebloatFiles.Delete(ExtraLocales(s))),
        },
        new DebloatOption
        {
            Id = "runtime", Name = Txt.SpotifyRuntime, Description = Txt.SpotifyRuntimeInfo, ClosesApp = true,
            Read = () => DebloatFiles.StateOfRemoval(Runtime(s).Count()),
            Apply = _ => Sync(() => DebloatFiles.Delete(Runtime(s))),
        },
    };

    static Task Sync(Action action) { action(); return Task.CompletedTask; }

    static bool HasRunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue("Spotify") is not null;
    }

    internal static IEnumerable<string> Helpers(SpotifyInstall s) => HelperFiles.SelectMany(n => DebloatFiles.Files(s.App, n));

    internal static IEnumerable<string> ExtraLocales(SpotifyInstall s) =>
        DebloatFiles.Files(Path.Combine(s.App, "locales"), "*.pak").Where(f => !Path.GetFileName(f).Equals("en-US.pak", StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<string> Runtime(SpotifyInstall s) => RuntimePatterns.SelectMany(p => DebloatFiles.Files(s.App, p));

    // ---------- updater folder ----------

    static string UpdateFolder(SpotifyInstall s) => Path.Combine(s.Local, "Update");
    static SecurityIdentifier User => WindowsIdentity.GetCurrent().User!;

    /// <summary>The folder where Spotify downloads updates exists and denies the user: it can't update.</summary>
    internal static bool IsUpdateBlocked(SpotifyInstall s)
    {
        var folder = new DirectoryInfo(UpdateFolder(s));
        if (!DebloatFiles.IsFolder(folder.FullName)) return false;
        try
        {
            return folder.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(User) && r.FileSystemRights.HasFlag(FileSystemRights.Write));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static void BlockUpdates(SpotifyInstall s)
    {
        var path = UpdateFolder(s);
        foreach (var checkedPath in new[] { s.Local, path })
            if (!DebloatFiles.IsRealOrMissing(checkedPath)) throw new InvalidOperationException(Txt.DebloatIsLink(checkedPath));
        if (Directory.Exists(path)) DebloatFiles.Delete(new[] { path });
        var folder = Directory.CreateDirectory(path);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(User, DeniedRights, AccessControlType.Deny));
        folder.SetAccessControl(acl);
    }

    internal static void UnblockUpdates(SpotifyInstall s)
    {
        var folder = new DirectoryInfo(UpdateFolder(s));
        if (!DebloatFiles.IsFolder(folder.FullName)) return;
        var acl = folder.GetAccessControl();
        acl.RemoveAccessRuleAll(new FileSystemAccessRule(User, DeniedRights, AccessControlType.Deny));
        folder.SetAccessControl(acl);
    }
}
