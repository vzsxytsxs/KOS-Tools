using System.Globalization;
using System.Xml.Linq;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.SystemInfo;

/// <summary>What the firmware exposed to Windows for CPU 0 at the last boot (Kernel-Processor-Power event 55).</summary>
/// <param name="Implementation">PerformanceImplementation: 0 = none (no P-states: EIST and Speed Shift off in the BIOS), 3 = ACPI CPPC (Speed Shift / CPPC); other values = ACPI P-states.</param>
public sealed record CpuPerfControl(int Implementation, int MaxPercent, int MinPercent, int NominalMhz, DateTime Time)
{
    /// <summary>Windows doesn't control the frequency: the power plan's processor settings (EPP, minimum/maximum state, boost mode) don't change it.</summary>
    public bool FixedByBios => Implementation == 0;
    public string Describe() => Implementation switch
    {
        0 => Txt.CpuFreqFixed(NominalMhz),
        3 => Txt.CpuFreqCppc,
        _ => Txt.CpuFreqPStates,
    };
}

public static class CpuPowerControl
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
    const string Query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=55]" +
                         " and EventData[Data[@Name='Number']='0'] and EventData[Data[@Name='Group']='0']]";

    /// <summary>The newest event 55 for CPU 0; null when there is none or the log can't be read.</summary>
    public static CpuPerfControl? Read()
    {
        try { return Stability.NewestXml(Query) is { } xml ? Parse(xml) : null; }
        catch (Exception ex) { Log.Warn($"Reading the CPU power capabilities failed: {ex.Message}"); return null; }
    }

    internal static CpuPerfControl? Parse(string xml)
    {
        var e = XDocument.Parse(xml).Root!;
        var data = e.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? new();
        int? Num(string name) => int.TryParse(data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (Num("PerformanceImplementation") is not { } impl) return null;
        var time = DateTime.Parse(e.Element(Ns + "System")!.Element(Ns + "TimeCreated")!.Attribute("SystemTime")!.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime();
        return new CpuPerfControl(impl, Num("MaximumPerformancePercent") ?? 0, Num("MinimumPerformancePercent") ?? 0, Num("NominalFrequency") ?? 0, time);
    }
}
