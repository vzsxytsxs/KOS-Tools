using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>
/// Existence of a user key whose (Default) value is empty text ("1" = exists, null = absent).
/// On removal, deletes the key and its parent, if that becomes empty. Used by COM overrides in HKCU\Software\Classes.
/// </summary>
public sealed class RegistryKeySetting(string path) : ISetting
{
    public string Path { get; } = path;
    public string Key => $@"regkey:HKCU\{Path}";
    public string Display => $@"HKCU\{Path} (key)";
    public bool MachineWide => false;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key is null ? null : "1";
    }

    public void Write(string? value)
    {
        if (value is not null)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path, writable: true);
            key.SetValue("", "", RegistryValueKind.String);
            return;
        }
        Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
        var parent = System.IO.Path.GetDirectoryName(Path);
        if (parent is null) return;
        using (var p = Registry.CurrentUser.OpenSubKey(parent))
            if (p is null || p.SubKeyCount > 0 || p.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
    }
}
