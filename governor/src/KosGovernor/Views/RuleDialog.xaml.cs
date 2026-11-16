using System.Windows;
using System.Windows.Controls;
using KosGovernor.Core;
using KosGovernor.ViewModels;

namespace KosGovernor.Views;

/// <summary>List item with text and value (null = "(no change)").</summary>
public sealed record Opt(string Label, object? Value)
{
    public override string ToString() => Label;
}

/// <summary>Editor for process, service (+S) and thread (+T) rules.</summary>
public partial class RuleDialog : Window
{
    static readonly object Custom = new();
    const string NoChange = "(no change)";
    readonly CpuTopology _topo = CpuTopology.Current;
    GovernorConfig _config = new();
    Func<RuleDialog, string?> _validate = _ => null;
    readonly Dictionary<ComboBox, Opt?> _lastCpu = new();

    RuleDialog() => InitializeComponent();

    public static ProcessRule? EditProcess(ProcessRule? rule, IEnumerable<string> names, GovernorConfig config, Func<ProcessRule, string?> validate)
    {
        var d = Create(rule is null ? "Add Process Rule" : $"Edit Rule: {rule.Exe}", "Process name:", names, config);
        d.Collapse(d.ModuleLabel, d.ModuleBox, d.IdealLabel, d.IdealBox);
        d.FillActions(d.ActionBox, Actions, rule?.Action ?? RuleAction.Apply);
        d.Fill(d.PriorityBox, Enum.GetValues<CpuPriority>().Reverse(), rule?.Priority);
        d.Fill(d.EfficiencyBox, Enum.GetValues<Efficiency>(), rule?.Efficiency);
        d.Fill(d.GpuBox, Labels.GpuPriorities, rule?.GpuPriority);
        d.FillCommon(rule, thread: false);
        d.FillExtras(rule);
        d.ChildrenBox.IsChecked = rule?.IncludeChildren == true;
        var delay = rule?.DelaySeconds ?? 0;
        d.DelayBox.ItemsSource = Delays.Union(new[] { delay }).Order().Select(s => new Opt(s == 0 ? "None" : s % 60 == 0 ? $"{s / 60} min" : $"{s} s", s)).ToList();
        d.Select(d.DelayBox, delay);
        d.NameBox.Text = rule?.Exe ?? "";
        d._validate = x => validate(x.ReadProcess());
        return d.Run() ? d.ReadProcess() : null;
    }

    public static ServiceRule? EditService(ServiceRule? rule, string exe, IEnumerable<string> services, GovernorConfig config, Func<ServiceRule, string?> validate)
    {
        var d = Create(rule is null ? $"Add Service Rule ({exe})" : $"Edit Service Rule: {rule.Service}", "Service name:", services, config);
        d.Collapse(d.ModuleLabel, d.ModuleBox, d.IdealLabel, d.IdealBox, d.ActionLabel, d.ActionBox, d.ChildrenBox, d.DelayLabel, d.DelayBox);
        d.CollapseExtras();
        d.FillActions(d.ActionBox, Actions.Take(1), RuleAction.Apply);
        d.Fill(d.PriorityBox, Enum.GetValues<CpuPriority>().Reverse(), rule?.Priority);
        d.Fill(d.EfficiencyBox, Enum.GetValues<Efficiency>(), rule?.Efficiency);
        d.Fill(d.GpuBox, Labels.GpuPriorities, rule?.GpuPriority);
        d.FillCommon(rule, thread: false);
        d.NameBox.Text = rule?.Service ?? "";
        d._validate = x => validate(x.ReadService());
        return d.Run() ? d.ReadService() : null;
    }

    public static ThreadRule? EditThread(ThreadRule? rule, string exe, IEnumerable<string> descriptions, IEnumerable<string> modules, GovernorConfig config)
    {
        var d = Create(rule is null ? $"Add Thread Rule ({exe})" : $"Edit Thread Rule ({exe})", "Description:", descriptions, config);
        d.Collapse(d.EfficiencyLabel, d.EfficiencyBox, d.GpuLabel, d.GpuBox, d.ChildrenBox, d.DelayLabel, d.DelayBox);
        d.CollapseExtras();
        d.PriorityLabel.Text = "Priority:";
        d.ModuleBox.ItemsSource = modules.ToList();
        d.FillActions(d.ActionBox, Actions, rule?.Action ?? RuleAction.Apply);
        d.PriorityBox.ItemsSource = new[] { new Opt(NoChange, null) }
            .Concat(Enum.GetValues<ThreadPriority>().Reverse().Select(p => new Opt(Labels.Long(p), p))).ToList();
        d.Select(d.PriorityBox, rule?.Priority);
        d.IdealBox.ItemsSource = new[] { new Opt(NoChange, null) }.Concat(d._topo.Cpus.Select(c => new Opt($"CPU {c.Index}", c.Index))).ToList();
        d.Select(d.IdealBox, rule?.IdealCpu);
        d.FillCommon(rule, thread: true);
        d.NameBox.Text = rule?.Description ?? "";
        d.ModuleBox.Text = rule?.Module ?? "";
        d._validate = x => Validation.RuleProblem(new ProcessRule { Exe = exe, Threads = { x.ReadThread() } }, Array.Empty<ProcessRule>(), x._config.Masks, x._topo);
        return d.Run() ? d.ReadThread() : null;
    }

    static readonly int[] Delays = { 0, 5, 10, 15, 30, 60, 120, 300 };

    static readonly (string, RuleAction)[] Actions = { ("Apply settings", RuleAction.Apply), ("Suspend", RuleAction.Suspend), ("Terminate", RuleAction.Terminate) };

    static RuleDialog Create(string title, string nameLabel, IEnumerable<string> names, GovernorConfig config)
    {
        var d = new RuleDialog { Title = title, _config = config };
        d.Heading.Text = title;
        d.NameLabel.Text = nameLabel;
        d.NameBox.ItemsSource = names.ToList();
        DialogSetup.Prepare(d, d.Root);
        d.Loaded += (_, _) => d.NameBox.Focus();
        return d;
    }

    bool Run() => ShowDialog() == true;

    void Collapse(params UIElement[] elements)
    {
        foreach (var e in elements) e.Visibility = Visibility.Collapsed;
    }

    void Fill<T>(ComboBox box, IEnumerable<T> values, T? current, bool noChange = true) where T : struct, Enum
    {
        box.ItemsSource = (noChange ? new[] { new Opt(NoChange, null) } : Array.Empty<Opt>()).Concat(values.Select(v => new Opt(Labels.Long(v), v))).ToList();
        Select(box, current);
    }

    void FillActions(ComboBox box, IEnumerable<(string Label, RuleAction Value)> values, RuleAction current)
    {
        box.ItemsSource = values.Select(v => new Opt(v.Label, v.Value)).ToList();
        Select(box, current);
    }

    void Select(ComboBox box, object? value) =>
        box.SelectedItem = ((IEnumerable<Opt>)box.ItemsSource).FirstOrDefault(o => Equals(o.Value, value)) ?? ((IEnumerable<Opt>)box.ItemsSource).First();

    void FillCommon(RuleBase? rule, bool thread)
    {
        Fill(IoBox, Enum.GetValues<IoPriority>().Reverse(), rule?.Io);
        Fill(MemoryBox, Enum.GetValues<MemoryPriority>().Reverse(), rule?.Memory);
        BoostBox.ItemsSource = new[] { new Opt(NoChange, null), new Opt("On", true), new Opt("Off", false) };
        Select(BoostBox, rule?.Boost);
        FillCpu(AffinityBox, rule?.Affinity, none: false);
        FillCpu(CpuSetsBox, rule?.CpuSets, none: thread);
        UpdateEnabled();
    }

    void FillCpu(ComboBox box, string? current, bool none)
    {
        var opts = new List<Opt> { new(NoChange, null), new("All", CpuSpec.All) };
        if (none) opts.Add(new Opt("None (use the process's)", CpuSpec.None));
        opts.AddRange(CpuSpec.Presets(_topo).Select(p => new Opt(p.Label, p.Spec)));
        opts.AddRange(_config.Masks.Select(m => new Opt($"Mask: {m.Name}", CpuSpec.MaskPrefix + m.Name)));
        if (current is not null && opts.All(o => (string?)o.Value != current)) opts.Add(new Opt(current, current));
        opts.Add(new Opt("Custom…", Custom));
        box.ItemsSource = opts;
        Select(box, current);
        _lastCpu[box] = (Opt)box.SelectedItem;
    }

    void OnCpuChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not Opt opt || !_lastCpu.ContainsKey(box)) return;
        if (opt.Value != Custom) { _lastCpu[box] = opt; return; }
        var previous = _lastCpu[box];
        var initial = previous?.Value is string spec ? CpuSpec.Resolve(spec, _topo, _config.Masks, out _) ?? 0 : 0;
        var (ok, mask, _) = CpuDialog.Pick(box == AffinityBox ? "CPU Affinity" : "CPU Sets", initial == 0 ? _topo.AllMask : initial, _config);
        if (!ok) { box.SelectedItem = previous; return; }
        var list = CpuSpec.FormatList(mask);
        var opts = (List<Opt>)box.ItemsSource;
        var item = opts.FirstOrDefault(o => (string?)o.Value == list) ?? new Opt(list, list);
        if (!opts.Contains(item)) opts.Insert(opts.Count - 1, item);
        box.Items.Refresh();
        box.SelectedItem = item;
    }

    void OnActionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabled();

    void UpdateEnabled()
    {
        var apply = ActionBox.SelectedItem is not Opt { Value: RuleAction a } || a == RuleAction.Apply;
        foreach (var box in new Control[] { PriorityBox, IoBox, MemoryBox, EfficiencyBox, GpuBox, BoostBox, IdealBox, AffinityBox, CpuSetsBox, ChildrenBox, PlanBox, LimitPercentBox, ScreenBox })
            box.IsEnabled = apply;
        LimitDetails.IsEnabled = apply && LimitPercentBox.SelectedItem is Opt { Value: int };
    }

    // ---------- power plan, CPU limiter and screen mode (process rules only) ----------

    void CollapseExtras() => Collapse(PlanLabel, PlanBox, LimitLabel, LimitPanel, ScreenLabel, ScreenBox);

    void FillExtras(ProcessRule? rule)
    {
        var plans = new List<Opt> { new(NoChange, null) };
        try { plans.AddRange(KosToolbox.Core.Power.PowerPlans.List().Select(p => new Opt(p.Name, p.Id))); }
        catch (Exception ex) { Log.Warn($"Listing power plans failed: {ex.Message}"); }
        if (rule?.PowerPlan is { } plan && plans.All(o => !Equals(o.Value, plan))) plans.Add(new Opt($"Plan {plan}", plan));
        PlanBox.ItemsSource = plans;
        Select(PlanBox, rule?.PowerPlan);

        var limit = rule?.CpuLimit;
        var percents = new[] { 10, 25, 50, 75, 90 }.Union(limit is null ? Array.Empty<int>() : new[] { limit.Percent }).Order();
        LimitPercentBox.ItemsSource = new[] { new Opt("Off", null) }.Concat(percents.Select(p => new Opt($"Above {p} % of the CPU", p))).ToList();
        Select(LimitPercentBox, limit?.Percent);
        LimitOverBox.ItemsSource = new[] { 3, 5, 10, 30, 60 }.Union(new[] { limit?.OverSeconds ?? 10 }).Order().Select(s => new Opt($"{s} s", s)).ToList();
        Select(LimitOverBox, limit?.OverSeconds ?? 10);
        var cpus = _topo.Cpus.Count;
        LimitCoresBox.ItemsSource = Enumerable.Range(1, Math.Max(1, cpus - 1)).Select(n => new Opt($"−{n} CPU", n)).ToList();
        Select(LimitCoresBox, Math.Min(limit?.Cores ?? 2, Math.Max(1, cpus - 1)));
        LimitForBox.ItemsSource = new[] { 10, 30, 60, 120, 300 }.Union(new[] { limit?.ForSeconds ?? 30 }).Order().Select(s => new Opt($"for {s} s", s)).ToList();
        Select(LimitForBox, limit?.ForSeconds ?? 30);

        var screens = new List<Opt> { new(NoChange, null) };
        try
        {
            if (KosToolbox.Core.Gpu.WindowsDisplays.List().FirstOrDefault(s => s.Primary) is { } primary)
                screens.AddRange(KosToolbox.Core.Gpu.WindowsDisplays.Modes(primary.GdiName)
                    .Select(m => new Opt($"{m.Width}×{m.Height} @ {m.Hz} Hz", $"{m.Width}x{m.Height}@{m.Hz}")));
        }
        catch (Exception ex) { Log.Warn($"Listing screen modes failed: {ex.Message}"); }
        var current = rule?.DisplayMode is { } dm ? $"{dm.Width}x{dm.Height}@{dm.RefreshHz}" : null;
        if (current is not null && screens.All(o => (string?)o.Value != current)) screens.Add(new Opt(rule!.DisplayMode!.ToString(), current));
        ScreenBox.ItemsSource = screens;
        Select(ScreenBox, current);
        UpdateEnabled();
    }

    void OnLimitChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabled();

    void ReadExtras(ProcessRule r)
    {
        r.PowerPlan = Value<Guid>(PlanBox);
        r.CpuLimit = Value<int>(LimitPercentBox) is { } percent
            ? new CpuLimit { Percent = percent, OverSeconds = LimitOverBox.SelectedItem is Opt { Value: int over } ? over : 10,
                Cores = LimitCoresBox.SelectedItem is Opt { Value: int cores } ? cores : 1, ForSeconds = LimitForBox.SelectedItem is Opt { Value: int f } ? f : 30 }
            : null;
        r.DisplayMode = ScreenBox.IsEnabled && ScreenBox.SelectedItem is Opt { Value: string mode } ? ParseMode(mode) : null;
    }

    /// <summary>"1280x960@144" (the refresh rate may be empty).</summary>
    internal static DisplayModeSetting? ParseMode(string text)
    {
        var parts = text.Split('x', '@');
        return parts.Length == 3 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? new DisplayModeSetting { Width = w, Height = h, RefreshHz = int.TryParse(parts[2], out var hz) ? hz : null }
            : null;
    }

    T? Value<T>(ComboBox box) where T : struct => box.IsEnabled && box.SelectedItem is Opt { Value: T v } ? v : null;
    string? Spec(ComboBox box) => box.IsEnabled && box.SelectedItem is Opt { Value: string s } ? s : null;
    RuleAction Action => ActionBox.SelectedItem is Opt { Value: RuleAction a } ? a : RuleAction.Apply;

    void Common(RuleBase r)
    {
        r.Action = Action;
        r.Io = Value<IoPriority>(IoBox);
        r.Memory = Value<MemoryPriority>(MemoryBox);
        r.Boost = Value<bool>(BoostBox);
        r.Affinity = Spec(AffinityBox);
        r.CpuSets = Spec(CpuSetsBox);
    }

    ProcessRule ReadProcess()
    {
        var r = new ProcessRule
        {
            Exe = NameBox.Text.Trim(), Priority = Value<CpuPriority>(PriorityBox), Efficiency = Value<Efficiency>(EfficiencyBox), GpuPriority = Value<GpuPriority>(GpuBox),
            IncludeChildren = ChildrenBox.IsEnabled && ChildrenBox.IsChecked == true,
            DelaySeconds = DelayBox.SelectedItem is Opt { Value: int delay } ? delay : 0,
        };
        Common(r);
        ReadExtras(r);
        return r;
    }

    ServiceRule ReadService()
    {
        var r = new ServiceRule
        {
            Service = NameBox.Text.Trim(), Priority = Value<CpuPriority>(PriorityBox), Efficiency = Value<Efficiency>(EfficiencyBox), GpuPriority = Value<GpuPriority>(GpuBox),
        };
        Common(r);
        return r;
    }

    ThreadRule ReadThread()
    {
        var r = new ThreadRule
        {
            Description = NameBox.Text.Trim() is { Length: > 0 } d ? d : null,
            Module = ModuleBox.Text.Trim() is { Length: > 0 } m ? m : null,
            Priority = Value<ThreadPriority>(PriorityBox),
            IdealCpu = Value<int>(IdealBox),
        };
        Common(r);
        return r;
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var error = _validate(this);
        Error.Text = error ?? "";
        Error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (error is null) DialogResult = true;
    }
}
