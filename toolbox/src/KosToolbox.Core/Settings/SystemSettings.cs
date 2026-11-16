using System.Runtime.InteropServices;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace KosToolbox.Core.Settings;

/// <summary>
/// One name=value pair of the Windows graphics settings: HKCU\...\DirectX\UserGpuPreferences → DirectXUserGlobalSettings,
/// e.g. "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;". Writing replaces or appends the pair and keeps the others;
/// null removes it. An absent pair = Windows decides.
/// </summary>
public sealed class DxSetting(string name) : ISetting
{
    const string Path = @"Software\Microsoft\DirectX\UserGpuPreferences", Value = "DirectXUserGlobalSettings";

    public string Key => "dx:" + name;
    public string Display => $@"HKCU\{Path} → {Value} ({name})";
    public bool MachineWide => false;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return Get(key?.GetValue(Value) as string, name);
    }

    public void Write(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path, writable: true);
        var text = Set(key.GetValue(Value) as string, name, value);
        if (text.Length == 0) key.DeleteValue(Value, throwOnMissingValue: false);
        else key.SetValue(Value, text, RegistryValueKind.String);
    }

    internal static string? Get(string? settings, string name) =>
        Tokens(settings).Where(t => Is(t, name)).Select(t => t[(t.IndexOf('=') + 1)..].Trim()).FirstOrDefault();

    /// <summary>Replaces the pair where it is (or appends it); tokens it doesn't understand are kept as they are.</summary>
    internal static string Set(string? settings, string name, string? value)
    {
        var parts = new List<string>();
        var done = false;
        foreach (var token in Tokens(settings))
        {
            if (!Is(token, name)) { parts.Add(token); continue; }
            if (!done && value is not null) parts.Add($"{name}={value}");
            done = true;
        }
        if (!done && value is not null) parts.Add($"{name}={value}");
        return parts.Count == 0 ? "" : string.Join(";", parts) + ";";
    }

    static IEnumerable<string> Tokens(string? s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool Is(string token, string name)
    {
        var i = token.IndexOf('=');
        return i > 0 && token[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Windows Firewall rules by rule name (the language-neutral id, e.g. RemoteDesktop-UserMode-In-TCP): "1" = all enabled,
/// "0" = all disabled. Read from the rule store (Active=TRUE/FALSE); switched with Set-NetFirewallRule. Unavailable when
/// none of the rules exist, as after the Advanced profile deletes every rule.
/// </summary>
public sealed class FirewallRuleSetting(params string[] ids) : ISetting
{
    const string Rules = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    public string Key => "fwrule:" + string.Join(",", ids);
    public string Display => "Windows Firewall rules " + string.Join(", ", ids);
    public bool MachineWide => true;
    public string? Unavailable() => Existing().Count == 0 ? Txt.FirewallRulesMissing : null;

    Dictionary<string, string> Existing()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Rules);
        var found = new Dictionary<string, string>();
        foreach (var id in ids)
            if (key?.GetValue(id) is string rule) found[id] = rule;
        return found;
    }

    public string? Read()
    {
        var states = Existing().Values.Select(Active).Distinct().ToList();
        return states.Count switch { 0 => null, 1 => states[0] ? "1" : "0", _ => "mixed" };
    }

    internal static bool Active(string rule) => rule.Split('|').Any(p => p.Trim().Equals("Active=TRUE", StringComparison.OrdinalIgnoreCase));

    public void Write(string? value)
    {
        var enabled = value switch { "1" => "True", "0" => "False", _ => throw new ArgumentException($"Invalid firewall rule state: {value}") };
        var names = string.Join(",", Existing().Keys.Select(id => $"'{id}'"));
        if (names.Length == 0) return;
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var r = ProcessRunner.Run(ps, $"-NoProfile -NonInteractive -Command \"Set-NetFirewallRule -Name {names} -Enabled {enabled} -ErrorAction Stop\"");
        if (r.ExitCode != 0) throw new InvalidOperationException($"Set-NetFirewallRule failed ({r.ExitCode}): {r.Output.Trim()}");
    }
}

/// <summary>
/// Nagle's algorithm on every TCP/IP interface (Tcpip\Parameters\Interfaces\{id}), like Nagle Algorithm.bat: off writes
/// TcpAckFrequency 1, TCPNoDelay 1 and TcpDelAckTicks 0; on deletes them. "0" = off everywhere, "1" = on everywhere.
/// </summary>
public sealed class NagleSetting : ISetting
{
    const string Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public string Key => "tcpip:nagle";
    public string Display => @"HKLM\" + Interfaces + @"\* → TcpAckFrequency, TCPNoDelay";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var root = Registry.LocalMachine.OpenSubKey(Interfaces);
        var states = new HashSet<string>();
        foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var k = root!.OpenSubKey(name);
            states.Add(State(k?.GetValue("TcpAckFrequency"), k?.GetValue("TCPNoDelay")));
        }
        return states.Count switch { 0 => null, 1 => states.First(), _ => "mixed" };
    }

    internal static string State(object? ack, object? noDelay) => (ack, noDelay) switch
    {
        (1, 1) => "0",
        (null, null) => "1",
        _ => "mixed",
    };

    public void Write(string? value)
    {
        if (value is not ("0" or "1")) throw new ArgumentException($"Invalid Nagle state: {value}");
        using var root = Registry.LocalMachine.OpenSubKey(Interfaces);
        foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{Interfaces}\{name}", writable: true);
            if (k is null) continue;
            if (value == "1") { foreach (var v in new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" }) k.DeleteValue(v, throwOnMissingValue: false); continue; }
            k.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
            k.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
            k.SetValue("TcpDelAckTicks", 0, RegistryValueKind.DWord);
        }
    }
}

/// <summary>
/// Windows write-cache buffer flushing on every disk that disk.sys drives (Services\disk\Enum), USB drives excluded:
/// off = UserWriteCacheSetting and CacheIsPowerProtected 1 under Device Parameters\Disk, on = both absent (Windows default).
/// "0" = flushing off on every disk, "1" = on on every disk.
/// </summary>
public sealed class WriteCacheFlushSetting : ISetting
{
    static readonly string[] Values = { "UserWriteCacheSetting", "CacheIsPowerProtected" };

    public string Key => "disk:write-cache-flush";
    public string Display => @"HKLM\SYSTEM\CurrentControlSet\Enum\<each disk>\Device Parameters\Disk → UserWriteCacheSetting, CacheIsPowerProtected";
    public bool MachineWide => true;
    public string? Unavailable() => Disks().Count == 0 ? Txt.NoDisks : null;

    internal static List<string> Disks()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
        var count = key?.GetValue("Count") is int c ? c : 0;
        return Enumerable.Range(0, count).Select(i => key!.GetValue(i.ToString()) as string)
            .OfType<string>().Where(i => IsFixed(i) && !OnUsbBus(i)).ToList();
    }

    internal static bool IsFixed(string instance) =>
        instance.Length > 0 && !instance.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) && !instance.Contains("..");

    /// <summary>
    /// The disk sits on USB (UAS drives enumerate as SCSI\Disk, so the name alone misses them): the bus type from
    /// IOCTL_STORAGE_QUERY_PROPERTY, through the disk's device interface. Unreadable counts as not USB.
    /// </summary>
    static bool OnUsbBus(string instance)
    {
        using var h = CreateFile(@"\\?\" + instance.Replace('\\', '#') + "#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;
        var query = new byte[12]; // StorageDeviceProperty, PropertyStandardQuery
        var descriptor = new byte[1024];
        return DeviceIoControl(h, 0x2D1400, query, query.Length, descriptor, descriptor.Length, out _, IntPtr.Zero)
               && BitConverter.ToInt32(descriptor, 28) == 7; // STORAGE_DEVICE_DESCRIPTOR.BusType: BusTypeUsb
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    static RegistrySetting Value(string instance, string name) =>
        new(Hive.LocalMachine, $@"SYSTEM\CurrentControlSet\Enum\{instance}\Device Parameters\Disk", name, RegistryValueKind.DWord);

    public string? Read()
    {
        var states = Disks().Select(d => State(Value(d, Values[0]).Read(), Value(d, Values[1]).Read())).Distinct().ToList();
        return states.Count switch { 0 => null, 1 => states[0], _ => "mixed" };
    }

    internal static string State(string? cache, string? protectedCache) => (cache, protectedCache) switch
    {
        ("1", "1") => "0",
        (null, null) => "1",
        _ => "mixed",
    };

    public void Write(string? value)
    {
        if (value is not ("0" or "1")) throw new ArgumentException($"Invalid write-cache flushing state: {value}");
        foreach (var disk in Disks())
            foreach (var name in Values) Value(disk, name).Write(value == "0" ? "1" : null);
    }
}

/// <summary>
/// An optional Windows feature as servicing records the last request for it (Notifications\OptionalFeatures\{name} → Selection:
/// 1 = enabled, 0 = disabled; a change waiting for a restart already counts). A feature nobody changed has no entry and keeps
/// its default. Read-only: a reviewed KOS script runs DISM.
/// </summary>
public sealed class OptionalFeatureSetting(string name, bool enabledByDefault = false) : ISetting
{
    const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Notifications\OptionalFeatures";

    public string Key => "feature:" + name;
    public string Display => $"Optional feature {name}";
    public bool MachineWide => true;
    public string? Unavailable() => null;

    public string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{name}");
        return State(key?.GetValue("Selection"), enabledByDefault);
    }

    internal static string State(object? selection, bool enabledByDefault) => selection switch
    {
        1 => "1",
        0 => "0",
        _ => enabledByDefault ? "1" : "0",
    };

    public void Write(string? value) => throw new NotSupportedException("A reviewed KOS script turns optional features on or off.");
}

/// <summary>A Windows exe that KOS turns off by renaming it to .old ("on" = .exe in place, "off" = renamed). Read-only: a reviewed KOS script renames it.</summary>
public sealed class RenamedExeSetting(string key, string exe) : ISetting
{
    string Old => Path.ChangeExtension(exe, ".old");
    public string Key => key;
    public string Display => $"{exe} (.old = off)";
    public bool MachineWide => true;
    public string? Unavailable() => File.Exists(exe) || File.Exists(Old) ? null : Txt.FileMissing(exe);
    public string? Read() => (File.Exists(exe), File.Exists(Old)) switch
    {
        (true, true) => throw new InvalidOperationException(Txt.RenamedBoth(exe)),
        var (on, off) => StartMenus.HostState(on, off),
    };
    public void Write(string? value) => throw new NotSupportedException("A reviewed KOS script renames this file.");
}
