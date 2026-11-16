namespace KosToolbox.Core.Settings;

/// <summary>
/// State of the hosts files swapped by GDID-Tracking.cmd (read-only; the script does the swap):
/// "1" = blocking active (hosts_1 exists, hosts_gdid doesn't), "0" = no blocking (the reverse).
/// Any other combination, including a pending .tmp, is treated as unknown.
/// </summary>
public sealed class HostsGdidSetting : ISetting
{
    static string Dir => Path.Combine(Environment.SystemDirectory, "drivers", "etc");

    public string Key => "file:hosts-gdid";
    public string Display => @"System32\drivers\etc\hosts (hosts_1 / hosts_gdid)";
    public bool MachineWide => true;
    public string? Unavailable() => File.Exists(Path.Combine(Dir, "hosts")) ? null : "The hosts file is missing.";

    public string? Read()
    {
        bool E(string n) => File.Exists(Path.Combine(Dir, n));
        if (E("hosts_1.tmp") || E("hosts_gdid.tmp"))
            throw new InvalidOperationException("A hosts .tmp file from an earlier operation exists; resolve it first.");
        if (E("hosts_1") && !E("hosts_gdid")) return "1";
        if (E("hosts_gdid") && !E("hosts_1")) return "0";
        throw new InvalidOperationException("The hosts files are in an ambiguous state (both hosts_1 and hosts_gdid exist, or neither).");
    }

    public void Write(string? value) =>
        throw new NotSupportedException("The hosts files are only swapped by GDID-Tracking.cmd.");
}
