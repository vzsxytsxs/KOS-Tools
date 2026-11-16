using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

public sealed record InfoRow(string Label, string Value);

/// <summary>KOS and system data read from the registry (read-only).</summary>
public static class SystemSummary
{
    /// <summary>KOS version and the post-install result, written by RunOnce.cmd (for About).</summary>
    public static List<InfoRow> KosRows()
    {
        var rows = new List<InfoRow>();
        void Add(string label, Func<string?> get)
        {
            string value;
            try { value = get() ?? Txt.NotFound; }
            catch (Exception ex) { value = Txt.ReadErrorWith(ex.Message); }
            rows.Add(new(label, value));
        }

        Add(Txt.InfoKosVersion, () =>
        {
            var v = Reg(Registry.LocalMachine, @"SOFTWARE\TheKOS", "currentver");
            var model = Reg(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", "Model");
            return v is null && model is null ? null : $"{model ?? "KOS"} (currentver {v ?? "?"})";
        });
        Add(Txt.InfoPostSetup, () => PostSetupLog.Latest()?.Summary);
        return rows;
    }

    /// <summary>Pending reboot requested by Windows itself (updates, file renames).</summary>
    public static List<string> WindowsRebootReasons()
    {
        var reasons = new List<string>();
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (k is not null) reasons.Add(Txt.RebootComponentServicing);
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (k is not null) reasons.Add(Txt.RebootWindowsUpdate);
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (k?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) reasons.Add(Txt.RebootPendingFiles);
        }
        catch (Exception ex) { reasons.Add(Txt.RebootCheckFailed(ex.Message)); }
        return reasons;
    }

    static string? Reg(RegistryKey root, string path, string name)
    {
        using var k = root.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }
}
