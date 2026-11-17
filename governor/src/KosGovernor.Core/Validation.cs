namespace KosGovernor.Core;

/// <summary>Checks everything that comes from the file, the import or the interface. Messages in English (they appear in the interface).</summary>
public static class Validation
{
    public const int MinGuiMs = 100, MaxGuiMs = 5000, MinEngineMs = 250, MaxEngineMs = 60000, MinOffDelay = 0, MaxOffDelay = 600, MaxRuleDelay = 600;

    /// <summary>Program name from the game list (with or without ".exe").</summary>
    public static string? GameNameProblem(string name) =>
        GameList.Key(name) is { Length: > 0 and <= 255 } key && name.Trim() == name && key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            ? null : "Enter the game's program name, like game.exe.";

    public static IEnumerable<RuleBase> AllRules(Profile p) =>
        p.Rules.Concat(p.OnEnable.Rules).Concat(p.OnDisable.Rules).SelectMany(r => new RuleBase[] { r }.Concat(r.Services).Concat(r.Threads));

    public static IEnumerable<string> Problems(GovernorConfig c, CpuTopology topo)
    {
        if (c.Profiles.Count == 0) yield return "There must be at least one profile.";
        if (c.ActiveProfile < 0 || c.ActiveProfile >= c.Profiles.Count) yield return "The active profile doesn't exist.";
        foreach (var p in c.Profiles)
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60) yield return "Profile names must have 1 to 60 characters.";
        foreach (var dup in c.Profiles.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"There are two profiles named \"{dup.Key}\".";
        foreach (var m in c.Masks)
            if (MaskProblem(m, c.Masks, topo) is { } e) yield return e;
        if ((c.Hotkey.Modifiers & ~0xFu) != 0 || c.Hotkey.Key is < 0x08 or > 0xFE) yield return "The gamemode hotkey isn't valid.";
        if (c.General.GuiIntervalMs is < MinGuiMs or > MaxGuiMs) yield return $"The GUI refresh must be {MinGuiMs}–{MaxGuiMs} ms.";
        if (c.General.EngineIntervalMs is < MinEngineMs or > MaxEngineMs) yield return $"The tray refresh must be {MinEngineMs}–{MaxEngineMs} ms.";
        if (c.AutoGamemode.OffDelaySeconds is < MinOffDelay or > MaxOffDelay) yield return $"The automatic gamemode delay must be {MinOffDelay}–{MaxOffDelay} s.";
        if (!Enum.IsDefined(c.Timer.Mode) || c.Timer.Resolution is < TimerResolution.Finest or > TimerResolution.Coarsest)
            yield return "The timer resolution must be 0.5–15.625 ms.";
        foreach (var g in c.AutoGamemode.Games)
            if (GameNameProblem(g.Name) is { } e) yield return $"Game list: {e}";
        foreach (var dup in c.AutoGamemode.Games.GroupBy(g => GameList.Key(g.Name), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Game list: {dup.Key} appears twice.";
        foreach (var p in c.Profiles)
        {
            foreach (var list in new[] { p.Rules, p.OnEnable.Rules, p.OnDisable.Rules })
            {
                foreach (var r in list)
                    if (RuleProblem(r, list, c.Masks, topo) is { } e) yield return $"{p.Name}: {r.Exe}: {e}";
            }
            if (p.System.PowerPlan && p.System.PowerPlanId is null) yield return $"{p.Name}: pick the power plan for gamemode.";
            if (p.System.GameCores && GameCoresProblem(p.System.GameCoresSpec, c.Masks, topo) is { } cores) yield return $"{p.Name}: {cores}";
            if (p.System.HeavyThreads is < 1 or > 4) yield return $"{p.Name}: the thread director gives 1–4 threads a core of their own.";
            if (p.System.CleanMemoryDelaySeconds is < 0 or > MaxRuleDelay) yield return $"{p.Name}: the memory clean delay must be 0–{MaxRuleDelay} s.";
            if (p.System.PurgeStandbyMb is < 64 or > 1 << 20 || p.System.PurgeFreeMb is < 64 or > 1 << 20) yield return $"{p.Name}: the standby list limits must be 64 MB or more.";
            if (p.System.RtssFrameLimit is < 0 or > 0 and < 10 or > 1000) yield return $"{p.Name}: the RTSS frame limit must be 10–1000 FPS, or 0 to keep RTSS's own.";
            if (!Enum.IsDefined(p.System.RtssLimiterMode)) yield return $"{p.Name}: unknown RTSS limiter mode.";
            if ((p.System.RtssOsdItems & ~RtssOsdItems.All) != 0) yield return $"{p.Name}: unknown RTSS on-screen display item.";
            foreach (var s in p.OnEnable.Start.Concat(p.OnDisable.Start))
                if (StartProblem(s) is { } e) yield return $"{p.Name}: {e}";
        }
    }

    public static string? MaskProblem(CpuMask m, IEnumerable<CpuMask> all, CpuTopology topo)
    {
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 40) return "Mask names must have 1 to 40 characters.";
        if (all.Any(o => !ReferenceEquals(o, m) && o.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase))) return $"There are two masks named \"{m.Name}\".";
        return CpuSpec.ParseList(m.Cpus, topo, out var error) is { } mask && mask != 0 ? null : $"Mask \"{m.Name}\": {error ?? "pick at least one processor."}";
    }

    public static string? ExeProblem(string exe) =>
        exe.Length is 0 or > 260 || exe.Trim() != exe || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Enter the program's file name, like game.exe." : null;

    /// <summary>Null when the rule (with its service and thread rules) is valid; <paramref name="siblings"/> checks for a duplicate name.</summary>
    public static string? RuleProblem(ProcessRule r, IEnumerable<ProcessRule> siblings, IReadOnlyList<CpuMask> masks, CpuTopology topo)
    {
        if (ExeProblem(r.Exe) is { } exe) return exe;
        if (siblings.Any(o => !ReferenceEquals(o, r) && o.Exe.Equals(r.Exe, StringComparison.OrdinalIgnoreCase))) return $"There's already a rule for {r.Exe}.";
        if (CommonProblem(r, masks, topo, allowNone: false) is { } common) return common;
        if (r.IncludeChildren && r.Action != RuleAction.Apply) return "\"Also the programs it opens\" needs the \"Apply settings\" action.";
        if (r.DelaySeconds is < 0 or > MaxRuleDelay) return $"The delay must be 0–{MaxRuleDelay} s.";
        if (r.CpuLimit is { } l && (l.Percent is < 1 or > 100 || l.OverSeconds is < 1 or > MaxRuleDelay || l.Cores < 1 || l.Cores >= topo.Cpus.Count || l.ForSeconds is < 1 or > 3600))
            return $"The CPU limiter needs 1–100 %, 1–{MaxRuleDelay} s over it, 1–{topo.Cpus.Count - 1} processor(s) and 1–3600 s.";
        if (r.DisplayMode is { } d && (d.Width is < 320 or > 16384 || d.Height is < 200 or > 16384 || d.RefreshHz is < 1 or > 1000))
            return "The screen mode isn't valid.";
        if (r.HasExtras && r.Action != RuleAction.Apply) return "Power plan, CPU limiter and screen mode need the \"Apply settings\" action.";
        foreach (var s in r.Services)
        {
            if (s.Service.Length is 0 or > 256) return "Service rules need the service name.";
            if (s.Action != RuleAction.Apply) return "Service rules can only apply settings.";
            if (r.Services.Count(o => o.Service.Equals(s.Service, StringComparison.OrdinalIgnoreCase)) > 1) return $"There are two rules for the service {s.Service}.";
            if (CommonProblem(s, masks, topo, allowNone: false) is { } e) return $"{s.Service}: {e}";
        }
        foreach (var t in r.Threads)
        {
            if (string.IsNullOrWhiteSpace(t.Description) && string.IsNullOrWhiteSpace(t.Module)) return "Thread rules need a description or a module.";
            if (t.IdealCpu is { } cpu && topo.Cpus.All(c => c.Index != cpu)) return $"Processor {cpu} doesn't exist.";
            if (CommonProblem(t, masks, topo, allowNone: true) is { } e) return $"Thread {t.Description ?? t.Module}: {e}";
        }
        return null;
    }

    static string? CommonProblem(RuleBase r, IReadOnlyList<CpuMask> masks, CpuTopology topo, bool allowNone)
    {
        // An empty rule is valid (shows "—"): it serves as parent for service and thread rules.
        if (!Enum.IsDefined(r.Action)) return "Unknown action.";
        if (r is ProcessLevelRule { GpuPriority: { } gpu } && !Enum.IsDefined(gpu)) return "Unknown GPU priority.";
        if ((Spec(r.Affinity, none: false) ?? Spec(r.CpuSets, allowNone)) is { } e) return e;
        return null;

        string? Spec(string? spec, bool none)
        {
            if (spec is null) return null;
            if (spec == CpuSpec.None && !none) return "\"None\" only works for thread CPU sets.";
            return CpuSpec.Resolve(spec, topo, masks, out var error) is null ? error : null;
        }
    }

    /// <summary>The cores games get in gamemode (<see cref="GamemodeSystemSettings.GameCoresSpec"/>).</summary>
    public static string? GameCoresProblem(string? spec, IReadOnlyList<CpuMask> masks, CpuTopology topo) =>
        spec is null or CpuSpec.None ? "pick the cores for games in gamemode."
        : CpuSpec.Resolve(spec, topo, masks, out var error) is null ? $"cores for games: {error}" : null;

    public static string? StartProblem(StartEntry s) =>
        !Path.IsPathFullyQualified(s.Path) || !s.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? $"\"{s.Path}\" must be the full path of an .exe."
        : s.Arguments is { Length: > 1000 } ? "The arguments are too long." : null;
}
