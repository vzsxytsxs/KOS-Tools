using KosToolbox.Core.Execution;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Debloat;

/// <summary>Value written under a browser's policy key. SubKey: relative to that key; with "@" at the start, a full HKLM path.</summary>
internal sealed record PolicyValue(string Group, string SubKey, string Name, object Data);

internal static partial class BrowserPolicies
{
    internal static string KeyOf(string baseKey, PolicyValue v) =>
        v.SubKey.Length == 0 ? baseKey : v.SubKey[0] == '@' ? v.SubKey[1..] : $@"{baseKey}\{v.SubKey}";

    internal static DebloatState Read(RegistryKey hive, string baseKey, IReadOnlyCollection<PolicyValue> values)
    {
        var matched = values.Count(v =>
        {
            using var key = hive.OpenSubKey(KeyOf(baseKey, v));
            return key?.GetValue(v.Name) is { } current && (v.Data is string s ? current is string c && c == s : current is int i && i == Convert.ToInt32(v.Data));
        });
        return matched == 0 ? DebloatState.NotApplied : matched == values.Count ? DebloatState.Applied : DebloatState.Partial;
    }

    internal static void Write(RegistryKey hive, string baseKey, IEnumerable<PolicyValue> values)
    {
        foreach (var v in values)
        {
            using var key = hive.CreateSubKey(KeyOf(baseKey, v));
            if (v.Data is string s) key.SetValue(v.Name, s, RegistryValueKind.String);
            else key.SetValue(v.Name, Convert.ToInt32(v.Data), RegistryValueKind.DWord);
        }
    }

    /// <summary>shortcut: Undo deletes the values instead of restoring what was there; a machine with its own browser policies would lose those, keep a backup if that ever matters.</summary>
    internal static void Remove(RegistryKey hive, string baseKey, IEnumerable<PolicyValue> values)
    {
        foreach (var v in values)
        {
            using var key = hive.OpenSubKey(KeyOf(baseKey, v), writable: true);
            key?.DeleteValue(v.Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>The browser's own updater: services, scheduled tasks, folders and leftover registry keys.</summary>
internal sealed record BrowserUpdater(string[] Services, string TaskPrefix, string[] Folders, string[] Keys);

public static class BrowserDebloat
{
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\", TaskTree = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
    static readonly (string Id, string Name, string Info, string? Warning)[] Groups =
    {
        ("ai", Txt.PolicyAi, Txt.PolicyAiInfo, null),
        ("telemetry", Txt.PolicyTelemetry, Txt.PolicyTelemetryInfo, null),
        ("ads", Txt.PolicyAds, Txt.PolicyAdsInfo, null),
        ("performance", Txt.PolicyPerformance, Txt.PolicyPerformanceInfo, null),
        ("startup", Txt.PolicyStartup, Txt.PolicyStartupInfo, null),
        ("permissions", Txt.PolicyPermissions, Txt.PolicyPermissionsInfo, null),
        ("extensions", Txt.PolicyExtensions, Txt.PolicyExtensionsInfo, null),
        ("update", Txt.PolicyUpdate, Txt.PolicyUpdateInfo, Txt.BrowserNoUpdatesWarning),
        ("security", Txt.PolicySecurity, Txt.PolicySecurityInfo, Txt.PolicySecurityWarning),
        ("other", Txt.PolicyOther, Txt.PolicyOtherInfo, null),
    };

    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
   static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static Func<string?> Exe(string path) => () => File.Exists(path) && DebloatFiles.IsRealOrMissing(Path.GetDirectoryName(path)!) ? path : null;
    static Task Sync(Action action) { action(); return Task.CompletedTask; }

    public static DebloatApp Chrome()
    {
        var app = Path.Combine(ProgramFiles, @"Google\Chrome\Application");
        var updater = new BrowserUpdater(new[] { "gupdate", "gupdatem", "googlechromeelevationservice" }, "GoogleUpdateTask",
            new[] { Path.Combine(ProgramFilesX86, @"Google\Update"), Path.Combine(ProgramFilesX86, @"Google\GoogleUpdater") }, new[] { @"SOFTWARE\Microsoft\Active Setup\Installed Components\{8A69D345-D564-463c-AFF1-A69D9E530F96}" });
        string[] files = { "chrome_wer.dll", "notification_helper.exe", "chrome_pwa_launcher.exe", "d3d*.*", "vk*.*", "vulkan*.*", "elevation_service.exe" };
        return new DebloatApp
        {
            Id = "chrome", Name = "Google Chrome", Icon = "chrome", Processes = new[] { "chrome" }, Executable = Exe(Path.Combine(app, "chrome.exe")),
            Options = new[] { RemoveUpdater(updater), Files(() => VersionItems(app, files, new[] { "Installer" }), admin: true) }
                .Concat(Policies(@"SOFTWARE\Policies\Google\Chrome", BrowserPolicies.Chrome)).ToList(),
        };
    }

    public static DebloatApp Brave()
    {
        var root = Path.Combine(ProgramFiles, @"BraveSoftware\Brave-Browser\Application");
        var updater = new BrowserUpdater(new[] { "brave", "bravem", "BraveElevationService" }, "BraveSoftwareUpdateTask",
            new[] { Path.Combine(ProgramFilesX86, "BraveSoftware"), Path.Combine(root, "SetupMetrics") },
            new[] { @"SOFTWARE\Microsoft\Active Setup\Installed Components\{AFE6A462-C574-4B8A-AF43-4CC60DF4563B}" });
        string[] files = { "notification_helper.exe", "eventlog_provider.dll", "chrome_pwa_launcher.exe", "brave_vpn_helper.exe", "elevation_service.exe", "vulkan-1.dll", "libEGL.dll", "libGLESv2.dll", "d3dcompiler_47.dll", "dxcompiler.dll", "dxil.dll" };
        string[] folders = { "Installer", "BraveVpnWireguardService", "Extensions", "IwaKeyDistribution", "PrivacySandboxAttestationsPreloaded", "MEIPreload" };
        return new DebloatApp
        {
            Id = "brave", Name = "Brave", Icon = "brave", Processes = new[] { "brave" }, Executable = Exe(Path.Combine(root, "brave.exe")),
            Options = new[] { RemoveUpdater(updater), UserUpdateFolder(), Files(() => VersionItems(root, files, folders).Concat(DebloatFiles.Files(root, "chrmstp.exe")), admin: true) }
                .Concat(Policies(@"SOFTWARE\Policies\BraveSoftware\Brave", BrowserPolicies.Brave)).ToList(),
        };
    }

    static readonly string[] MozillaFiles = { "crashreporter.exe", "crashreporter.ini", "maintenanceservice.exe", "maintenanceservice_installer.exe", "minidump-analyzer.exe", "pingsender.exe", "updater.exe", "crash*.*", "maintenance*.*", "install.log", "minidump*.*" };

    public static DebloatApp Firefox()
    {
        var root = Path.Combine(ProgramFiles, "Mozilla Firefox");
        return new DebloatApp
        {
            Id = "firefox", Name = "Mozilla Firefox", Icon = "firefox", Processes = new[] { "firefox" }, Executable = Exe(Path.Combine(root, "firefox.exe")),
            Options = new[] { Files(() => MozillaFiles.SelectMany(p => DebloatFiles.Files(root, p)), admin: true, Txt.BrowserNoUpdatesWarning) }
                .Concat(Policies(@"SOFTWARE\Policies\Mozilla\Firefox", BrowserPolicies.Firefox)).ToList(),
        };
    }

    public static DebloatApp LibreWolf()
    {
        var root = Path.Combine(ProgramFiles, "LibreWolf");
        return new DebloatApp
        {
            Id = "librewolf", Name = "LibreWolf", Icon = "librewolf", Processes = new[] { "librewolf" }, Executable = Exe(Path.Combine(root, "librewolf.exe")),
            Options = new[] { Files(() => MozillaFiles.SelectMany(p => DebloatFiles.Files(root, p)), admin: true, Txt.BrowserNoUpdatesWarning) },
        };
    }

    public static DebloatApp Thorium()
    {
        var app = Path.Combine(Local, @"Thorium\Application");
        string[] files = { "chrome_wer.dll", "notification_helper.exe", "chrome_pwa_launcher.exe", "d3d*.*", "vk*.*", "vulkan*.*" };
        return new DebloatApp
        {
            Id = "thorium", Name = "Thorium", Icon = "thorium", Processes = new[] { "thorium" }, Executable = Exe(Path.Combine(app, "thorium.exe")),
            Options = new[] { Files(() => VersionItems(app, files, Array.Empty<string>()), admin: false) },
        };
    }

    /// <summary>Runs as the user, not in the elevated job: it is a folder the user's programs can write. %ProgramData%\BraveSoftware is left alone for the same reason (any user can create it there).</summary>
    static DebloatOption UserUpdateFolder()
    {
        var parent = Path.Combine(Local, "BraveSoftware");
        var folder = Path.Combine(parent, "Update");
        return new DebloatOption
        {
            Id = "userupdate", Name = Txt.BrowserUserUpdate, Description = Txt.BrowserUserUpdateInfo,
            Read = () => DebloatFiles.StateOfRemoval(DebloatFiles.IsFolder(parent) && DebloatFiles.IsFolder(folder) ? 1 : 0),
            Apply = _ => Sync(() => { if (DebloatFiles.IsFolder(parent) && DebloatFiles.IsFolder(folder)) DebloatFiles.Delete(new[] { folder }); }),
        };
    }

    /// <summary>Files and folders inside the browser''s version folders (Application\138.0...).</summary>
    internal static IEnumerable<string> VersionItems(string application, string[] files, string[] folders) =>
        from version in DebloatFiles.Folders(application, "1*")
        from item in files.SelectMany(f => DebloatFiles.Files(version, f)).Concat(folders.SelectMany(f => DebloatFiles.Folders(version, f)))
        select item;

    static DebloatOption Files(Func<IEnumerable<string>> items, bool admin, string? warning = null) => new()
    {
        Id = "files", Name = Txt.BrowserFiles, Description = Txt.BrowserFilesInfo, NeedsAdmin = admin, ClosesApp = true, Warning = warning,
        Read = () => DebloatFiles.StateOfRemoval(items().Count()),
        Apply = _ => Sync(() => DebloatFiles.Delete(items())),
    };

    static IEnumerable<DebloatOption> Policies(string baseKey, PolicyValue[] all) =>
        from g in Groups
        let values = all.Where(v => v.Group == g.Id).ToList()
        where values.Count > 0
        select new DebloatOption
        {
            Id = "policy-" + g.Id, Name = g.Name, Description = Txt.PolicyDescription(g.Info, values.Count), NeedsAdmin = true, Warning = g.Warning,
            Read = () => BrowserPolicies.Read(Registry.LocalMachine, baseKey, values),
            Apply = _ => Sync(() => BrowserPolicies.Write(Registry.LocalMachine, baseKey, values)),
            Undo = _ => Sync(() => BrowserPolicies.Remove(Registry.LocalMachine, baseKey, values)),
        };

    // ---------- updater ----------

    static DebloatOption RemoveUpdater(BrowserUpdater u) => new()
    {
        Id = "updater", Name = Txt.BrowserUpdater, Description = Txt.BrowserUpdaterInfo, NeedsAdmin = true, ClosesApp = true, Warning = Txt.BrowserNoUpdatesWarning,
        Read = () => DebloatFiles.StateOfRemoval(Services(u).Count() + Tasks(u).Count() + u.Folders.Count(DebloatFiles.IsFolder)),
        Apply = ct => RemoveUpdaterAsync(u, ct),
    };

    static IEnumerable<string> Services(BrowserUpdater u) => u.Services.Where(s =>
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + s);
        return key is not null;
    });

    static IEnumerable<string> Tasks(BrowserUpdater u)
    {
        using var tree = Registry.LocalMachine.OpenSubKey(TaskTree);
        return tree?.GetSubKeyNames().Where(n => n.StartsWith(u.TaskPrefix, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
    }

    static async Task RemoveUpdaterAsync(BrowserUpdater u, CancellationToken ct)
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        foreach (var service in Services(u).ToList())
        {
            await ProcessRunner.RunAsync(sc, $"stop {service}", ct: ct);
            await ProcessRunner.RunAsync(sc, $"delete {service}", ct: ct);
        }
        foreach (var task in Tasks(u).ToList())
            await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN {ProcessRunner.Quote(task)} /F", ct: ct);
        foreach (var key in u.Keys) Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        DebloatFiles.Delete(u.Folders.Where(DebloatFiles.IsFolder));
    }
}
