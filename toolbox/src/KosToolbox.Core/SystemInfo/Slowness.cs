using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using KosToolbox.Core.Startup;
using KosToolbox.Core.Text;
using Microsoft.Win32;

namespace KosToolbox.Core.SystemInfo;

public enum DiskSpeed { Idle, Fine, Slow, VerySlow }

public sealed record HungWindow(string Process, string Title);
/// <summary>One physical disk over the sample: average time per request, queue length and % busy.</summary>
public sealed record DiskSample(string Name, double LatencyMs, double Queue, double BusyPercent, double TransfersPerSecond)
{
    public DiskSpeed Speed => Slowness.Rate(LatencyMs, TransfersPerSecond);
}
public sealed record MemorySample(double CommitPercent, double AvailableMb, double TotalMb)
{
    public bool UnderPressure => CommitPercent >= Slowness.CommitPressurePercent || AvailableMb < TotalMb * Slowness.AvailablePressureShare;
}
public sealed record AppHang(DateTime Time, string Program, string? Path);
public sealed record HangGroup(string Program, int Count, DateTime Last, string? Path);
public sealed record BootProgram(string Name, string Path, double SecondsAfterBoot);

/// <summary>What the Diagnose › Slow page shows; <see cref="DisksNote"/> is set when the disk counters couldn't be read.</summary>
public sealed record SlowSample(IReadOnlyList<HungWindow> Hung, IReadOnlyList<DiskSample> Disks, string? DisksNote, MemorySample Memory);

/// <summary>
/// "Why does this PC feel slow?" from data Windows already has, read only and without administrator: windows not responding,
/// disk latency (PhysicalDisk counters), memory pressure, Application Hang events (1002) and when the sign-in programs started.
/// </summary>
public static class Slowness
{
    public const double SlowDiskMs = 20, VerySlowDiskMs = 50;
    /// <summary>Below this many requests per second a disk is idle: one slow request on an idle disk says nothing.</summary>
    public const double IdleTransfers = 1;
    public const double CommitPressurePercent = 90, AvailablePressureShare = 0.05;
    /// <summary>A program that hung this often in 30 days is worth naming when nothing else stands out.</summary>
    public const int FrequentHangs = 3;
    /// <summary>A sign-in program started later than this was started again by hand, not at sign-in.</summary>
    public static readonly TimeSpan MaxStartupOffset = TimeSpan.FromMinutes(15);
    public const string BootLog = "Microsoft-Windows-Diagnostics-Performance/Operational";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static DiskSpeed Rate(double latencyMs, double transfersPerSecond) =>
        transfersPerSecond < IdleTransfers ? DiskSpeed.Idle : latencyMs >= VerySlowDiskMs ? DiskSpeed.VerySlow : latencyMs >= SlowDiskMs ? DiskSpeed.Slow : DiskSpeed.Fine;

    /// <summary>One or two sentences, the most pressing first.</summary>
    public static IReadOnlyList<string> Verdict(IReadOnlyList<HungWindow> hung, IReadOnlyList<DiskSample> disks, MemorySample? memory, IReadOnlyList<HangGroup> hangs)
    {
        var lines = new List<string>();
        if (hung.Count > 0) lines.Add(Txt.SlowVerdictHung(hung.Select(h => h.Process).Distinct(StringComparer.OrdinalIgnoreCase)));
        if (disks.Where(d => d.Speed is DiskSpeed.Slow or DiskSpeed.VerySlow).MaxBy(d => d.LatencyMs) is { } disk) lines.Add(Txt.SlowVerdictDisk(disk.Name, disk.LatencyMs));
        if (memory is { UnderPressure: true }) lines.Add(Txt.SlowVerdictMemory(memory.CommitPercent, memory.AvailableMb));
        if (hangs.Where(h => h.Count >= FrequentHangs).MaxBy(h => h.Count) is { } often) lines.Add(Txt.SlowVerdictHangs(often.Program, often.Count));
        return lines.Count == 0 ? new[] { Txt.SlowVerdictNothing } : lines.Take(2).ToList();
    }

    // ---------- now ----------

    /// <summary>Samples the disks for <paramref name="sample"/> (blocking), then reads the windows and memory.</summary>
    public static SlowSample Sample(TimeSpan sample)
    {
        var disks = new List<DiskSample>();
        string? note = null;
        try
        {
            using var pdh = new Pdh();
            var latency = pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
            var queue = pdh.Add(@"\PhysicalDisk(*)\Avg. Disk Queue Length");
            var idle = pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
            var transfers = pdh.Add(@"\PhysicalDisk(*)\Disk Transfers/sec");
            if (latency == IntPtr.Zero || idle == IntPtr.Zero || transfers == IntPtr.Zero) note = Txt.SlowDisksNoCounters;
            else
            {
                pdh.Collect();
                Thread.Sleep(sample);
                pdh.Collect();
                var l = pdh.Values(latency); var q = queue == IntPtr.Zero ? new() : pdh.Values(queue); var i = pdh.Values(idle); var t = pdh.Values(transfers);
                // Instances "0 C: D:": disk number and its letters.
                foreach (var instance in i.Keys.Where(k => k != "_Total").OrderBy(k => k))
                {
                    var parts = instance.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var name = parts.Length > 1 ? Txt.SlowDiskName(parts[0], parts[1]) : Txt.SlowDiskName(parts[0], null);
                    disks.Add(new DiskSample(name, l.GetValueOrDefault(instance) * 1000, q.GetValueOrDefault(instance),
                        Math.Clamp(100 - i[instance], 0, 100), t.GetValueOrDefault(instance)));
                }
                if (disks.Count == 0) note = Txt.SlowDisksNoCounters;
            }
        }
        catch (Exception ex) { note = ex.Message; }

        var m = LiveMonitor.Memory();
        var memory = new MemorySample(m.CommitLimit == 0 ? 0 : 100.0 * m.Committed / m.CommitLimit, (m.Total - m.Used) / 1048576.0, m.Total / 1048576.0);
        return new SlowSample(HungWindows(), disks, note, memory);
    }

    /// <summary>Visible top-level windows Windows marks as not responding (no input for 5 s). No message is sent to them.</summary>
    public static List<HungWindow> HungWindows()
    {
        var list = new List<HungWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !IsHungAppWindow(h)) return true;
            var title = new StringBuilder(256);
            InternalGetWindowText(h, title, title.Capacity); // never blocks on a hung window, unlike GetWindowText
            GetWindowThreadProcessId(h, out var pid);
            string process;
            try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
            catch (Exception) { process = $"PID {pid}"; }
            list.Add(new HungWindow(process, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------- recent hangs ----------

    /// <summary>Application Hang (1002) events of the last <paramref name="days"/> days, newest first.</summary>
    public static List<AppHang> Hangs(int days = 30) =>
        Stability.Read("Application", $"*[System[Provider[@Name='Application Hang'] and EventID=1002 and TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]]]", 1000, xml =>
        {
            try { return ParseHang(xml); }
            catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or NullReferenceException) { return null; }
        });

    internal static AppHang? ParseHang(string xml)
    {
        var e = XDocument.Parse(xml).Root!;
        var system = e.Element(Ns + "System")!;
        if (system.Element(Ns + "Provider")?.Attribute("Name")?.Value != "Application Hang" || system.Element(Ns + "EventID")?.Value != "1002") return null;
        var time = DateTime.Parse(system.Element(Ns + "TimeCreated")!.Attribute("SystemTime")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();
        var data = e.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new();
        string? Field(string name, int index) => (data.FirstOrDefault(d => (string?)d.Attribute("Name") == name) ?? (index < data.Count ? data[index] : null))?.Value is { Length: > 0 } v ? v : null;
        return Field("AppName", 0) is { } program ? new AppHang(time, program, Field("ExeFileName", 5)) : null;
    }

    /// <summary>By program, most hangs first; a clock set to the future once logged events years ahead, so those are left out.</summary>
    public static List<HangGroup> Group(IEnumerable<AppHang> hangs, DateTime now) =>
        hangs.Where(h => h.Time <= now.AddDays(1))
            .GroupBy(h => h.Program, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HangGroup(g.First().Program, g.Count(), g.Max(h => h.Time), g.Select(h => h.Path).FirstOrDefault(p => p is not null)))
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Last).ToList();

    // ---------- since boot ----------

    public static DateTime BootTime() => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>HiberbootEnabled: true = Fast Startup on (a shutdown hibernates the kernel, so "boot" is the last full restart).</summary>
    public static bool? FastStartup()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        return key?.GetValue("HiberbootEnabled") is int v ? v != 0 : null;
    }

    /// <summary>Events in Windows' boot-timing log; null when it couldn't be opened (missing or not readable).</summary>
    public static bool? BootLogEmpty()
    {
        try { return Stability.Read(BootLog, "*", 1, x => x).Count == 0; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Seconds from boot to the start; null when the process started before boot (clock change) or long after (started again).</summary>
    public static double? StartOffset(DateTime start, DateTime boot)
    {
        var offset = start - boot;
        return offset < TimeSpan.Zero || offset > MaxStartupOffset ? null : offset.TotalSeconds;
    }

    /// <summary>
    /// Processes of this session whose exe is an enabled sign-in entry (Run keys, policy Run, Startup folders), by when they started after
    /// boot, latest first. Script hosts (cmd, rundll32 ...) are left out: their name says nothing about which entry started them.
    /// <paramref name="refused"/> counts processes whose start time Windows didn't give.
    /// </summary>
    public static List<BootProgram> StartupPrograms(DateTime boot, out int refused)
    {
        var exes = new Dictionary<string, (string Name, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in StartupManager.Providers.Where(p => p is RunKeyProvider or PolicyRunProvider or StartupFolderProvider))
        {
            IEnumerable<StartupEntry> entries;
            try { entries = provider.Collect(false).ToList(); }
            catch (Exception) { continue; }
            foreach (var e in entries)
            {
                if (e.Enabled == false || e.ImagePath is not { Length: > 0 } image) continue;
                var path = Environment.ExpandEnvironmentVariables(image);
                if (Signatures.IsHost(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                exes.TryAdd(System.IO.Path.GetFileNameWithoutExtension(path), (e.Name, path));
            }
        }
        var session = Process.GetCurrentProcess().SessionId;
        var list = new List<BootProgram>();
        refused = 0;
        foreach (var group in Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            if (!exes.TryGetValue(group.Key, out var entry)) { foreach (var p in group) p.Dispose(); continue; }
            DateTime? first = null;
            foreach (var p in group)
            {
                try { if (p.SessionId == session && (first is null || p.StartTime < first)) first = p.StartTime; }
                catch (Exception) { refused++; }
                finally { p.Dispose(); }
            }
            if (first is { } start && StartOffset(start, boot) is { } seconds) list.Add(new BootProgram(entry.Name, entry.Path, seconds));
        }
        return list.OrderByDescending(p => p.SecondsAfterBoot).ToList();
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
