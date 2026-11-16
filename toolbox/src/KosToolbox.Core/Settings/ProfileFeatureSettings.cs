using Microsoft.Win32;

namespace KosToolbox.Core.Settings;

/// <summary>Per-process mitigation masks written by the Advanced profile.</summary>
public sealed class ProcessMitigationsSetting : ISetting
{
    internal static readonly string[] Processes =
    {
        "fontdrvhost.exe", "dwm.exe", "lsass.exe", "svchost.exe", "WmiPrvSE.exe",
        "winlogon.exe", "csrss.exe", "audiodg.exe", "ntoskrnl.exe", "services.exe",
    };
    const string Root = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public string Key => "mitigation:profile-processes";
    public string Display => "Per-process mitigation masks";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        var on = 0;
        var off = 0;
        foreach (var name in Processes)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{name}");
            var mitigation = key?.GetValue("MitigationOptions") as byte[];
            var audit = key?.GetValue("MitigationAuditOptions") as byte[];
            if (mitigation is null && audit is null) on++;
            else if (mitigation is { Length: > 0 } && audit is not null && mitigation.SequenceEqual(audit)
                && mitigation.All(b => b == 0x22)) off++;
        }
        return on == Processes.Length ? "on" : off == Processes.Length ? "off" : "partial";
    }

    public void Write(string? value) => throw new NotSupportedException("The KOS security feature script changes these masks.");
}

/// <summary>The backup says which DMA values Advanced zeroed.</summary>
public sealed class DmaRemappingSetting : ISetting
{
    const string Backup = @"SOFTWARE\TheKOS\ProfileBackup\DmaRemapping";

    public string Key => "mitigation:dma-remapping";
    public string Display => "Driver DMA remapping";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var backup = Registry.LocalMachine.OpenSubKey(Backup);
        var names = backup?.GetValueNames() ?? Array.Empty<string>();
        if (names.Length == 0) return "on";
        foreach (var full in names)
        {
            var relative = full.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ? full[5..]
                : full.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase) ? full[19..] : "";
            if (relative.Length == 0) return "partial";
            using var service = Registry.LocalMachine.OpenSubKey(relative);
            if (service?.GetValue("DmaRemappingCompatible") is not int value || value != 0) return "partial";
        }
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        return services is not null && !AnyNonZero(services) ? "off" : "partial";
    }

    static bool AnyNonZero(RegistryKey key)
    {
        if (key.GetValue("DmaRemappingCompatible") is int value && value != 0) return true;
        foreach (var name in key.GetSubKeyNames())
        {
            try
            {
                using var child = key.OpenSubKey(name);
                if (child is not null && AnyNonZero(child)) return true;
            }
            catch (UnauthorizedAccessException) { /* Protected driver; reading the backup still holds. */ }
            catch (System.Security.SecurityException) { /* Protected driver. */ }
        }
        return false;
    }

    public void Write(string? value) => throw new NotSupportedException("The KOS security feature script restores DMA values.");
}

/// <summary>
/// Compares the current rules with the Windows defaults (Defaults, where "netsh advfirewall reset" copies from). Rules
/// that apps and the Store create after Advanced don't count: only the default ones say whether the firewall was reset or emptied.
/// </summary>
public sealed class FirewallRulesSetting : ISetting
{
    const string Path = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
    const string Defaults = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Defaults\FirewallPolicy\FirewallRules";

    public string Key => "firewall:rules";
    public string Display => "Windows Firewall rules";
    public bool MachineWide => true;
    public string? Unavailable()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Path);
        using var defaults = Registry.LocalMachine.OpenSubKey(Defaults);
        return key is null ? "Firewall rules key is missing."
            : defaults is null || defaults.ValueCount == 0 ? "The default Windows Firewall rules are missing." : null;
    }

    public string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Path);
        using var defaults = Registry.LocalMachine.OpenSubKey(Defaults);
        return Classify(key?.GetValueNames() ?? Array.Empty<string>(), defaults?.GetValueNames() ?? Array.Empty<string>());
    }

    internal static string Classify(IEnumerable<string> current, IReadOnlyCollection<string> defaults)
    {
        var present = defaults.Intersect(current, StringComparer.OrdinalIgnoreCase).Count();
        return present == 0 ? "off" : present == defaults.Count ? "on" : "partial";
    }

    public void Write(string? value) => throw new NotSupportedException("The KOS security feature script changes firewall rules.");
}
