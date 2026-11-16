using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using KosGovernor.Core;
using KosGovernor.Views;

namespace KosGovernor.ViewModels;

/// <summary>A button of the details panel (a priority, a processor, a mode); selected = what the process reports.</summary>
public sealed class PanelOption(string label, ICommand command, string? tooltip = null) : ObservableObject
{
    bool _selected;
    public string Label => label;
    public string? Tooltip => tooltip;
    public ICommand Command => command;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// The details panel under the process list: what the selected processes report now, buttons that apply a priority or processors to them
/// (through <see cref="MainViewModel.ApplyNow"/>) and the switch that keeps it in a rule. Several selected = applies to all, like the menu.
/// </summary>
public sealed class ProcessPanelViewModel : ObservableObject
{
    readonly MainViewModel _main;
    IReadOnlyList<ProcessRow> _rows = Array.Empty<ProcessRow>();
    List<ProcessRow> _owners = new();
    bool _open, _cpuSets = true;
    string _description = "";

    public ProcessPanelViewModel(MainViewModel main)
    {
        _main = main;
        CpuPriorities = Enum.GetValues<CpuPriority>().Select(v => new PanelOption(Labels.Long(v), new RelayCommand(() => SetPriority(v)))).ToList();
        IoPriorities = Enum.GetValues<IoPriority>().Select(v => new PanelOption(Labels.Long(v), new RelayCommand(() => Change(r => r.Io = v)))).ToList();
        MemoryPriorities = Enum.GetValues<MemoryPriority>()
            .Select(v => new PanelOption(((int)v + 1).ToString(), new RelayCommand(() => Change(r => r.Memory = v)), Labels.Long(v))).ToList();
        Modes = new[] { new PanelOption("Affinity", new RelayCommand(() => SetMode(false))), new PanelOption("CPU sets", new RelayCommand(() => SetMode(true))) };
        Cpus = main.Topology.Cpus.Select(c => new PanelOption(c.Index.ToString(), new RelayCommand(() => ToggleCpu(c.Index)), $"CPU {c.Index}")).ToList();
        ToggleRuleCommand = new RelayCommand(ToggleRule);
        ToggleGamemodeOnlyCommand = new RelayCommand(ToggleGamemodeOnly);
        SeeRulesCommand = new RelayCommand(() => main.ShowSection(GamemodeOnly ? "Gamemode" : "Rules"));
        OpenLocationCommand = new RelayCommand(() => { if (_rows.Count > 0) main.OpenLocation(_rows[0]); });
        main.Config.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ConfigViewModel.RuleRows)) RaiseRule(); };
    }

    public bool IsOpen { get => _open; private set => Set(ref _open, value); }
    /// <summary>The program of the first selected row (a service row's own process): its icon, threads and file.</summary>
    public ProcessRow? Owner => _owners.FirstOrDefault();
    bool Single => _owners.Count == 1;

    public IReadOnlyList<PanelOption> CpuPriorities { get; }
    public IReadOnlyList<PanelOption> IoPriorities { get; }
    public IReadOnlyList<PanelOption> MemoryPriorities { get; }
    public IReadOnlyList<PanelOption> Modes { get; }
    public IReadOnlyList<PanelOption> Cpus { get; }
    public ICommand ToggleRuleCommand { get; }
    public ICommand ToggleGamemodeOnlyCommand { get; }
    public ICommand SeeRulesCommand { get; }
    public ICommand OpenLocationCommand { get; }

    /// <summary>Opens the panel on these rows (the list's selection); none closes it.</summary>
    public void Show(IList<ProcessRow> rows)
    {
        foreach (var o in _owners) o.PropertyChanged -= OnRowChanged;
        _rows = rows.ToList();
        _owners = rows.Select(r => r.Parent ?? r).Distinct().ToList();
        foreach (var o in _owners) o.PropertyChanged += OnRowChanged;
        if (_owners.Count == 0) { IsOpen = false; return; }
        _description = Single && _rows[0].Service is { } service ? $"{service.DisplayName} (service)" : Single ? Description(Owner!.ImagePath) : "";
        var d = Owner!.Details;
        _cpuSets = d.Affinity is not { } affinity || affinity == _main.Topology.AllMask; // affinity mode when that's what limits the process
        IsOpen = true;
        foreach (var n in new[] { nameof(Owner), nameof(Icon), nameof(Title), nameof(Subtitle), nameof(ThreadsText), nameof(CanOpenThreads), nameof(Path), nameof(PathText) }) Raise(n);
        RaiseNow();
        RaiseValues();
        RaiseRule();
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProcessRow.Details): RaiseValues(); break;
            case nameof(ProcessRow.CpuUseText) or nameof(ProcessRow.MemUseText) or nameof(ProcessRow.GpuText) or nameof(ProcessRow.ThreadsText): RaiseNow(); break;
            case nameof(ProcessRow.Icon): Raise(nameof(Icon)); break;
        }
    }

    static string Description(string? path)
    {
        if (path is null) return "";
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
        catch { return ""; } // a file that went away or can't be read: no description
    }

    // ---------- header and Now ----------

    public ImageSource? Icon => Single ? Owner?.Icon : null;
    public string Title => Single ? Owner!.Name : $"{_owners.Count} processes";
    public string Subtitle => Single
        ? string.Join(" · ", new[] { _description, $"PID {Owner!.Pid}" }.Where(s => s.Length > 0))
        : string.Join(", ", _owners.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase));
    public bool CanOpenThreads => Single;
    public string ThreadsText => $"Threads ({Owner?.ThreadCount})";
    public string? Path => Single ? Owner!.ImagePath : null;
    public string PathText => Path ?? (Single ? "The program's path can't be read." : "");

    public string CpuNow => $"{_owners.Sum(o => o.CpuUse):0.0}%";
    public string MemoryNow => Size(_owners.Sum(o => o.MemUse));
    public string GpuNow => $"{_owners.Sum(o => o.Gpu):0.0}%";
    public string ThreadsNow => _owners.Sum(o => o.ThreadCount).ToString("N0");

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / 1048576.0:N0} MB";

    void RaiseNow()
    {
        foreach (var n in new[] { nameof(CpuNow), nameof(MemoryNow), nameof(GpuNow), nameof(ThreadsNow), nameof(ThreadsText) }) Raise(n);
    }

    // ---------- priority and processors (read back from the process) ----------

    /// <summary>The processors in the mode on screen, as the process reports them (null = can't be read).</summary>
    ulong? CurrentMask
    {
        get
        {
            var d = Owner?.Details;
            if (d is null) return null;
            return _cpuSets ? d.CpuSets is 0 ? _main.Topology.AllMask : d.CpuSets : d.Affinity;
        }
    }

    public string ProcessorsText => Owner?.Details is { } d
        ? _cpuSets ? $"CPU sets: {Labels.CpuSets(d.CpuSets, _main.Topology.AllMask)}" : $"Affinity: {Labels.Mask(d.Affinity, _main.Topology.AllMask)}"
        : "";

    void RaiseValues()
    {
        // Selected only when every selected process reports that value.
        bool All<T>(Func<ProcessDetails, T?> read, T value) where T : struct => _owners.Count > 0 && _owners.All(o => Equals(read(o.Details), value));
        var cpu = Enum.GetValues<CpuPriority>();
        for (var i = 0; i < cpu.Length; i++) CpuPriorities[i].IsSelected = All(d => d.Priority, cpu[i]);
        var io = Enum.GetValues<IoPriority>();
        for (var i = 0; i < io.Length; i++) IoPriorities[i].IsSelected = All(d => d.Io, io[i]);
        var memory = Enum.GetValues<MemoryPriority>();
        for (var i = 0; i < memory.Length; i++) MemoryPriorities[i].IsSelected = All(d => d.Memory, memory[i]);
        Modes[0].IsSelected = !_cpuSets;
        Modes[1].IsSelected = _cpuSets;
        var mask = CurrentMask;
        foreach (var (option, cpuIndex) in Cpus.Zip(_main.Topology.Cpus.Select(c => c.Index))) option.IsSelected = mask is { } m && (m >> cpuIndex & 1) != 0;
        Raise(nameof(ProcessorsText));
    }

    void SetMode(bool cpuSets)
    {
        _cpuSets = cpuSets;
        RaiseValues();
    }

    void SetPriority(CpuPriority value)
    {
        if (value == CpuPriority.Realtime && !Menus.ConfirmRealtime()) { RaiseValues(); return; }
        Change(r => r.Priority = value);
    }

    void ToggleCpu(int index)
    {
        var mask = (CurrentMask ?? _main.Topology.AllMask) ^ 1UL << index;
        if (mask == 0)
        {
            _main.Report("At least one processor stays on.");
            RaiseValues();
            return;
        }
        var spec = mask == _main.Topology.AllMask ? CpuSpec.All : CpuSpec.FormatList(mask);
        if (_cpuSets) Change(r => r.CpuSets = spec);
        else Change(r => r.Affinity = spec);
    }

    /// <summary>Applies now; with the rule on, also saves it in the rule (first, so a failed save doesn't leave a change that looks kept).</summary>
    void Change(Action<ProcessLevelRule> set)
    {
        if (_owners.Count == 0) return;
        if (RuleOn && !_main.Always(_rows, set, GamemodeOnly)) { RaiseValues(); return; }
        _main.ApplyNow(_rows, set);
        RaiseValues(); // the buttons go back to what the process reports until the refresh reads it again
    }

    // ---------- rule ----------

    /// <summary>The rule each selected row changes: the service's on a service row, otherwise the program's (in Rules, or the gamemode rules).</summary>
    (ProcessLevelRule? Always, ProcessLevelRule? Gamemode) RulesOf(ProcessRow row)
    {
        var profile = _main.Config.Config.Active;
        var exe = (row.Parent ?? row).Name;
        var always = profile.Rules.FirstOrDefault(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (row.Service is { } s) return (always?.Services.FirstOrDefault(x => x.Service.Equals(s.Name, StringComparison.OrdinalIgnoreCase)), null);
        return (always, profile.OnEnable.Rules.FirstOrDefault(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)));
    }

    public string RuleLabel => Single ? $"Apply every time {(_rows[0].Service is { } s ? s.DisplayName : Owner!.Name)} starts" : "Apply every time they start";
    public bool RuleOn => _rows.Count > 0 && _rows.All(r => RulesOf(r) is { Always: not null } or { Gamemode: not null });
    public bool GamemodeOnly => _rows.Count > 0 && _rows.All(r => RulesOf(r) is { Always: null, Gamemode: not null });
    /// <summary>Service rules live inside the program's rule and have no gamemode version.</summary>
    public bool CanGamemode => RuleOn && _rows.All(r => r.Service is null);
    public string RuleNote => RuleOn
        ? $"In the {_main.Config.ProfileName} profile. Applied every time it starts{(GamemodeOnly ? " while gamemode is on" : "")}."
        : "Changes here last until the process closes.";

    void RaiseRule()
    {
        foreach (var n in new[] { nameof(RuleLabel), nameof(RuleOn), nameof(GamemodeOnly), nameof(CanGamemode), nameof(RuleNote) }) Raise(n);
    }

    void ToggleRule()
    {
        if (RuleOn) RemoveRule();
        else SaveRule();
        RaiseRule(); // the switch shows the saved state, also when it was cancelled or failed
    }

    /// <summary>Saves what each process reports now (priorities; processors only when limited) as its rule.</summary>
    void SaveRule()
    {
        var all = _main.Topology.AllMask;
        foreach (var row in _rows)
        {
            var d = (row.Parent ?? row).Details;
            _main.Always(new[] { row }, r =>
            {
                (r.Priority, r.Io, r.Memory) = (d.Priority, d.Io, d.Memory);
                if (d.Affinity is { } a && a != all) r.Affinity = CpuSpec.FormatList(a);
                if (d.CpuSets is { } c && c != 0 && c != all) r.CpuSets = CpuSpec.FormatList(c);
            });
        }
    }

    void RemoveRule()
    {
        // Confirmed only when the rule holds more than this panel shows (it would be lost without a trace).
        var hidden = _rows.Select(RulesOf).SelectMany(x => new[] { x.Always, x.Gamemode }).OfType<ProcessLevelRule>().Where(HasMore).ToList();
        if (hidden.Count > 0 && !Dialog.Confirm("Remove the rule",
                "The rule also has settings this panel doesn't show (in Rules). Removing it removes them too.", "Remove")) return;
        var names = string.Join(", ", _rows.Select(r => r.Service?.DisplayName ?? (r.Parent ?? r).Name).Distinct());
        _main.Config.Mutate(c =>
        {
            foreach (var row in _rows)
            {
                var exe = (row.Parent ?? row).Name;
                bool Same(ProcessRule r) => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase);
                if (row.Service is { } s)
                    foreach (var rule in c.Active.Rules.Where(Same)) rule.Services.RemoveAll(x => x.Service.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                else
                {
                    c.Active.Rules.RemoveAll(Same);
                    c.Active.OnEnable.Rules.RemoveAll(Same);
                }
            }
        }, $"Rule removed for {names}.");
    }

    static bool HasMore(ProcessLevelRule r) => r.Action != RuleAction.Apply || r.Boost is not null || r.Efficiency is not null
        || r is ProcessRule { IncludeChildren: true } or ProcessRule { DelaySeconds: > 0 } or ProcessRule { HasExtras: true }
        || r is ProcessRule p && (p.Services.Count > 0 || p.Threads.Count > 0);

    /// <summary>Moves the rules between Rules and the gamemode (On Enable) rules; refused where the other list already has one for that program.</summary>
    void ToggleGamemodeOnly()
    {
        if (CanGamemode)
        {
            var toGamemode = !GamemodeOnly;
            var exes = _owners.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _main.Config.Mutate(c =>
            {
                var (from, to) = toGamemode ? (c.Active.Rules, c.Active.OnEnable.Rules) : (c.Active.OnEnable.Rules, c.Active.Rules);
                foreach (var exe in exes)
                {
                    bool Same(ProcessRule r) => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase);
                    if (to.Any(Same))
                        throw new InvalidOperationException(toGamemode
                            ? $"{exe} already has a gamemode rule: change or remove it in Gamemode."
                            : $"{exe} already has a rule in Rules: change or remove it there.");
                    var rule = from.FirstOrDefault(Same);
                    if (rule is null) continue;
                    from.Remove(rule);
                    to.Add(rule);
                }
            }, toGamemode ? $"{string.Join(", ", exes)}: applied only while gamemode is on." : $"{string.Join(", ", exes)}: applied every time it starts.");
        }
        RaiseRule();
    }
}
