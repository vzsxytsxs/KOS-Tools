using KosToolbox.Core.Persistence;
using Microsoft.Win32;

namespace KosToolbox.Core.Gpu;

/// <summary>AMD display adapters: the display class keys whose driver provider is AMD (where the Radeon driver reads its overrides).</summary>
public static class AmdGpus
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Class key paths (HKLM-relative). Replaceable, so the UI probe can show the AMD section on a PC without one.</summary>
    public static Func<List<string>> ClassKeys { get; set; } = Find;

    static List<string> Find()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
                using var key = cls!.OpenSubKey(sub);
                if (IsAmd(key?.GetValue("ProviderName") as string)) list.Add($@"{DisplayClass}\{sub}");
            }
        }
        catch (Exception ex) { Log.Warn($"Listing AMD GPUs failed: {ex.Message}"); }
        return list;
    }

    internal static bool IsAmd(string? provider) =>
        provider is not null && (provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
                                 || provider.Equals("AMD", StringComparison.OrdinalIgnoreCase) || provider.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase));
}
