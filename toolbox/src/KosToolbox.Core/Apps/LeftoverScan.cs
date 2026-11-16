using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Apps;

public static partial class Leftovers
{
    // ---------- where the registry can be deleted ----------

    // Keys below these prefixes can be removed (only below: never the prefix itself). Values only in the listed trace keys.
    static readonly string[] KeyPrefixes =
    {
        @"HKCU\Software\", @"HKLM\SOFTWARE\", @"HKLM\SOFTWARE\WOW6432Node\",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\",
        @"HKLM\SOFTWARE\Microsoft\Tracing\", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Tracing\",
        @"HKCU\Control Panel\NotifyIconSettings\",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\",
        @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PolicyCache\",
        @"HKLM\SOFTWARE\Classes\Installer\Products\", @"HKLM\SOFTWARE\Classes\Installer\Features\",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\", @"HKCU\Software\Microsoft\Installer\Products\",
    };

    // Under Software\<X>, these X belong to Windows: neither they nor anything below them are removed by the generic prefix.
    static readonly HashSet<string> SystemSoftwareKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Microsoft", "Classes", "Policies", "WOW6432Node", "RegisteredApplications", "Clients", "ODBC", "Windows", "Intel", "Wow6432Node" };

    const string Run = @"Microsoft\Windows\CurrentVersion\Run", RunOnce = @"Microsoft\Windows\CurrentVersion\RunOnce";
    const string MuiCache = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    const string Layers = @"Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string CompatStore = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";
    const string FeatureUsage = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage";
    static readonly string[] FeatureUsageKeys = { "AppSwitched", "ShowJumpView", "AppBadgeUpdated", "AppLaunch" };

    static readonly string[] ValueKeys =
    {
        @"HKCU\Software\" + Run, @"HKCU\Software\" + RunOnce, @"HKLM\SOFTWARE\" + Run, @"HKLM\SOFTWARE\" + RunOnce,
        @"HKLM\SOFTWARE\WOW6432Node\" + Run, @"HKLM\SOFTWARE\WOW6432Node\" + RunOnce, MuiCache, @"HKCU\Software\" + Layers,
        @"HKLM\SOFTWARE\" + Layers, CompatStore,
    };

    /// <summary>The key can be removed: below an allowed prefix and, under the generic Software\ prefix, outside the Windows keys.</summary>
    internal static bool KeyAllowed(string key)
    {
        foreach (var prefix in KeyPrefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Length == prefix.Length) continue;
            var first = key[prefix.Length..].Split('\\')[0];
            if (first.Length == 0) continue;
            var generic = prefix is @"HKCU\Software\" or @"HKLM\SOFTWARE\" or @"HKLM\SOFTWARE\WOW6432Node\";
            if (!generic || !SystemSoftwareKeys.Contains(first)) return true;
        }
        return false;
    }

    internal static bool ValueAllowed(string key) =>
        ValueKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || FeatureUsageKeys.Any(k => key.Equals($@"{FeatureUsage}\{k}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Null if the item can still be removed now; otherwise, the reason (the same test as the scan, repeated at delete time).</summary>
    public static string? CannotRemove(Leftover item)
    {
        var allowed = item.Kind switch
        {
            LeftoverKind.Folder => Full(item.Path) is { } p && p.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && !IsProtected(p),
            // Loose files: only Start Menu and Desktop shortcuts.
            LeftoverKind.File => Full(item.Path) is { } f && f.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && IsShortcut(f),
            LeftoverKind.RegistryKey => KeyAllowed(item.Path),
            LeftoverKind.RegistryValue => ValueAllowed(item.Path) && item.ValueName is { Length: > 0 },
            LeftoverKind.Service => item.Path.Length > 0 && item.Path.IndexOfAny(new[] { '\\', '/', '"' }) < 0,
            LeftoverKind.Task => item.Path.StartsWith('\\') && !item.Path.Contains("..") && !item.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!allowed) return Txt.LeftoverNotAllowed;
        return item.MachineWide && !Execution.Elevation.IsAdmin ? Txt.LeftoverNeedsAdmin : null;
    }

    static bool IsShortcut(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
        && (InFolder(path, Environment.SpecialFolder.Programs) || InFolder(path, Environment.SpecialFolder.CommonPrograms)
            || InFolder(path, Environment.SpecialFolder.DesktopDirectory) || InFolder(path, Environment.SpecialFolder.CommonDesktopDirectory));

    static bool InFolder(string path, Environment.SpecialFolder folder) => Full(Environment.GetFolderPath(folder)) is { } f && IsInside(path, f);

    // ---------- scan ----------

    /// <summary>
    /// Leftovers of the program, without deleting anything. <paramref name="force"/> (Force removal): the program is still in the list;
    /// its folder and Uninstall key are included, and it does not protect its own files.
    /// </summary>
    public static List<Leftover> Scan(ProgramTrace t, bool force = false)
    {
        var others = InstalledPrograms.List().Where(p => p.Id != t.ProgramId).ToList();
        var otherFolders = others.Select(FolderOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = ProductNames(t.Name, t.Folder);
        // A name that another installed program also has does not identify this one (Python 3.11 and 3.12 are both "Python").
        names.ExceptWith(others.SelectMany(p => ProductNames(p.Name, FolderOf(p))));
        var vendors = VendorNames(t.Publisher);
        // The folder only matches traces by path if it is not protected (System32 would match Windows services) and
        // is not also used by another installed program (the Riot Client of LoL and TFT). Exception: the Store package
        // in WindowsApps, which only leads to account items (UserOnly).
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var folder = t.Folder is { } f && UsableFolder(f) && (!IsProtected(f) || t.PackageFamily is not null && IsInside(f, windowsApps))
            && !otherFolders.Any(o => IsInside(f, o) || IsInside(o, f)) ? f : null;
        var c = new Collector(otherFolders, t.UserOnly);

        // Install folder that was left behind (or, with Force removal, the program's own).
        if (folder is not null && Directory.Exists(folder)) c.Folder(folder, Txt.LeftoverInstallFolder);
        ScanFolders(c, names, vendors, t, folder);
        if (folder is not null) ScanShortcuts(c, folder);
        ScanStartMenuFolders(c, names);
        ScanKeys(c, names, vendors, t);
        var uninstallLeft = t.UninstallKey is { } uninstall && KeyExists(uninstall);
        if (uninstallLeft) c.Key(t.UninstallKey!, Txt.LeftoverUninstallEntry);
        // Windows Installer registration (keys by product code): with Force removal, or if the MSI left the list but is still registered.
        if (t.MsiProductCode is { } code && PackedGuid(code) is { } packed && (force || !uninstallLeft))
            foreach (var key in new[] { $@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", $@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}",
                         $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}", $@"HKCU\Software\Microsoft\Installer\Products\{packed}" })
                c.KeyIfExists(key, Txt.LeftoverMsi);
        if (folder is not null) ScanTraces(c, folder, t.Executables);
        if (t.PackageFamily is { Length: > 0 } family) ScanPackage(c, family);
        return c.Result();
    }

    static void ScanFolders(Collector c, IReadOnlySet<string> names, IReadOnlySet<string> vendors, ProgramTrace t, string? folder)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            KnownFolder(LocalLowId), KnownFolder(UserProgramFilesId), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        foreach (var root in roots.Select(Full).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var dir in Children(root))
            {
                var name = Path.GetFileName(dir);
                // Above the program folder (e.g. Programs\Python with Python312 inside): only removed when empty.
                if (Matches(name, names)) c.Folder(dir, Txt.LeftoverNamed, onlyIfEmpty: folder is not null && IsInside(folder, dir) && !folder.Equals(dir, StringComparison.OrdinalIgnoreCase));
                else if (Matches(name, vendors)) VendorFolder(c, dir, names, t.Executables);
            }
    }

    /// <summary>Vendor folder: the program's subfolders (by name, or the .NET settings ones "app.exe_Url_…") and the folder itself, if it ends up empty.</summary>
    static void VendorFolder(Collector c, string vendor, IReadOnlySet<string> names, IReadOnlyList<string> exes)
    {
        var children = Children(vendor).ToList();
        var flagged = 0;
        foreach (var dir in children)
        {
            var name = Path.GetFileName(dir);
            var dotNet = exes.Any(e => name.StartsWith(e + ".exe_", StringComparison.OrdinalIgnoreCase) || name.StartsWith(e + "_Url_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(e + "_StrongName_", StringComparison.OrdinalIgnoreCase));
            if (Matches(name, names) || dotNet) { c.Folder(dir, dotNet ? Txt.LeftoverDotNetSettings : Txt.LeftoverNamed); flagged++; }
        }
        bool hasFiles;
        try { hasFiles = Directory.EnumerateFiles(vendor).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (!hasFiles && flagged == children.Count) c.Folder(vendor, Txt.LeftoverVendor, onlyIfEmpty: true);
    }

    /// <summary>Subfolders that are not links (a link can lead anywhere).</summary>
    static IEnumerable<string> Children(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Start Menu and Desktop shortcuts that point inside the program folder.</summary>
    static void ScanShortcuts(Collector c, string folder)
    {
        var places = new[]
        {
            (Environment.SpecialFolder.Programs, true), (Environment.SpecialFolder.CommonPrograms, true),
            (Environment.SpecialFolder.DesktopDirectory, false), (Environment.SpecialFolder.CommonDesktopDirectory, false),
        };
        foreach (var (place, recurse) in places)
        {
            var dir = Environment.GetFolderPath(place);
            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions { RecurseSubdirectories = recurse, MaxRecursionDepth = 4, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var lnk in links)
            {
                try
                {
                    if (ShellLink.Read(lnk).Target is { } target && Full(target) is { } t && IsInside(t, folder)) c.File(lnk, Txt.LeftoverShortcut);
                }
                catch (Exception ex) { Log.Warn($"Leftovers: couldn't read {lnk}: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Start Menu folders named after the program: only removed when empty (shortcuts to the program folder go first,
    /// one by one); a name equal to a shared group does not take other programs' shortcuts.
    /// </summary>
    static void ScanStartMenuFolders(Collector c, IReadOnlySet<string> names)
    {
        foreach (var place in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            foreach (var dir in Children(Environment.GetFolderPath(place)))
                if (Matches(Path.GetFileName(dir), names)) c.Folder(dir, Txt.LeftoverStartMenu, onlyIfEmpty: true);
    }

    /// <summary>Software\&lt;program&gt; and Software\&lt;vendor&gt;\&lt;program&gt; for the user and the machine (64 and 32-bit).</summary>
    static void ScanKeys(Collector c, IReadOnlySet<string> names, IReadOnlySet<string> vendors, ProgramTrace t)
    {
        foreach (var root in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
        {
            using var key = OpenKey(root);
            if (key is null) continue;
            foreach (var sub in SubKeys(key))
            {
                if (SystemSoftwareKeys.Contains(sub)) continue;
                var path = $@"{root}\{sub}";
                if (Matches(sub, names)) { c.Key(path, Txt.LeftoverNamed); continue; }
                if (!Matches(sub, vendors)) continue;
                using var vendor = key.OpenSubKey(sub);
                if (vendor is null) continue;
                var children = SubKeys(vendor);
                var flagged = 0;
                foreach (var child in children)
                    if (Matches(child, names) || t.Executables.Any(e => Norm(child) == Norm(e))) { c.Key($@"{path}\{child}", Txt.LeftoverNamed); flagged++; }
                if (vendor.ValueCount == 0 && flagged == children.Count) c.Key(path, Txt.LeftoverVendor, onlyIfEmpty: true);
            }
        }
    }

    /// <summary>Registry entries that mention the program path: startup, App Paths, shell names, compatibility, usage, tray, services and tasks.</summary>
    static void ScanTraces(Collector c, string folder, IReadOnlyList<string> exes)
    {
        foreach (var path in ValueKeys)
        {
            using var key = OpenKey(path);
            if (key is null) continue;
            var byData = path.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                var hit = byData ? StartsInside(key.GetValue(name) as string, folder) : StartsInside(name, folder);
                if (hit) c.Value(path, name, byData ? Txt.LeftoverRun : path == MuiCache ? Txt.LeftoverMuiCache : Txt.LeftoverCompat);
            }
        }
        foreach (var k in FeatureUsageKeys)
        {
            var path = $@"{FeatureUsage}\{k}";
            using var key = OpenKey(path);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
                if (StartsInside(ExpandKnownFolder(name, KnownFolder), folder)) c.Value(path, name, Txt.LeftoverUsage);
        }
        foreach (var root in new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                     @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths" })
        {
            using var key = OpenKey(root);
            if (key is null) continue;
            foreach (var sub in SubKeys(key))
            {
                using var app = key.OpenSubKey(sub);
                if (StartsInside(app?.GetValue("") as string, folder) || StartsInside(app?.GetValue("Path") as string, folder)) c.Key($@"{root}\{sub}", Txt.LeftoverAppPaths);
            }
        }
        using (var icons = OpenKey(@"HKCU\Control Panel\NotifyIconSettings"))
            if (icons is not null)
                foreach (var sub in SubKeys(icons))
                {
                    using var icon = icons.OpenSubKey(sub);
                    if (icon?.GetValue("ExecutablePath") is string exe && StartsInside(ExpandKnownFolder(exe, KnownFolder), folder))
                        c.Key($@"HKCU\Control Panel\NotifyIconSettings\{sub}", Txt.LeftoverTrayIcon);
                }
        // RAS tracing that Windows creates named after the .exe (Tracing\app_RASAPI32).
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Tracing", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Tracing" })
        {
            using var key = OpenKey(root);
            if (key is null) continue;
            foreach (var sub in SubKeys(key))
                if (exes.Any(e => sub.Equals(e + "_RASAPI32", StringComparison.OrdinalIgnoreCase) || sub.Equals(e + "_RASMANCS", StringComparison.OrdinalIgnoreCase)))
                    c.Key($@"{root}\{sub}", Txt.LeftoverTracing);
        }
        using (var services = OpenKey(@"HKLM\SYSTEM\CurrentControlSet\Services"))
            if (services is not null)
                foreach (var sub in SubKeys(services))
                {
                    using var s = services.OpenSubKey(sub);
                    if (StartsInside(s?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, folder)) c.Service(sub, Txt.LeftoverService);
                }
        try
        {
            foreach (var (task, program) in ScheduledTaskProvider.ExecActions())
                if (StartsInside(program, folder)) c.Task(task, Txt.LeftoverTask);
        }
        catch (Exception ex) { Log.Warn($"Leftovers: scheduled tasks couldn't be read: {ex.Message}"); }
    }

    /// <summary>Store app: the data folder and the user settings named after the package (the AppX database is not touched).</summary>
    static void ScanPackage(Collector c, string family)
    {
        if (Full(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family)) is { } data && Directory.Exists(data))
            c.Folder(data, Txt.LeftoverPackageData);
        c.KeyIfExists($@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\{family}", Txt.LeftoverPackageSettings);
        c.KeyIfExists($@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PolicyCache\{family}", Txt.LeftoverPackageSettings);
        const string notifications = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        using var key = OpenKey(notifications);
        if (key is not null)
            foreach (var sub in SubKeys(key))
                if (sub.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase)) c.Key($@"{notifications}\{sub}", Txt.LeftoverPackageSettings);
    }

    // ---------- registry ----------

    /// <summary>"HKCU\..." or "HKLM\..." in the 64-bit view (WOW6432Node is spelled out in the path).</summary>
    internal static (RegistryHive Hive, string Sub)? SplitKey(string path) =>
        path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? (RegistryHive.CurrentUser, path[5..])
        : path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ? (RegistryHive.LocalMachine, path[5..]) : null;

    internal static RegistryKey? OpenKey(string path, bool writable = false)
    {
        if (SplitKey(path) is not { } k) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(k.Hive, RegistryView.Registry64);
            return root.OpenSubKey(k.Sub, writable);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    internal static bool KeyExists(string path)
    {
        using var key = OpenKey(path);
        return key is not null;
    }

    static List<string> SubKeys(RegistryKey key)
    {
        try { return key.GetSubKeyNames().ToList(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return new(); }
    }

    /// <summary>MSI product code in the Windows Installer key format ({12345678-ABCD-...} → 87654321DCBA...).</summary>
    internal static string? PackedGuid(string productCode)
    {
        if (!Guid.TryParse(productCode, out var guid)) return null;
        var s = guid.ToString("N").ToUpperInvariant();
        static string Rev(string x) => new(x.Reverse().ToArray());
        var parts = new List<string> { Rev(s[..8]), Rev(s[8..12]), Rev(s[12..16]) };
        for (var i = 16; i < 32; i += 2) parts.Add(Rev(s.Substring(i, 2)));
        return string.Concat(parts);
    }

    // ---------- collection ----------

    /// <summary>Gathers the items applying the protections; at the end drops what is already inside another item and measures the folders.</summary>
    sealed class Collector(IReadOnlyList<string> otherFolders, bool userOnly)
    {
        readonly List<Leftover> _items = new();
        readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

        void Add(Leftover item)
        {
            if (userOnly && item.MachineWide) return; // an entry the account writes does not lead to machine items
            if (CannotRemove(item) is not { } why || why == Txt.LeftoverNeedsAdmin) { if (_ids.Add(item.Id)) _items.Add(item); }
        }

        public void Folder(string path, string reason, bool onlyIfEmpty = false)
        {
            if (Full(path) is not { } p || IsProtected(p) || IsLink(p)) return;
            // Folder of another installed program, inside one, or containing one: stays.
            if (otherFolders.Any(o => IsInside(p, o) || IsInside(o, p))) return;
            Add(new Leftover(LeftoverKind.Folder, p, null, reason, !InUserProfile(p), onlyIfEmpty));
        }

        public void File(string path, string reason)
        {
            if (Full(path) is not { } p || IsLink(p) || otherFolders.Any(o => IsInside(p, o))) return;
            Add(new Leftover(LeftoverKind.File, p, null, reason, !InUserProfile(p)));
        }

        public void Key(string path, string reason, bool onlyIfEmpty = false) =>
            Add(new Leftover(LeftoverKind.RegistryKey, path, null, reason, path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase), onlyIfEmpty));

        public void KeyIfExists(string path, string reason) { if (KeyExists(path)) Key(path, reason); }

        public void Value(string key, string name, string reason) =>
            Add(new Leftover(LeftoverKind.RegistryValue, key, name, reason, key.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)));

        public void Service(string name, string reason) => Add(new Leftover(LeftoverKind.Service, name, null, reason, true));

        public void Task(string path, string reason) => Add(new Leftover(LeftoverKind.Task, path, null, reason, true));

        public List<Leftover> Result()
        {
            var trees = _items.Where(i => i.Kind == LeftoverKind.Folder && !i.OnlyIfEmpty).Select(i => i.Path).ToList();
            var keys = _items.Where(i => i.Kind == LeftoverKind.RegistryKey && !i.OnlyIfEmpty).Select(i => i.Path).ToList();
            bool Covered(Leftover i) => i.Kind switch
            {
                LeftoverKind.Folder or LeftoverKind.File => trees.Any(t => !t.Equals(i.Path, StringComparison.OrdinalIgnoreCase) && IsInside(i.Path, t)),
                LeftoverKind.RegistryKey => keys.Any(k => !k.Equals(i.Path, StringComparison.OrdinalIgnoreCase) && IsInside(i.Path, k)),
                LeftoverKind.RegistryValue => keys.Any(k => IsInside(i.Path, k)),
                _ => false,
            };
            return _items.Where(i => !Covered(i)).Select(i => i.Kind switch
            {
                LeftoverKind.Folder when !i.OnlyIfEmpty => i with { Size = FolderSize(i.Path) },
                LeftoverKind.File => i with { Size = SafeLength(i.Path) },
                _ => i,
            }).ToList();
        }
    }

    static long? SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return null; }
    }

    /// <summary>Sum of the files (not following links), up to 200 thousand files.</summary>
    internal static long? FolderSize(string dir)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(dir).EnumerateFiles("*", options).Take(200_000).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
