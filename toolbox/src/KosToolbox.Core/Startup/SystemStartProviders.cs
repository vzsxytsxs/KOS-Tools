using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Startup;

/// <summary>
/// Active Setup: commands Windows runs once for each user at their next sign-in (Installed Components → StubPath).
/// Shown for reference only: the entries belong to their installers.
/// </summary>
public sealed class ActiveSetupProvider : IStartupProvider
{
    static readonly (string Path, string Label)[] Roots =
    {
        (@"SOFTWARE\Microsoft\Active Setup\Installed Components", Txt.SourceActiveSetup),
        (@"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components", Txt.SourceActiveSetup32),
    };

    public string Name => Txt.SourceActiveSetup;
    public StartupGroup Group => StartupGroup.Logon;
    public bool AdvancedOnly => true;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var (path, label) in Roots)
        {
            using var root = hklm.OpenSubKey(path);
            if (root is null) continue;
            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id);
                if (key?.GetValue("StubPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string stub || stub.Trim().Length == 0) continue;
                var installed = key.GetValue("IsInstalled") is not int i || i != 0;
                var title = key.GetValue("") as string;
                var image = CommandLine.Executable(stub);
                list.Add(new StartupEntry
                {
                    Provider = this,
                    Id = $"activesetup|{path}|{id}",
                    Name = string.IsNullOrWhiteSpace(title) || title.StartsWith('@') ? id : title,
                    Location = $@"HKLM\{path}",
                    Source = label,
                    Command = stub,
                    ImagePath = image,
                    Enabled = installed,
                    StartupType = Txt.TypeActiveSetup,
                    StatusDetail = Txt.ActiveSetupDetail(installed),
                    ReadOnlyReason = Txt.ActiveSetupReadOnly,
                    MachineWide = true,
                    WindowsComponent = CommandLine.InWindowsFolder(image),
                    Details = new[] { RunKeyProvider.Pair(Txt.DetailRegistryValue, $@"HKLM\{path}\{id}\StubPath") },
                });
            }
        }
        return list;
    }

    public Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct) => throw new NotSupportedException(Txt.ActiveSetupReadOnly);
}

/// <summary>
/// Winlogon's Shell, Userinit, Taskman and AppSetup (and the per-user Shell): what Windows starts at every sign-in.
/// Shown for reference only: a wrong value can stop sign-in, so they aren't changed here.
/// </summary>
public sealed class WinlogonProvider : IStartupProvider
{
    const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    static readonly string[] Values = { "Shell", "Userinit", "Taskman", "AppSetup" };

    public string Name => Txt.SourceWinlogon;
    public StartupGroup Group => StartupGroup.Logon;
    public bool AdvancedOnly => true;

    public IEnumerable<StartupEntry> Collect(bool advanced)
    {
        var list = new List<StartupEntry>();
        foreach (var hive in new[] { Settings.Hive.LocalMachine, Settings.Hive.CurrentUser })
        {
            using var root = StartupApproved.OpenBase(hive);
            using var key = root.OpenSubKey(Winlogon);
            if (key is null) continue;
            var location = $@"{(hive == Settings.Hive.LocalMachine ? "HKLM" : "HKCU")}\{Winlogon}";
            foreach (var name in Values)
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command || command.Trim().Length == 0) continue;
                var image = CommandLine.Executable(command);
                list.Add(new StartupEntry
                {
                    Provider = this,
                    Id = $"winlogon|{location}|{name}",
                    Name = name,
                    Location = location,
                    Source = Txt.SourceWinlogon,
                    Command = command,
                    ImagePath = image,
                    Enabled = true,
                    StartupType = Txt.TypeWinlogon,
                    StatusDetail = Txt.WinlogonDetail,
                    ReadOnlyReason = Txt.WinlogonReadOnly,
                    MachineWide = hive == Settings.Hive.LocalMachine,
                    WindowsComponent = CommandLine.InWindowsFolder(image),
                    Details = new[] { RunKeyProvider.Pair(Txt.DetailRegistryValue, $@"{location}\{name}") },
                });
            }
        }
        return list;
    }

    public Task SetEnabledAsync(StartupEntry entry, bool enable, CancellationToken ct) => throw new NotSupportedException(Txt.WinlogonReadOnly);
}
