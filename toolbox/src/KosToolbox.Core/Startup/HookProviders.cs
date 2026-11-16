using System.Text;
using System.Text.Json;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Places Windows loads code from without a Run entry, as Autoruns lists them: AppInit DLLs, image hijacks (IFEO Debugger),
/// boot execute, LSA packages, print monitors, known DLLs and Winsock providers. Read-only: they belong to Windows or to the
/// program that added them, and a wrong change can stop Windows from starting or signing in.
/// </summary>
public sealed class SystemHookProvider : IStartupProvider
{
    const string NtCurrent = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    const string NtCurrent32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion";
    const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    const string Lsa = @"SYSTEM\CurrentControlSet\Control\Lsa";
    const string PrintMonitors = @"SYSTEM\CurrentControlSet\Control\Print\Monitors";
    const string Winsock = @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters";

    public string Name => Txt.SourceHooks;
    public StartupGroup Group => StartupGroup.Hooks;
    public bool AdvancedOnly => true;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var system32 = Environment.SystemDirectory;

        // AppInit DLLs: loaded into every program that loads user32 when LoadAppInit_DLLs is 1 (and Secure Boot is off).
        foreach (var (path, bits) in new[] { (NtCurrent, ""), (NtCurrent32, " (32-bit)") })
        {
            using var k = hklm.OpenSubKey($@"{path}\Windows");
            if (k?.GetValue("AppInit_DLLs") is not string dlls || dlls.Trim().Length == 0) continue;
            var on = k.GetValue("LoadAppInit_DLLs") is int i && i == 1;
            foreach (var dll in dlls.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(Entry($"appinit|{path}|{dll}", Path.GetFileName(dll), $@"HKLM\{path}\Windows", Txt.SourceAppInit + bits, dll, Expand(dll),
                    Txt.TypeAppInit, on ? Txt.AppInitOn : Txt.AppInitOff, on, $@"HKLM\{path}\Windows\AppInit_DLLs"));
        }

        // Image hijacks: Windows starts the Debugger instead of the program (KOS uses systray.exe to keep programs from starting).
        foreach (var (path, bits) in new[] { (NtCurrent, ""), (NtCurrent32, " (32-bit)") })
        {
            using var root = hklm.OpenSubKey($@"{path}\Image File Execution Options");
            if (root is null) continue;
            foreach (var exe in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(exe);
                if (k?.GetValue("Debugger", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string debugger || debugger.Trim().Length == 0) continue;
                var kos = debugger.TrimEnd('"', ' ').EndsWith(@"\systray.exe", StringComparison.OrdinalIgnoreCase);
                list.Add(Entry($"ifeo|{path}|{exe}", exe, $@"HKLM\{path}\Image File Execution Options\{exe}", Txt.SourceIfeo + bits, debugger,
                    CommandLine.Executable(debugger), Txt.TypeIfeo, kos ? Txt.IfeoKos(exe) : Txt.IfeoDetail(exe), true,
                    $@"HKLM\{path}\Image File Execution Options\{exe}\Debugger"));
            }
        }

        using (var sm = hklm.OpenSubKey(SessionManager))
        {
            // Boot execute: native programs smss runs before Windows starts (autocheck autochk * is Windows' own disk check).
            if (sm?.GetValue("BootExecute") is string[] boot)
                foreach (var line in boot.Where(l => l.Trim().Length > 0))
                {
                    var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var program = words.Length > 1 && words[0].Equals("autocheck", StringComparison.OrdinalIgnoreCase) ? words[1] : words[0];
                    list.Add(Entry($"bootexecute|{line}", line, $@"HKLM\{SessionManager}", Txt.SourceBootExecute, line,
                        Path.Combine(system32, program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program : program + ".exe"),
                        Txt.TypeBootExecute, Txt.BootExecuteDetail, true, $@"HKLM\{SessionManager}\BootExecute"));
                }
            // Known DLLs: loaded from System32 by name for every program, never from the program's own folder.
            using var known = hklm.OpenSubKey($@"{SessionManager}\KnownDLLs");
            if (known is not null)
                foreach (var name in known.GetValueNames())
                    if (known.GetValue(name) is string dll && dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        list.Add(Entry($"knowndll|{name}", dll, $@"HKLM\{SessionManager}\KnownDLLs", Txt.SourceKnownDlls, dll, Path.Combine(system32, dll),
                            Txt.TypeKnownDll, Txt.KnownDllDetail, true, $@"HKLM\{SessionManager}\KnownDLLs\{name}"));
        }

        // LSA: packages lsass loads at boot (authentication, security support and password filters).
        foreach (var (path, value) in new[] { (Lsa, "Authentication Packages"), (Lsa, "Notification Packages"), (Lsa, "Security Packages"), ($@"{Lsa}\OSConfig", "Security Packages") })
        {
            using var k = hklm.OpenSubKey(path);
            if (k?.GetValue(value) is not string[] packages) continue;
            foreach (var package in packages.Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0))
            {
                var file = package.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? package : package + ".dll";
                list.Add(Entry($"lsa|{path}|{value}|{package}", package, $@"HKLM\{path}", Txt.SourceLsa, package,
                    Path.IsPathRooted(file) ? file : Path.Combine(system32, file), Txt.TypeLsa, Txt.LsaDetail(value), true, $@"HKLM\{path}\{value}"));
            }
        }

        // Print monitors: DLLs the print spooler loads when it starts.
        using (var monitors = hklm.OpenSubKey(PrintMonitors))
            if (monitors is not null)
                foreach (var name in monitors.GetSubKeyNames())
                {
                    using var k = monitors.OpenSubKey(name);
                    if (k?.GetValue("Driver") is not string dll || dll.Trim().Length == 0) continue;
                    list.Add(Entry($"printmonitor|{name}", name, $@"HKLM\{PrintMonitors}\{name}", Txt.SourcePrintMonitors, dll,
                        Path.IsPathRooted(dll) ? dll : Path.Combine(system32, dll), Txt.TypePrintMonitor, Txt.PrintMonitorDetail, true, $@"HKLM\{PrintMonitors}\{name}\Driver"));
                }

        // Winsock: transport and name-space providers loaded into every program that uses the network (one row per DLL).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (catalog, bits) in new[] { ("Catalog_Entries64", ""), ("Catalog_Entries", " (32-bit)") })
        {
            using (var protocols = hklm.OpenSubKey($@"{Winsock}\Protocol_Catalog9\{catalog}"))
                if (protocols is not null)
                    foreach (var id in protocols.GetSubKeyNames())
                    {
                        using var k = protocols.OpenSubKey(id);
                        if (k?.GetValue("PackedCatalogItem") is not byte[] packed || PackedPath(packed) is not { } dll || !seen.Add(dll + bits)) continue;
                        list.Add(Entry($"winsock|{catalog}|{dll}", Path.GetFileName(dll), $@"HKLM\{Winsock}\Protocol_Catalog9\{catalog}", Txt.SourceWinsock + bits, dll,
                            Expand(dll), Txt.TypeWinsock, Txt.WinsockDetail, true, $@"HKLM\{Winsock}\Protocol_Catalog9\{catalog}\{id}\PackedCatalogItem"));
                    }
            using var spaces = hklm.OpenSubKey($@"{Winsock}\NameSpace_Catalog5\{catalog}");
            if (spaces is null) continue;
            foreach (var id in spaces.GetSubKeyNames())
            {
                using var k = spaces.OpenSubKey(id);
                if (k?.GetValue("LibraryPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string dll || !seen.Add(dll + bits)) continue;
                var title = k.GetValue("DisplayString") as string;
                list.Add(Entry($"winsockns|{catalog}|{dll}", string.IsNullOrWhiteSpace(title) || title.StartsWith('@') ? Path.GetFileName(dll) : title,
                    $@"HKLM\{Winsock}\NameSpace_Catalog5\{catalog}", Txt.SourceWinsock + bits, dll, Expand(dll), Txt.TypeWinsock, Txt.WinsockDetail, true,
                    $@"HKLM\{Winsock}\NameSpace_Catalog5\{catalog}\{id}\LibraryPath"));
            }
        }
        return list;
    }

    /// <summary>The provider DLL at the start of a WSAPROTOCOL catalog item (ANSI, NUL-terminated, at most MAX_PATH).</summary>
    internal static string? PackedPath(byte[] packed)
    {
        var end = Array.IndexOf(packed, (byte)0, 0, Math.Min(packed.Length, 260));
        var path = Encoding.Default.GetString(packed, 0, end < 0 ? Math.Min(packed.Length, 260) : end).Trim();
        return path.Length > 0 ? path : null;
    }

    static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim('"'));

    StartupEntry Entry(string id, string name, string location, string source, string command, string? image, string type, string detail, bool enabled, string value) => new()
    {
        Provider = this,
        Id = "hook|" + id,
        Name = name,
        Location = location,
        Source = source,
        Command = command,
        ImagePath = image is not null && File.Exists(image) ? image : null,
        Enabled = enabled,
        StartupType = type,
        StatusDetail = detail,
        ReadOnlyReason = Txt.HooksReadOnly,
        MachineWide = true,
        WindowsComponent = CommandLine.InWindowsFolder(image),
        Details = new[] { RunKeyProvider.Pair(Txt.DetailRegistryValue, value) },
    };

    public Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct) => throw new NotSupportedException(Txt.HooksReadOnly);
}

/// <summary>
/// WMI event consumers that run a command or a script (root\subscription): they start when their event filter fires, with no
/// Run entry or task. Read-only.
/// </summary>
public sealed class WmiSubscriptionProvider : IStartupProvider
{
    public string Name => Txt.SourceWmi;
    public StartupGroup Group => StartupGroup.Hooks;
    public bool AdvancedOnly => true;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        const string script =
            "$c = Get-CimInstance -Namespace root/subscription -ClassName __EventConsumer -ErrorAction Stop | Where-Object { $_.CimClass.CimClassName -in 'CommandLineEventConsumer','ActiveScriptEventConsumer' }; " +
            "ConvertTo-Json -Compress -InputObject @($c | ForEach-Object { @{ Name = [string]$_.Name; Class = $_.CimClass.CimClassName; " +
            "Command = [string]$(if ($_.CommandLineTemplate) { $_.CommandLineTemplate } elseif ($_.ExecutablePath) { $_.ExecutablePath } elseif ($_.ScriptFileName) { $_.ScriptFileName } else { $_.ScriptText }) } })";
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var r = ProcessRunner.Run(ps, $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"", TimeSpan.FromSeconds(45));
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Output.Trim().Split('\n')[0]);
        return Parse(r.Output).Select(c =>
        {
            var image = c.Class == "CommandLineEventConsumer" ? CommandLine.Executable(c.Command) : null;
            return new StartupEntry
            {
                Provider = this,
                Id = $"wmi|{c.Class}|{c.Name}",
                Name = c.Name,
                Location = @"root\subscription",
                Source = Txt.SourceWmi,
                Command = c.Command.Length > 300 ? c.Command[..300] + "…" : c.Command,
                ImagePath = image,
                Enabled = true,
                StartupType = c.Class == "CommandLineEventConsumer" ? Txt.TypeWmiCommand : Txt.TypeWmiScript,
                StatusDetail = Txt.WmiDetail,
                ReadOnlyReason = Txt.HooksReadOnly,
                MachineWide = true,
                WindowsComponent = CommandLine.InWindowsFolder(image),
            };
        }).ToList();
    }

    internal static List<(string Name, string Class, string Command)> Parse(string json)
    {
        var start = json.IndexOf('[');
        if (start < 0) return new();
        using var doc = JsonDocument.Parse(json[start..]);
        return doc.RootElement.EnumerateArray().Select(e => (
            e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
            e.TryGetProperty("Class", out var c) ? c.GetString() ?? "" : "",
            e.TryGetProperty("Command", out var m) ? m.GetString() ?? "" : "")).ToList();
    }

    public Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct) => throw new NotSupportedException(Txt.HooksReadOnly);
}
