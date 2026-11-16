using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Enable flag in Explorer\StartupApproved, the same one Task Manager uses: 12 bytes, first
/// byte even = enabled, odd = disabled. The original entry (Run value or file in the folder) is never deleted.
/// </summary>
public static class StartupApproved
{
    public const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Even byte = enabled (0x02, 0x06); odd = disabled (0x03, 0x07).</summary>
    internal static bool IsEnabledFlag(byte b) => (b & 1) == 0;

    internal static RegistryKey OpenBase(Hive hive) =>
        RegistryKey.OpenBaseKey(hive == Hive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    /// <summary>Reads the flag. No value = enabled (Windows default); unexpected type or read failure = unknown.</summary>
    public static (bool? Enabled, string Detail) Read(Hive hive, string subKey, string name)
    {
        try
        {
            using var root = OpenBase(hive);
            using var key = root.OpenSubKey($@"{Root}\{subKey}");
            var raw = key?.GetValue(name);
            return raw switch
            {
                null => (true, Txt.ApprovedAbsent),
                byte[] { Length: > 0 } b => (IsEnabledFlag(b[0]), Txt.ApprovedFlag(b[0], IsEnabledFlag(b[0]))),
                _ => (null, Txt.ApprovedUnexpected),
            };
        }
        catch (Exception ex) { return (null, Txt.ApprovedReadError(ex.Message)); }
    }

    /// <summary>12 bytes: flag + FILETIME of the change, as Task Manager writes it.</summary>
    public static string Value(bool enabled)
    {
        var data = new byte[12];
        data[0] = (byte)(enabled ? 0x02 : 0x03);
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        return Convert.ToHexString(data);
    }

    public static async Task WriteAsync(Hive hive, string subKey, string name, bool enabled, CancellationToken ct)
    {
        var hex = Value(enabled);
        var setting = new RegistrySetting(hive, $@"{Root}\{subKey}", name, RegistryValueKind.Binary);
        if (hive == Hive.LocalMachine && !Elevation.IsAdmin)
            await ElevatedRunner.RunAsync(new ElevatedJob { Writes = { new JobWrite { Key = setting.Key, Value = hex } } }, ct).ConfigureAwait(false);
        else
            setting.Write(hex);
        if (Read(hive, subKey, name).Enabled != enabled) throw new InvalidOperationException(Txt.ChangeNotConfirmed);
    }
}

/// <summary>Run and RunOnce values in HKCU and HKLM (64-bit and 32-bit views).</summary>
public sealed class RunKeyProvider : IStartupProvider
{
    const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    const string Wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\";

    // HKCU\Software hasn't been redirected for 32-bit since Windows 7, so there is a single view for the user.
    static readonly (Hive Hive, string Path, string? Approved, string Label)[] Locations =
    {
        (Hive.CurrentUser, Run, "Run", Txt.SourceRunHkcu),
        (Hive.CurrentUser, RunOnce, null, Txt.SourceRunOnceHkcu),
        (Hive.LocalMachine, Run, "Run", Txt.SourceRunHklm),
        (Hive.LocalMachine, Wow + "Run", "Run32", Txt.SourceRunHklm32),
        (Hive.LocalMachine, RunOnce, null, Txt.SourceRunOnceHklm),
        (Hive.LocalMachine, Wow + "RunOnce", null, Txt.SourceRunOnceHklm32),
    };

    public string Name => Txt.SourceRunKeys;
    public StartupGroup Group => StartupGroup.Logon;
    public bool AdvancedOnly => false;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        foreach (var (hive, path, approved, label) in Locations)
        {
            using var root = StartupApproved.OpenBase(hive);
            using var key = root.OpenSubKey(path);
            if (key is null) continue;
            var location = $@"{(hive == Hive.LocalMachine ? "HKLM" : "HKCU")}\{path}";
            foreach (var name in key.GetValueNames())
            {
                var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var command = raw as string ?? "";
                bool? enabled; string detail;
                if (raw is not string) { enabled = null; detail = Txt.RunValueNotText; }
                else if (approved is null) { enabled = true; detail = Txt.RunOnceDetail; }
                else (enabled, detail) = StartupApproved.Read(hive, approved, name);
                var image = CommandLine.Executable(command);
                list.Add(new StartupEntry
                {
                    Provider = this,
                    Id = $"run|{location}|{name}",
                    Name = name.Length == 0 ? Txt.DefaultValueName : name,
                    Location = location,
                    Source = label,
                    Command = command,
                    ImagePath = image,
                    Enabled = enabled,
                    StartupType = approved is null ? Txt.TypeRunOnce : Txt.TypeLogon,
                    StatusDetail = detail,
                    ReadOnlyReason = approved is null ? Txt.RunOnceReadOnly : null,
                    MachineWide = hive == Hive.LocalMachine,
                    WindowsComponent = CommandLine.InWindowsFolder(image),
                    Hive = hive.ToString(), ApprovedSubKey = approved, ValueName = name,
                    Details = new[] { Pair(Txt.DetailRegistryValue, $@"{location}\{(name.Length == 0 ? Txt.DefaultValueName : name)}") },
                });
            }
        }
        return list;
    }

    public Task SetEnabledAsync(StartupEntry e, bool enable, CancellationToken ct) =>
        StartupApproved.WriteAsync(Enum.Parse<Hive>(e.Hive!), e.ApprovedSubKey!, e.ValueName!, enable, ct);

    internal static KeyValuePair<string, string> Pair(string k, string v) => new(k, v);
}

/// <summary>Programs set by policy (Policies\Explorer\Run). Read-only: they are managed by the policy.</summary>
public sealed class PolicyRunProvider : IStartupProvider
{
    const string Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";

    public string Name => Txt.SourcePolicyRun;
    public StartupGroup Group => StartupGroup.Logon;
    public bool AdvancedOnly => false;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        foreach (var hive in new[] { Hive.CurrentUser, Hive.LocalMachine })
        {
            using var root = StartupApproved.OpenBase(hive);
            using var key = root.OpenSubKey(Path);
            if (key is null) continue;
            var location = $@"{(hive == Hive.LocalMachine ? "HKLM" : "HKCU")}\{Path}";
            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var image = CommandLine.Executable(command);
                list.Add(new StartupEntry
                {
                    Provider = this,
                    Id = $"policy|{location}|{name}",
                    Name = name,
                    Location = location,
                    Source = hive == Hive.LocalMachine ? Txt.SourcePolicyHklm : Txt.SourcePolicyHkcu,
                    Command = command,
                    ImagePath = image,
                    Enabled = true,
                    StartupType = Txt.TypeLogon,
                    StatusDetail = Txt.PolicyDetail,
                    ReadOnlyReason = Txt.PolicyReadOnly,
                    MachineWide = hive == Hive.LocalMachine,
                    WindowsComponent = CommandLine.InWindowsFolder(image),
                    Details = new[] { RunKeyProvider.Pair(Txt.DetailRegistryValue, $@"{location}\{name}") },
                });
            }
        }
        return list;
    }

    public Task SetEnabledAsync(StartupEntry e, bool enable, CancellationToken ct) => throw new InvalidOperationException(Txt.PolicyReadOnly);
}

/// <summary>Files in the Startup folders of the user and of all users.</summary>
public sealed class StartupFolderProvider : IStartupProvider
{
    public string Name => Txt.SourceStartupFolders;
    public StartupGroup Group => StartupGroup.Logon;
    public bool AdvancedOnly => false;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        foreach (var hive in new[] { Hive.CurrentUser, Hive.LocalMachine })
        {
            var folder = Environment.GetFolderPath(hive == Hive.LocalMachine ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
            if (folder.Length == 0 || !Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = System.IO.Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var (enabled, detail) = StartupApproved.Read(hive, "StartupFolder", name);
                string command = file;
                string? image = file;
                var details = new List<KeyValuePair<string, string>> { RunKeyProvider.Pair(Txt.DetailFile, file) };
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var link = ShellLink.Read(file);
                        image = link.Target;
                        command = link.Target is null ? file : $"\"{link.Target}\"{(link.Arguments is null ? "" : " " + link.Arguments)}";
                        details.Add(RunKeyProvider.Pair(Txt.DetailShortcutTarget, link.Target ?? Txt.ShortcutNoTarget));
                        if (link.Arguments is not null) details.Add(RunKeyProvider.Pair(Txt.DetailArguments, link.Arguments));
                        if (link.WorkingDirectory is not null) details.Add(RunKeyProvider.Pair(Txt.DetailWorkingDirectory, link.WorkingDirectory));
                        if (link.Target is not null && !File.Exists(Environment.ExpandEnvironmentVariables(link.Target)))
                            detail += " " + Txt.ShortcutBroken;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Startup: couldn't read shortcut {file}: {ex.Message}");
                        details.Add(RunKeyProvider.Pair(Txt.DetailShortcutTarget, Txt.ShortcutUnreadable(ex.Message)));
                    }
                }
                list.Add(new StartupEntry
                {
                    Provider = this,
                    Id = $"folder|{file}",
                    Name = System.IO.Path.GetFileNameWithoutExtension(name),
                    Location = folder,
                    Source = hive == Hive.LocalMachine ? Txt.SourceStartupFolderAll : Txt.SourceStartupFolderUser,
                    Command = command,
                    ImagePath = image,
                    Enabled = enabled,
                    StartupType = Txt.TypeLogon,
                    StatusDetail = detail,
                    MachineWide = hive == Hive.LocalMachine,
                    WindowsComponent = CommandLine.InWindowsFolder(image),
                    Hive = hive.ToString(), ApprovedSubKey = "StartupFolder", ValueName = name,
                    Details = details,
                });
            }
        }
        return list;
    }

    public Task SetEnabledAsync(StartupEntry e, bool enable, CancellationToken ct) =>
        StartupApproved.WriteAsync(Enum.Parse<Hive>(e.Hive!), e.ApprovedSubKey!, e.ValueName!, enable, ct);
}
