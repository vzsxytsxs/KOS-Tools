using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

/// <summary>KOS security profile, chosen in RunOnce and stored in HKLM\SOFTWARE\TheKOS\Profile.</summary>
public enum KosProfile { Basic, Advanced }

public static class KosProfiles
{
    // Read once: a profile change only takes effect after the PC restarts.
    static readonly Lazy<KosProfile?> current = new(() =>
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TheKOS");
            return Parse(k?.GetValue("Profile") as string);
        }
        catch { return null; }
    });

    /// <summary>This installation's profile; null if the registry couldn't be read.</summary>
    public static KosProfile? Current => current.Value;

    /// <summary>"basic" is Basic. Any other value is Advanced: "advanced", the old name "extreme" and installations
    /// without the value (KOS 4.0.0 or earlier, which behave like Advanced).</summary>
    internal static KosProfile Parse(string? value) =>
        string.Equals(value?.Trim(), "basic", StringComparison.OrdinalIgnoreCase) ? KosProfile.Basic : KosProfile.Advanced;
}
