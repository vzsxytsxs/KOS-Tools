using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>
/// Which Start menu is active. The KOS script turns off the Windows one by renaming StartMenuExperienceHost.exe to .old;
/// Open-Shell starts with Explorer unless the "Start automatically for this user" option (AutoStart) is 0.
/// </summary>
public static class StartMenus
{
    public static string HostFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy");
    public static string HostExe => Path.Combine(HostFolder, "StartMenuExperienceHost.exe");
    public static string HostOld => Path.Combine(HostFolder, "StartMenuExperienceHost.old");
    public static string OpenShellExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Open-Shell\StartMenu.exe");
    internal const string OpenShellSettings = @"Software\OpenShell\StartMenu\Settings";

    /// <summary>"on" = .exe in place; "off" = renamed to .old; null = neither. Both together say nothing.</summary>
    internal static string? HostState(bool exe, bool old) => (exe, old) switch
    {
        (true, false) => "on",
        (false, true) => "off",
        (false, false) => null,
        _ => throw new InvalidOperationException(Txt.StartMenuHostBoth),
    };

    // shortcut: ignores the Open-Shell policies (HKLM/HKCU\Software\Policies\OpenShell), which KOS doesn't use; read them if they start being used.
    /// <summary>Open-Shell AutoStart: only the DWORD 0 turns it off; absent is its default (start).</summary>
    internal static string AutoStart(object? raw) => raw is int v && v == 0 ? "off" : "on";

    internal static object? ReadAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(OpenShellSettings);
        return key?.GetValue("AutoStart");
    }

    /// <summary>Reason the Start menu search doesn't apply (Open-Shell is the menu, or the Windows one is turned off); null = applies.</summary>
    internal static string? SearchUnavailable(bool hostExe, bool openShellStarts) =>
        openShellStarts ? Txt.StartSearchOpenShell : !hostExe ? Txt.StartSearchNoWindowsMenu : null;

    public static string? SearchUnavailable() =>
        SearchUnavailable(File.Exists(HostExe), File.Exists(OpenShellExe) && AutoStart(ReadAutoStart()) == "on");
}

/// <summary>Windows Start menu on ("on") or renamed to .old ("off"); read-only, the KOS script does the switching.</summary>
public sealed class StartMenuHostSetting : ISetting
{
    public string Key => "file:start-menu-host";
    public string Display => @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe (.old = off)";
    public bool MachineWide => true;
    public string? Unavailable() => File.Exists(StartMenus.HostExe) || File.Exists(StartMenus.HostOld) ? null : Txt.StartMenuHostMissing;
    public string? Read() => StartMenus.HostState(File.Exists(StartMenus.HostExe), File.Exists(StartMenus.HostOld));
    public void Write(string? value) => throw new NotSupportedException("The Windows Start menu is switched by the reviewed KOS script.");
}

/// <summary>Open-Shell starting with Windows for this user ("on"/"off"); read-only, the KOS script does the switching.</summary>
public sealed class OpenShellAutoStartSetting : ISetting
{
    public string Key => "openshell:autostart";
    public string Display => @"HKCU\" + StartMenus.OpenShellSettings + " → AutoStart (Open-Shell starts at sign-in)";
    public bool MachineWide => false;
    public string? Unavailable() => File.Exists(StartMenus.OpenShellExe) ? null : Txt.OpenShellMissing;
    public string? Read() => StartMenus.AutoStart(StartMenus.ReadAutoStart());
    public void Write(string? value) => throw new NotSupportedException("Open-Shell is switched by the reviewed KOS script.");
}
