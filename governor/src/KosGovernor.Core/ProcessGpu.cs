using System.Text.RegularExpressions;
using KosToolbox.Core.SystemInfo;

namespace KosGovernor.Core;

/// <summary>
/// GPU use and dedicated video memory of each process, from the counters Task Manager uses: "GPU Engine" (one instance per engine, named
/// pid_…_engtype_3D) and "GPU Process Memory". A process's GPU use is its busiest engine type, as in Task Manager.
/// </summary>
public sealed partial class ProcessGpu : IDisposable
{
    readonly Pdh _pdh = new();
    readonly IntPtr _engine, _memory;

    public ProcessGpu()
    {
        _engine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _memory = _pdh.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _pdh.Collect(); // utilization is a rate: the first values come with the next collect
    }

    public (Dictionary<int, double> Percent, Dictionary<int, long> Vram) Read()
    {
        _pdh.Collect();
        var byType = new Dictionary<(int Pid, string Type), double>();
        if (_engine != IntPtr.Zero)
            foreach (var (name, value) in _pdh.Values(_engine))
                if (EngineName().Match(name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pid))
                    byType[(pid, m.Groups[2].Value)] = byType.GetValueOrDefault((pid, m.Groups[2].Value)) + value;
        var percent = byType.GroupBy(e => e.Key.Pid).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(e => e.Value)));
        var vram = new Dictionary<int, long>();
        if (_memory != IntPtr.Zero)
            foreach (var (name, value) in _pdh.Values(_memory))
                if (MemoryName().Match(name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pid))
                    vram[pid] = vram.GetValueOrDefault(pid) + (long)value;
        return (percent, vram);
    }

    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$", RegexOptions.IgnoreCase)] internal static partial Regex EngineName();
    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase)] internal static partial Regex MemoryName();

    public void Dispose() => _pdh.Dispose();
}
