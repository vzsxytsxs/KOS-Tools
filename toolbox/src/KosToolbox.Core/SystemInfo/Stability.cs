using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace KosToolbox.Core.SystemInfo;

public enum StabilityKind { BlueScreen, UnexpectedRestart, DriverReset, HardwareError, DumpFailed }

/// <summary>One crash or hardware event from the System log; <see cref="Severe"/> = the PC went down.</summary>
public sealed record StabilityEvent(DateTime Time, StabilityKind Kind, string Title, string Detail, bool Severe);

public sealed record CrashDump(string Path, DateTime Time, long Size);

/// <summary>
/// Crash and hardware-error history, like BlueScreenView with HWiNFO's WHEA count: Kernel-Power 41 (restart without a clean
/// shutdown, with the bug check if there was one), WER 1001 (blue screen and its dump), EventLog 6008, volmgr 46 (no dump
/// written), Display 4101 (driver reset, TDR) and WHEA-Logger 17/18/19/47 (hardware errors). Read through wevtapi, no administrator.
/// </summary>
public static class Stability
{
    const string Channel = "System";
    const string Providers =
        "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008)" +
        " or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or (Provider[@Name='volmgr'] and EventID=46)" +
        " or (Provider[@Name='Display'] and EventID=4101)" +
        " or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=47))";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Newest first, up to <paramref name="max"/>; with <paramref name="since"/>, only events logged from then on.</summary>
    public static List<StabilityEvent> Events(int max = 500, DateTime? since = null)
    {
        var query = since is { } from
            ? $"*[System[({Providers}) and TimeCreated[@SystemTime>='{from.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]"
            : $"*[System[{Providers}]]";
        return Read(Channel, query, max, Parse);
    }

    /// <summary>Events of <paramref name="channel"/> matching <paramref name="query"/> (XPath), newest first, through <paramref name="parse"/> (null = skipped).</summary>
    internal static List<T> Read<T>(string channel, string query, int max, Func<string, T?> parse) where T : class
    {
        var list = new List<T>();
        var results = EvtQuery(IntPtr.Zero, channel, query, 0x1 | 0x200); // channel path, newest first
        if (results == IntPtr.Zero) throw new InvalidOperationException($"The {channel} log couldn't be read ({Marshal.GetLastWin32Error()}).");
        var buffer = IntPtr.Zero;
        var size = 0;
        try
        {
            var events = new IntPtr[64];
            while (list.Count < max && EvtNext(results, events.Length, events, 1000, 0, out var returned))
                for (var i = 0; i < returned; i++)
                {
                    try
                    {
                        if (!EvtRender(IntPtr.Zero, events[i], 1, size, buffer, out var used, out _)) // 1 = the event as XML
                        {
                            if (Marshal.GetLastWin32Error() != 122) continue; // ERROR_INSUFFICIENT_BUFFER
                            Marshal.FreeHGlobal(buffer);
                            buffer = Marshal.AllocHGlobal(size = used);
                            if (!EvtRender(IntPtr.Zero, events[i], 1, size, buffer, out used, out _)) continue;
                        }
                        if (list.Count < max && parse(Marshal.PtrToStringUni(buffer)!) is { } e) list.Add(e);
                    }
                    catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or OverflowException or NullReferenceException) { } // one unreadable event is skipped
                    finally { EvtClose(events[i]); }
                }
        }
        finally
        {
            EvtClose(results);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    /// <summary>The newest System log event matching <paramref name="query"/> (XPath), as XML; null when none matches.</summary>
    internal static string? NewestXml(string query)
    {
        var results = EvtQuery(IntPtr.Zero, Channel, query, 0x1 | 0x200); // channel path, newest first
        if (results == IntPtr.Zero) throw new InvalidOperationException($"The System log couldn't be read ({Marshal.GetLastWin32Error()}).");
        var events = new IntPtr[1];
        try
        {
            if (!EvtNext(results, 1, events, 1000, 0, out var returned) || returned == 0) return null;
            try
            {
                EvtRender(IntPtr.Zero, events[0], 1, 0, IntPtr.Zero, out var used, out _);
                var buffer = Marshal.AllocHGlobal(used);
                try { return EvtRender(IntPtr.Zero, events[0], 1, used, buffer, out _, out _) ? Marshal.PtrToStringUni(buffer) : null; }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { EvtClose(events[0]); }
        }
        finally { EvtClose(results); }
    }

    internal static StabilityEvent? Parse(string xml)
    {
        var e = XDocument.Parse(xml).Root!;
        var system = e.Element(Ns + "System")!;
        var provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
        var id = int.Parse(system.Element(Ns + "EventID")!.Value, CultureInfo.InvariantCulture);
        var time = DateTime.Parse(system.Element(Ns + "TimeCreated")!.Attribute("SystemTime")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();
        var data = e.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new();
        string? Named(string name) => data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
        string? At(int i) => i < data.Count ? data[i].Value : null;
        switch (provider, id)
        {
            case ("Microsoft-Windows-Kernel-Power", 41):
                var code = ulong.TryParse(Named("BugcheckCode"), out var c) ? c : 0;
                var button = Named("PowerButtonTimestamp") is { } b && b != "0";
                return new StabilityEvent(time, StabilityKind.UnexpectedRestart, "Restarted without shutting down cleanly",
                    code != 0 ? $"After a blue screen: {BugCheck(code)}"
                    : button ? "The power button was held down."
                    : "No crash code: a power cut, a reset, a hard freeze or an unstable overclock or undervolt.", true);
            case ("Microsoft-Windows-WER-SystemErrorReporting", 1001):
                var text = Named("param1") ?? At(0) ?? "";
                var hex = text.Split(' ', 2)[0];
                var name = ulong.TryParse(hex.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out var bc) ? BugCheck(bc) : hex;
                var dump = Named("param2") ?? At(1);
                return new StabilityEvent(time, StabilityKind.BlueScreen, "Blue screen", $"{name}{(string.IsNullOrEmpty(dump) ? "" : $" · dump {dump}")}", true);
            case ("EventLog", 6008):
                return new StabilityEvent(time, StabilityKind.UnexpectedRestart, "Unexpected shutdown", "The shutdown before this start wasn't clean.", true);
            case ("volmgr", 46):
                return new StabilityEvent(time, StabilityKind.DumpFailed, "No crash dump written",
                    "Windows couldn't prepare the crash dump: the page file may be off or too small, so a blue screen leaves no dump.", false);
            case ("Display", 4101):
                return new StabilityEvent(time, StabilityKind.DriverReset, "Display driver reset (TDR)",
                    $"{At(0) ?? "The display driver"} stopped responding and recovered: often an unstable GPU overclock, or a hung game.", false);
            case ("Microsoft-Windows-WHEA-Logger", var w):
                var source = Named("ErrorSource") is { } s ? WheaSource(s) : null;
                var device = Named("PrimaryDeviceName") ?? Named("DeviceName");
                var title = w switch { 18 => "Fatal hardware error", 47 => "Corrected memory error", _ => "Corrected hardware error" };
                var detail = string.Join(" · ", new[] { source, string.IsNullOrEmpty(device) ? null : device,
                    w == 18 ? "the PC stopped: CPU, memory or bus fault (often an unstable overclock or undervolt)" : "the hardware fixed it, but repeats point to an unstable overclock, a riser or a failing part" }
                    .Where(x => x is not null));
                return new StabilityEvent(time, StabilityKind.HardwareError, title, detail, w == 18);
        }
        return null;
    }

    /// <summary>The bug checks seen most after overclocking, driver and hardware problems; others show only their code.</summary>
    public static string BugCheck(ulong code) => $"0x{code:X8}" + (code switch
    {
        0x0A => " IRQL_NOT_LESS_OR_EQUAL", 0x19 => " BAD_POOL_HEADER", 0x1A => " MEMORY_MANAGEMENT", 0x1E => " KMODE_EXCEPTION_NOT_HANDLED",
        0x3B => " SYSTEM_SERVICE_EXCEPTION", 0x50 => " PAGE_FAULT_IN_NONPAGED_AREA", 0x7E => " SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        0x7F => " UNEXPECTED_KERNEL_MODE_TRAP", 0x9F => " DRIVER_POWER_STATE_FAILURE", 0xA0 => " INTERNAL_POWER_ERROR", 0xC2 => " BAD_POOL_CALLER",
        0xD1 => " DRIVER_IRQL_NOT_LESS_OR_EQUAL", 0xEF => " CRITICAL_PROCESS_DIED", 0xF4 => " CRITICAL_OBJECT_TERMINATION",
        0x101 => " CLOCK_WATCHDOG_TIMEOUT", 0x109 => " CRITICAL_STRUCTURE_CORRUPTION", 0x116 => " VIDEO_TDR_FAILURE", 0x117 => " VIDEO_TDR_TIMEOUT_DETECTED",
        0x119 => " VIDEO_SCHEDULER_INTERNAL_ERROR", 0x124 => " WHEA_UNCORRECTABLE_ERROR", 0x12B => " FAULTY_HARDWARE_CORRUPTED_PAGE",
        0x133 => " DPC_WATCHDOG_VIOLATION", 0x139 => " KERNEL_SECURITY_CHECK_FAILURE", 0x13A => " KERNEL_MODE_HEAP_CORRUPTION",
        0x154 => " UNEXPECTED_STORE_EXCEPTION", 0x1A1 => " WIN32K_CALLOUT_WATCHDOG_LIVEDUMP", 0x1E2 => " CRASHDUMP_WATCHDOG_TIMEOUT",
        _ => "",
    });

    /// <summary>WHEA_ERROR_SOURCE_TYPE as the logger writes it (a number or its name).</summary>
    static string WheaSource(string s) => s switch
    {
        "0" or "MCE" => "Machine check (CPU)", "1" or "CMC" => "Corrected machine check (CPU)", "2" or "CPE" => "Corrected platform error",
        "3" or "NMI" => "NMI", "4" or "PCIe" => "PCI Express", "6" or "INIT" => "INIT", "7" or "BOOT" => "Boot error", "8" or "SCIGeneric" => "Platform (SCI)",
        "10" or "IPFMCA" or "11" or "IPFCMC" => "Processor", "13" or "Generic" => "Generic hardware", _ => $"Source {s}",
    };

    /// <summary>Kernel crash dumps (Minidump folder and MEMORY.DMP); reading them needs administrator.</summary>
    public static List<CrashDump> Dumps()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var files = new List<FileInfo>();
        var mini = new DirectoryInfo(Path.Combine(windows, "Minidump"));
        if (mini.Exists) files.AddRange(mini.EnumerateFiles("*.dmp"));
        var full = new FileInfo(Path.Combine(windows, "MEMORY.DMP"));
        if (full.Exists) files.Add(full);
        return files.Select(f => new CrashDump(f.FullName, f.LastWriteTime, f.Length)).OrderByDescending(d => d.Time).ToList();
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] static extern bool EvtNext(IntPtr results, int count, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)] static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, IntPtr buffer, out int used, out int properties);
    [DllImport("wevtapi.dll")] static extern bool EvtClose(IntPtr handle);
}
