using System.Windows;
using System.Windows.Controls;
using KosGovernor.Core;
using KosGovernor.ViewModels;

namespace KosGovernor.Views;

/// <summary>
/// Context menus of the process and thread lists. Each setting has "Apply Now" (only now, on this process) and
/// "Always" (creates/changes the rule in the active profile; the engine applies it whenever the program opens).
/// </summary>
static class Menus
{
    static MenuItem Item(string header, Action? click = null, bool danger = false, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        if (danger) item.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("ErrorBrush");
        if (click is not null) item.Click += (_, e) => { e.Handled = true; click(); };
        return item;
    }

    static MenuItem Sub(string header, IEnumerable<object> children, bool danger = false)
    {
        var item = Item(header, danger: danger);
        foreach (var c in children) item.Items.Add(c);
        return item;
    }

    /// <summary>"Header ▸ Apply Now ▸ values / Always ▸ values + Don't change". The current value (or the rule's) is checked.</summary>
    static MenuItem NowAlways<T>(string header, IEnumerable<(string Label, T Value)> options, T? current, T? ruleValue, Action<T> now, Action<T?> always) where T : struct =>
        Pair(header, options.Select(o => (o.Label, (object)o.Value)), current, ruleValue, v => now((T)v), v => always((T?)v));

    static MenuItem NowAlways(string header, IEnumerable<(string Label, string Value)> options, string? current, string? ruleValue, Action<string> now, Action<string?> always) =>
        Pair(header, options.Select(o => (o.Label, (object)o.Value)), current, ruleValue, v => now((string)v), v => always((string?)v));

    static MenuItem Pair(string header, IEnumerable<(string Label, object Value)> options, object? current, object? ruleValue, Action<object> now, Action<object?> always)
    {
        var list = options.ToList();
        var nowItems = list.Select(o => (object)Item(o.Label, () => now(o.Value), isChecked: Equals(o.Value, current)));
        var alwaysItems = list.Select(o => (object)Item(o.Label, () => always(o.Value), isChecked: Equals(o.Value, ruleValue)))
            .Append(new Separator()).Append(Item("Don't change", () => always(null), isChecked: ruleValue is null));
        return Sub(header, new object[] { Sub("Apply Now", nowItems), Sub("Always", alwaysItems) });
    }

    /// <summary>From highest to lowest (High … Very low).</summary>
    static IEnumerable<(string, T)> Values<T>() where T : struct, Enum => Enum.GetValues<T>().Reverse().Select(v => (Labels.Long(v), v));

    static readonly (string, bool)[] OnOff = { ("On", true), ("Off", false) };

    /// <summary>Processor options; "Custom…" opens the picker and returns the list ("0-3,8").</summary>
    static IEnumerable<(string, string)> CpuOptions(GovernorConfig config, bool none)
    {
        yield return ("All", CpuSpec.All);
        if (none) yield return ("None (use the process's)", CpuSpec.None);
        foreach (var preset in CpuSpec.Presets(CpuTopology.Current)) yield return preset;
        foreach (var m in config.Masks) yield return ($"Mask: {m.Name}", CpuSpec.MaskPrefix + m.Name);
        yield return ("Custom…", "");
    }

    /// <summary>Turns the "Custom…" choice (empty text) into a list through the dialog; null = cancelled.</summary>
    static string? Resolve(string spec, string title, GovernorConfig config, ulong initial)
    {
        if (spec.Length > 0) return spec;
        var (ok, mask, _) = CpuDialog.Pick(title, initial, config);
        return ok ? CpuSpec.FormatList(mask) : null;
    }

    static string? CurrentSpec(ulong? mask, ulong all, bool zeroIsAll = false) =>
        mask is null ? null : mask == all || (zeroIsAll && mask == 0) ? CpuSpec.All : CpuSpec.FormatList(mask.Value);

    // ---------- processes ----------

    public static ContextMenu ForProcesses(MainViewModel vm, IList<ProcessRow> rows, Action<ProcessRow> openThreads)
    {
        var menu = new ContextMenu();
        if (rows.Count == 0) return menu;
        var first = rows[0];
        var owner = first.Parent ?? first;
        var config = vm.Config.Config;
        var topo = vm.Topology;
        var single = rows.Select(r => r.Parent ?? r).Distinct().Count() == 1;
        var d = single ? owner.Details : new ProcessDetails();
        // The rule that "Always" changes: the service's on a service row, otherwise the program's.
        var processRule = config.Active.Rules.FirstOrDefault(r => r.Exe.Equals(owner.Name, StringComparison.OrdinalIgnoreCase));
        ProcessLevelRule? rule = first.Service is { } svc ? processRule?.Services.FirstOrDefault(s => s.Service.Equals(svc.Name, StringComparison.OrdinalIgnoreCase)) : processRule;
        if (!single) rule = null;

        void Now(Action<ProcessLevelRule> set) => vm.ApplyNow(rows, set);
        void Always(Action<ProcessLevelRule> set) => vm.Always(rows, set);

        menu.Items.Add(NowAlways("CPU Priority", Values<CpuPriority>(), d.Priority, rule?.Priority,
            v => { if (v != CpuPriority.Realtime || ConfirmRealtime()) Now(r => r.Priority = v); },
            v => { if (v != CpuPriority.Realtime || ConfirmRealtime()) Always(r => r.Priority = v); }));
        menu.Items.Add(NowAlways("I/O Priority", Values<IoPriority>(), d.Io, rule?.Io, v => Now(r => r.Io = v), v => Always(r => r.Io = v)));
        menu.Items.Add(NowAlways("Memory Priority", Values<MemoryPriority>(), d.Memory, rule?.Memory, v => Now(r => r.Memory = v), v => Always(r => r.Memory = v)));
        menu.Items.Add(NowAlways("Priority Boost", OnOff, d.Boost, rule?.Boost, v => Now(r => r.Boost = v), v => Always(r => r.Boost = v)));
        menu.Items.Add(NowAlways("Efficiency Mode", new[] { ("On", Efficiency.On), ("Off", Efficiency.Off), ("Auto", Efficiency.Auto) }, d.Efficiency, rule?.Efficiency,
            v => Now(r => r.Efficiency = v), v => Always(r => r.Efficiency = v)));
        // The current GPU priority can't be read back: nothing is checked under Apply Now.
        menu.Items.Add(NowAlways("GPU Priority", Labels.GpuPriorities.Select(v => (Labels.Long(v), v)), null, rule?.GpuPriority,
            v => { if (v != GpuPriority.Realtime || ConfirmGpuRealtime()) Now(r => r.GpuPriority = v); },
            v => { if (v != GpuPriority.Realtime || ConfirmGpuRealtime()) Always(r => r.GpuPriority = v); }));
        menu.Items.Add(CpuMenu("CPU Affinity", config, none: false, CurrentSpec(d.Affinity, topo.AllMask), rule?.Affinity, d.Affinity ?? topo.AllMask,
            s => Now(r => r.Affinity = s), s => Always(r => r.Affinity = s)));
        menu.Items.Add(CpuMenu("CPU Sets", config, none: false, CurrentSpec(d.CpuSets, topo.AllMask, zeroIsAll: true), rule?.CpuSets,
            d.CpuSets is null or 0 ? topo.AllMask : d.CpuSets.Value, s => Now(r => r.CpuSets = s), s => Always(r => r.CpuSets = s)));
        menu.Items.Add(new Separator());

        // Suspend/terminate always apply to the program (a service rule only applies adjustments).
        var owners = rows.Select(r => r.Parent ?? r).Distinct().ToList();
        var ownerRule = single ? processRule : null;
        menu.Items.Add(Sub("Suspend", new object[]
        {
            Item("Apply Now", () => vm.ApplyNow(owners, r => r.Action = RuleAction.Suspend)),
            Item("Resume", () => vm.Resume(owners)),
            new Separator(),
            Item("Always", () => vm.Always(owners, r => r.Action = r.Action == RuleAction.Suspend ? RuleAction.Apply : RuleAction.Suspend),
                isChecked: ownerRule?.Action == RuleAction.Suspend),
        }));
        if (single) menu.Items.Add(GameMenu(vm, owner.Name, config));
        if (single && !first.IsService && owner.ImagePath is { } image) menu.Items.Add(WindowsSettingsMenu(vm, image));
        menu.Items.Add(Item("Threads", () => openThreads(owner)));
        menu.Items.Add(Item("Open File Location", () => vm.OpenLocation(first)));
        menu.Items.Add(Item("Properties", () => vm.Properties(first)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Sub("Terminate", new object[]
        {
            Item("Apply Now", () => vm.ApplyNow(owners, r => r.Action = RuleAction.Terminate), danger: true),
            Item("Always", () => vm.Always(owners, r => r.Action = r.Action == RuleAction.Terminate ? RuleAction.Apply : RuleAction.Terminate),
                danger: true, isChecked: ownerRule?.Action == RuleAction.Terminate),
        }, danger: true));
        return menu;

        MenuItem CpuMenu(string header, GovernorConfig c, bool none, string? current, string? ruleValue, ulong initial, Action<string?> now, Action<string?> always) =>
            NowAlways(header, CpuOptions(c, none), current, ruleValue,
                s => { if (Resolve(s, header, c, initial) is { } spec) now(spec); },
                s => { if (s is null) always(null); else if (Resolve(s, header, c, initial) is { } spec) always(spec); });
    }

    /// <summary>Per-program Windows settings for games (read when the menu opens; each click applies and reads back).</summary>
    static MenuItem WindowsSettingsMenu(MainViewModel vm, string path)
    {
        KosToolbox.Core.Settings.GameWindowsSettings? s = null;
        try { s = KosToolbox.Core.Settings.GameSettings.Read(path); }
        catch (Exception ex) { Log.Warn($"Game settings: reading {path} failed: {ex.Message}"); }
        MenuItem Toggle(GameSetting what, bool current) => Item(char.ToUpper(GameSettingsWindow.Name(what)[0]) + GameSettingsWindow.Name(what)[1..], () =>
        {
            var (_, error) = GameSettingsWindow.Apply(path, what, !current);
            vm.Report(error ?? GameSettingsWindow.Done(path, what, !current), error is not null);
        }, isChecked: current);
        var items = new List<object>();
        if (s is not null)
        {
            items.Add(Toggle(GameSetting.Dscp, s.Dscp));
            items.Add(Toggle(GameSetting.FullscreenOptimizationsOff, s.FullscreenOptimizationsOff));
            items.Add(Toggle(GameSetting.HighPerformanceGpu, s.HighPerformanceGpu));
            items.Add(new Separator());
        }
        items.Add(Item("All programs…", () => GameSettingsWindow.ShowSingle(vm)));
        return Sub("Windows Settings", items);
    }

    /// <summary>Automatic gamemode: the program is a game, never a game, or follows the built-in list (whatever is in effect is checked).</summary>
    static MenuItem GameMenu(MainViewModel vm, string exe, GovernorConfig config) => Sub("Gamemode", GameItems(vm, exe, config));

    /// <summary>Items of the Gamemode menu (also used in the game list window).</summary>
    internal static object[] GameItems(MainViewModel vm, string exe, GovernorConfig config)
    {
        var games = new GameList(config.AutoGamemode.Games);
        var entry = games.Find(exe);
        var mine = games.IsUserEntry(exe);
        var builtIn = Item("Use the built-in list", () => vm.Config.SetGame(exe, null), isChecked: !mine);
        builtIn.IsEnabled = mine;
        return new object[]
        {
            Item("Add as a game", () => vm.Config.SetGame(exe, true), isChecked: entry is { IsBlacklisted: false }),
            Item("Never a game", () => vm.Config.SetGame(exe, false), isChecked: entry is { IsBlacklisted: true }),
            new Separator(),
            builtIn,
        };
    }

    static bool ConfirmGpuRealtime() => Dialog.Confirm("Realtime GPU priority",
        "A program at Realtime GPU priority runs ahead of the desktop compositor: a busy one can freeze the screen until it stops. Use High unless you know you need it.", "Use Realtime");

    internal static bool ConfirmRealtime() => Dialog.Confirm("Realtime priority",
        "Realtime runs ahead of the mouse, keyboard and disk drivers: a busy program can freeze Windows. Use High unless you know you need it.", "Use Realtime");

    // ---------- threads ----------

    public static ContextMenu ForThreads(ThreadsWindow window, IList<ThreadRow> rows)
    {
        var menu = new ContextMenu();
        if (rows.Count == 0) return menu;
        var config = window.Config;
        var topo = CpuTopology.Current;
        var d = rows.Count == 1 ? rows[0].Details : new ThreadDetails();
        var rule = rows.Count == 1 ? window.RuleFor(rows[0]) : null;

        void Now(Action<ThreadRule> set) => window.ApplyNow(rows, set);
        void Always(Action<ThreadRule> set) => window.Always(rows, set);

        var currentPriority = d.Priority is { } p && Array.IndexOf(ThreadControl.Priorities, p) is >= 0 and var i ? (ThreadPriority?)i : null;
        menu.Items.Add(NowAlways("Priority", Values<ThreadPriority>(), currentPriority, rule?.Priority, v => Now(r => r.Priority = v), v => Always(r => r.Priority = v)));
        menu.Items.Add(NowAlways("I/O Priority", Values<IoPriority>(), d.Io, rule?.Io, v => Now(r => r.Io = v), v => Always(r => r.Io = v)));
        menu.Items.Add(NowAlways("Memory Priority", Values<MemoryPriority>(), d.Memory, rule?.Memory, v => Now(r => r.Memory = v), v => Always(r => r.Memory = v)));
        menu.Items.Add(NowAlways("Priority Boost", OnOff, d.Boost, rule?.Boost, v => Now(r => r.Boost = v), v => Always(r => r.Boost = v)));
        menu.Items.Add(NowAlways("Ideal Processor", topo.Cpus.Select(c => ($"CPU {c.Index}", c.Index)), d.Ideal is { Group: 0 } ideal ? ideal.Number : (int?)null, rule?.IdealCpu,
            v => Now(r => r.IdealCpu = v), v => Always(r => r.IdealCpu = v)));
        menu.Items.Add(Cpu("CPU Affinity", none: false, CurrentSpec(d.Affinity, topo.AllMask), rule?.Affinity, d.Affinity ?? topo.AllMask,
            s => Now(r => r.Affinity = s), s => Always(r => r.Affinity = s)));
        menu.Items.Add(Cpu("CPU Sets", none: true, d.CpuSets is 0 ? CpuSpec.None : CurrentSpec(d.CpuSets, topo.AllMask), rule?.CpuSets,
            d.CpuSets is null or 0 ? topo.AllMask : d.CpuSets.Value, s => Now(r => r.CpuSets = s), s => Always(r => r.CpuSets = s)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Sub("Suspend", new object[]
        {
            Item("Apply Now", () => Now(r => r.Action = RuleAction.Suspend)),
            Item("Resume", () => window.Resume(rows)),
            new Separator(),
            Item("Always", () => Always(r => r.Action = r.Action == RuleAction.Suspend ? RuleAction.Apply : RuleAction.Suspend), isChecked: rule?.Action == RuleAction.Suspend),
        }));
        menu.Items.Add(Sub("Terminate", new object[]
        {
            Item("Apply Now", () => Now(r => r.Action = RuleAction.Terminate), danger: true),
            Item("Always", () => Always(r => r.Action = r.Action == RuleAction.Terminate ? RuleAction.Apply : RuleAction.Terminate), danger: true,
                isChecked: rule?.Action == RuleAction.Terminate),
        }, danger: true));
        return menu;

        MenuItem Cpu(string header, bool none, string? current, string? ruleValue, ulong initial, Action<string?> now, Action<string?> always) =>
            NowAlways(header, CpuOptions(config, none), current, ruleValue,
                s => { if (Resolve(s, header, config, initial) is { } spec) now(spec); },
                s => { if (s is null) always(null); else if (Resolve(s, header, config, initial) is { } spec) always(spec); });
    }
}
