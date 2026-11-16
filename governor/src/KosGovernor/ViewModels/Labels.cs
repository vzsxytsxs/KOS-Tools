using System.Text;
using KosGovernor.Core;

namespace KosGovernor.ViewModels;

/// <summary>Texts for the values (lists, menus and rule summary).</summary>
public static class Labels
{
    /// <summary>"BelowNormal" → "Below normal", "TimeCritical" → "Time critical".</summary>
    public static string Long(Enum value)
    {
        var s = value.ToString();
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append(' ').Append(char.ToLowerInvariant(s[i]));
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>GPU priorities from highest to lowest (their numbers aren't in that order).</summary>
    public static readonly GpuPriority[] GpuPriorities =
        { GpuPriority.Realtime, GpuPriority.High, GpuPriority.AboveNormal, GpuPriority.Normal, GpuPriority.BelowNormal, GpuPriority.Idle };

    /// <summary>Narrow column: "AboveNorm", "BelowNorm".</summary>
    public static string Short(Enum? value) => value?.ToString() switch { null => "?", "Normal" => "Normal", var s => s.Replace("Normal", "Norm") };

    public static string ThreadPriority(int? value) => value switch
    {
        null => "?",
        -15 => "Idle",
        -2 => "Lowest",
        -1 => "Below Normal",
        0 => "Normal",
        1 => "Above Normal",
        2 => "Highest",
        15 => "Time Critical",
        _ => value.Value.ToString(),
    };

    public static string Mask(ulong? mask, ulong all) => mask is null ? "?" : mask == all ? "All" : CpuSpec.FormatList(mask.Value);

    /// <summary>CPU sets: 0 = none set, which is the same as all.</summary>
    public static string CpuSets(ulong? mask, ulong all) => mask is null ? "?" : mask == 0 || mask == all ? "All" : CpuSpec.FormatList(mask.Value);

    public static string Check(bool? value) => value switch { null => "?", true => "✓", false => "" };

    public static string Summary(RuleBase r)
    {
        var after = r is ProcessRule { DelaySeconds: > 0 and var s } ? $" (after {s} s)" : "";
        if (r.Action == RuleAction.Terminate) return "Terminate" + after;
        if (r.Action == RuleAction.Suspend) return "Suspend" + after;
        var parts = new List<string>();
        switch (r)
        {
            case ProcessLevelRule p:
                if (p.Priority is { } cpu) parts.Add($"CPU {Long(cpu)}");
                break;
            case ThreadRule t:
                if (t.Priority is { } tp) parts.Add($"Priority {Long(tp)}");
                break;
        }
        if (r.Io is { } io) parts.Add($"I/O {Long(io)}");
        if (r.Memory is { } m) parts.Add($"Memory {Long(m)}");
        if (r.Boost is { } b) parts.Add($"Boost {(b ? "on" : "off")}");
        if (r is ProcessLevelRule { Efficiency: { } e }) parts.Add($"Efficiency {Long(e).ToLowerInvariant()}");
        if (r is ProcessLevelRule { GpuPriority: { } gpu }) parts.Add($"GPU {Long(gpu)}");
        if (r is ThreadRule { IdealCpu: { } ideal }) parts.Add($"Ideal CPU {ideal}");
        if (r.Affinity is { } a) parts.Add($"Affinity {CpuSpec.Label(a)}");
        if (r.CpuSets is { } c) parts.Add($"CPU sets {CpuSpec.Label(c)}");
        if (r is ProcessRule { IncludeChildren: true }) parts.Add("Also the programs it opens");
        if (r is ProcessRule { PowerPlan: { } plan }) parts.Add($"Plan {PlanName(plan)}");
        if (r is ProcessRule { CpuLimit: { } l }) parts.Add($"Limit above {l.Percent} %: −{l.Cores} CPU for {l.ForSeconds} s");
        if (r is ProcessRule { DisplayMode: { } d }) parts.Add($"Screen {d}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts) + after;
    }

    static Dictionary<Guid, string>? _plans;

    static string PlanName(Guid id)
    {
        try { _plans ??= KosToolbox.Core.Power.PowerPlans.List().ToDictionary(p => p.Id, p => p.Name); }
        catch { _plans = new(); }
        return _plans.GetValueOrDefault(id, id.ToString());
    }

    public static string Hotkey(Hotkey h, Func<uint, string> keyName)
    {
        var parts = new List<string>();
        if ((h.Modifiers & Core.Hotkey.Ctrl) != 0) parts.Add("Ctrl");
        if ((h.Modifiers & Core.Hotkey.Alt) != 0) parts.Add("Alt");
        if ((h.Modifiers & Core.Hotkey.Shift) != 0) parts.Add("Shift");
        if ((h.Modifiers & Core.Hotkey.Win) != 0) parts.Add("Win");
        parts.Add(keyName(h.Key));
        return string.Join("+", parts);
    }
}
