using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Firewall;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>A rule of the config (Apps and Rules tabs): its action, on/off and removal, each saved through <see cref="KosFirewall.Change"/>.</summary>
public sealed class FirewallRuleRow : ObservableObject
{
    public FirewallRuleRow(FirewallRule rule, FirewallPageViewModel page)
    {
        Rule = rule;
        Actions = [new OptionItem("allow", Txt.FwAllow) { IsSelected = rule.Allow }, new OptionItem("block", Txt.FwBlock) { IsSelected = !rule.Allow }];
        SetActionCommand = new AsyncCommand(p => (string)p! == "allow" == rule.Allow ? Task.CompletedTask // already so: no rebuild
            : page.EditAsync(rule.Id, r => r.Allow = (string)p! == "allow", Name), _ => page.CanEdit);
        ToggleEnabledCommand = new AsyncCommand(() => page.EditAsync(rule.Id, r => r.Enabled = !r.Enabled, Name), () => page.CanEdit);
        RemoveCommand = new AsyncCommand(() => page.RemoveAsync(rule.Id, Name), () => page.CanEdit);
        var problem = FirewallPolicy.Problem(rule) ?? (rule.Path is { Length: > 0 } p && !File.Exists(p) ? Txt.FwMissingProgram : null);
        Problem = rule.Enabled && problem is not null ? Txt.FwLeftOut(problem) : null;
    }

    public FirewallRule Rule { get; }
    public string Name => FirewallPolicy.Label(Rule);
    public string Detail => Rule.Kind == FirewallRuleKind.App ? Rule.Path ?? "" : FirewallPageViewModel.Summary(Rule);
    public string Direction => FirewallPageViewModel.DirectionText(Rule.Direction);
    public bool Enabled => Rule.Enabled;
    public string? Problem { get; }
    public IReadOnlyList<OptionItem> Actions { get; }
    public ICommand SetActionCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand RemoveCommand { get; }
}

/// <summary>A service or Store app with its none / allow / block choice: one outbound rule, matched by service name or package SID.</summary>
public sealed class FirewallTargetRow : ObservableObject
{
    readonly FirewallRuleKind _kind;
    readonly string? _service, _sid;

    public FirewallTargetRow(FirewallRuleKind kind, string name, string detail, string? service, string? sid, FirewallPageViewModel page)
    {
        (_kind, _service, _sid, Name, Detail) = (kind, service, sid, name, detail);
        ChooseCommand = new AsyncCommand(p => page.ChooseAsync(this, (string)p! switch { "allow" => true, "block" => false, _ => null }), _ => page.CanEdit);
    }

    public string Name { get; }
    public string Detail { get; }
    public IReadOnlyList<OptionItem> Choices { get; } = [new("none", Txt.FwNone), new("allow", Txt.FwAllow), new("block", Txt.FwBlock)];
    public ICommand ChooseCommand { get; }

    /// <summary>A new rule for this target (the service SID is looked up when one is added).</summary>
    public FirewallRule NewRule() => new() { Kind = _kind, Name = Name, Service = _service, Sid = _sid };

    public void Show(FirewallConfig config)
    {
        var key = FirewallRules.Current(config, NewRule()) switch { true => "allow", false => "block", _ => "none" };
        foreach (var c in Choices) c.IsSelected = c.Key == key;
    }
}

/// <summary>A connection KOS Firewall dropped, from the filter engine's log; Allow and Block make an App rule for its program.</summary>
public sealed class FirewallEventRow
{
    public FirewallEventRow(WfpNetEvent e, string? path, FirewallPageViewModel page)
    {
        Time = e.Time.ToLocalTime().ToString("g");
        Path = path;
        Program = path is not null ? System.IO.Path.GetFileName(path) : e.AppDevicePath is { Length: > 0 } d ? System.IO.Path.GetFileName(d) : Txt.FwSystem;
        Detail = path ?? e.AppDevicePath ?? "";
        Remote = e.Remote is null ? "" : (e.Remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{e.Remote}]" : e.Remote.ToString()) + (e.RemotePort > 0 ? $":{e.RemotePort}" : "");
        Protocol = FirewallPageViewModel.ProtocolText(e.Protocol);
        Direction = e.Inbound ? Txt.FwIn : Txt.FwOut;
        var direction = e.Inbound ? FirewallDirection.In : FirewallDirection.Out;
        AllowCommand = new AsyncCommand(() => page.SetAppAsync(path!, true, direction), () => page.CanEdit);
        BlockCommand = new AsyncCommand(() => page.SetAppAsync(path!, false, direction), () => page.CanEdit);
    }

    public string Time { get; }
    public string Program { get; }
    public string Detail { get; }
    /// <summary>The drive-letter path; null for System and anything not on a lettered volume (no App rule for those).</summary>
    public string? Path { get; }
    public bool HasPath => Path is not null;
    public string Remote { get; }
    public string Protocol { get; }
    public string Direction { get; }
    public ICommand AllowCommand { get; }
    public ICommand BlockCommand { get; }
}

/// <summary>
/// Firewall: KOS Firewall (allow-list filters in the Windows filter engine, see <see cref="KosFirewall"/>), the Windows Firewall
/// profiles and the hosts file. Every visit only reads; each change is a click, saved through <see cref="KosFirewall"/> and read back.
/// Administrator only: the config folder is writable by administrators and the filter engine's lists need it too.
/// </summary>
public sealed class FirewallPageViewModel : PageViewModel
{
    FirewallConfig _config = new();
    int? _filters;
    // Every rebuild gives the filters new ids, and the engine's log keeps the old ones: every KOS filter id read this session.
    // shortcut: drops under ids from a rebuild made while this page never read them (KOS Engine, another run) aren't shown; upgrade if the engine logs drops itself.
    readonly HashSet<ulong> _kosIds = new();
    string _status = "", _windows = "";
    bool _statusWarning, _anyWindowsOff, _busy, _hostsLoaded;
    string? _bfe;
    IReadOnlyList<string> _notes = [];
    string _tab = "apps", _search = "";
    IReadOnlyList<FirewallRuleRow> _appRows = [], _customRows = [];
    List<FirewallTargetRow>? _services, _storeApps;
    ListCollectionView? _servicesView, _storeView;
    IReadOnlyList<FirewallEventRow> _events = [];
    string? _logNote, _listNote;
    string _hostsText = "", _hostsSaved = "";
    IReadOnlyList<string> _hostsProblems = [];
    string _ruleName = "", _ruleProgram = "", _ruleRemotePorts = "", _ruleLocalPorts = "", _ruleAddresses = "";
    OptionItem _ruleDirection, _ruleProtocol;

    public FirewallPageViewModel() : base("firewall", Txt.PageFirewall, "\uEA18", Txt.PageFirewallSubtitle) // a shield (escaped: private-use glyph)
    {
        Tabs =
        [
            new GpuTab("apps", Txt.FwTabApps, "\uE71D") { IsSelected = true }, new GpuTab("services", Txt.FwTabServices, "\uE81C"),
            new GpuTab("store", Txt.FwTabStore, "\uE719"), new GpuTab("rules", Txt.FwTabRules, "\uE8FD"),
            new GpuTab("log", Txt.FwTabLog, "\uE8A5"), new GpuTab("hosts", Txt.FwTabHosts, "\uE943"),
        ];
        Directions = [new("out", Txt.FwOut), new("in", Txt.FwIn), new("both", Txt.FwBoth)];
        Protocols = [new("", Txt.FwAny), new("6", "TCP"), new("17", "UDP"), new("1", "ICMP"), new("58", "ICMPv6")];
        RuleActions = [new OptionItem("allow", Txt.FwAllow) { IsSelected = true }, new OptionItem("block", Txt.FwBlock)];
        (_ruleDirection, _ruleProtocol) = (Directions[0], Protocols[0]);

        SelectTabCommand = new RelayCommand(p => MainViewModel.Fire(SelectTabAsync((string)p!)));
        ToggleKosCommand = new AsyncCommand(ToggleKosAsync, () => CanEdit && _bfe is null);
        TogglePopupsCommand = new AsyncCommand(TogglePopupsAsync, () => CanEdit);
        WindowsOnCommand = new AsyncCommand(WindowsOnAsync, () => CanEdit && _anyWindowsOff);
        AddProgramCommand = new AsyncCommand(AddProgramAsync, () => CanEdit);
        BrowseRuleProgramCommand = new RelayCommand(BrowseRuleProgram);
        SetRuleActionCommand = new RelayCommand(p => { foreach (var a in RuleActions) a.IsSelected = a.Key == (string)p!; });
        AddRuleCommand = new AsyncCommand(AddRuleAsync, () => CanEdit && FormProblem is null);
        PresetCommand = new AsyncCommand(p => PresetAsync((string)p!), _ => CanEdit);
        RefreshLogCommand = new AsyncCommand(LoadLogAsync, () => !_busy);
        SaveHostsCommand = new AsyncCommand(SaveHostsAsync, () => CanEdit && HostsDirty && _hostsProblems.Count == 0);
        ReloadHostsCommand = new AsyncCommand(LoadHostsAsync, () => !_busy);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        RestartAsAdminCommand = new RelayCommand(SettingsViewModel.RestartAsAdmin);
    }

    public override bool ScrollsItself => true;
    public bool IsAdmin => Elevation.IsAdmin;
    public bool NeedsAdmin => !Elevation.IsAdmin;
    internal bool CanEdit => Elevation.IsAdmin && !_busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    // ---------- header ----------
    public bool KosOn => _config.Enabled;
    public bool Popups => _config.Popups;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusWarning { get => _statusWarning; private set => Set(ref _statusWarning, value); }
    public string WindowsState { get => _windows; private set => Set(ref _windows, value); }
    /// <summary>Why it can't run or what's off (BFE, the config folder, KOS Engine); warnings.</summary>
    public IReadOnlyList<string> Notes { get => _notes; private set => Set(ref _notes, value); }
    public ICommand ToggleKosCommand { get; }
    public ICommand TogglePopupsCommand { get; }
    public ICommand WindowsOnCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    // ---------- tabs ----------
    public IReadOnlyList<GpuTab> Tabs { get; }
    public ICommand SelectTabCommand { get; }
    public bool IsAppsTab => _tab == "apps";
    public bool IsServicesTab => _tab == "services";
    public bool IsStoreTab => _tab == "store";
    public bool IsRulesTab => _tab == "rules";
    public bool IsLogTab => _tab == "log";
    public bool IsHostsTab => _tab == "hosts";

    public IReadOnlyList<FirewallRuleRow> AppRows { get => _appRows; private set { Set(ref _appRows, value); Raise(nameof(NoAppRows)); } }
    public bool NoAppRows => _appRows.Count == 0;
    public ICommand AddProgramCommand { get; }

    public ListCollectionView? ServicesView { get => _servicesView; private set => Set(ref _servicesView, value); }
    public ListCollectionView? StoreView { get => _storeView; private set => Set(ref _storeView, value); }
    /// <summary>"Reading…" or the reason a list couldn't be read.</summary>
    public string? ListNote { get => _listNote; private set => Set(ref _listNote, value); }
    public string SearchText
    {
        get => _search;
        set { if (!Set(ref _search, value ?? "")) return; _servicesView?.Refresh(); _storeView?.Refresh(); }
    }
    public ICommand ClearSearchCommand { get; }

    public IReadOnlyList<FirewallRuleRow> CustomRows { get => _customRows; private set { Set(ref _customRows, value); Raise(nameof(NoCustomRows)); } }
    public bool NoCustomRows => _customRows.Count == 0;
    public IReadOnlyList<OptionItem> Directions { get; }
    public IReadOnlyList<OptionItem> Protocols { get; }
    public IReadOnlyList<OptionItem> RuleActions { get; }
    public string RuleName { get => _ruleName; set { if (Set(ref _ruleName, value ?? "")) RaiseForm(); } }
    public string RuleProgram { get => _ruleProgram; set { if (Set(ref _ruleProgram, value ?? "")) RaiseForm(); } }
    public string RuleRemotePorts { get => _ruleRemotePorts; set { if (Set(ref _ruleRemotePorts, value ?? "")) RaiseForm(); } }
    public string RuleLocalPorts { get => _ruleLocalPorts; set { if (Set(ref _ruleLocalPorts, value ?? "")) RaiseForm(); } }
    public string RuleAddresses { get => _ruleAddresses; set { if (Set(ref _ruleAddresses, value ?? "")) RaiseForm(); } }
    // Form state only: choosing in the lists changes nothing on the system.
    public OptionItem RuleDirection { get => _ruleDirection; set { if (value is not null && Set(ref _ruleDirection, value)) RaiseForm(); } }
    public OptionItem RuleProtocol { get => _ruleProtocol; set { if (value is not null && Set(ref _ruleProtocol, value)) RaiseForm(); } }
    /// <summary>Why the form's rule can't be added; not shown while the form is empty.</summary>
    public string? RuleProblem => _ruleName.Length + _ruleProgram.Length + _ruleRemotePorts.Length + _ruleLocalPorts.Length + _ruleAddresses.Length == 0 ? null : FormProblem;
    public ICommand BrowseRuleProgramCommand { get; }
    public ICommand SetRuleActionCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand PresetCommand { get; }

    public IReadOnlyList<FirewallEventRow> Events { get => _events; private set => Set(ref _events, value); }
    public string? LogNote { get => _logNote; private set => Set(ref _logNote, value); }
    public ICommand RefreshLogCommand { get; }

    public string HostsText
    {
        get => _hostsText;
        set
        {
            if (!Set(ref _hostsText, value ?? "")) return;
            // Lines Windows already ignores in the saved file don't block a save; new mistakes do.
            HostsProblems = HostsFile.NewProblems(_hostsText, _hostsSaved).Select(p => Txt.FwHostsLine(p.Line, p.Message)).ToList();
            Raise(nameof(HostsDirty));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HostsDirty => _hostsText != _hostsSaved;
    public IReadOnlyList<string> HostsProblems { get => _hostsProblems; private set { Set(ref _hostsProblems, value); Raise(nameof(HostsValid)); } }
    public bool HostsValid => _hostsProblems.Count == 0;
    public string HostsHint => Txt.FwHostsHint;
    public ICommand SaveHostsCommand { get; }
    public ICommand ReloadHostsCommand { get; }

    // ---------- reading ----------

    /// <summary>Reads only: the config, the filters, BFE, Windows Firewall and the open tab's list.</summary>
    public override async Task RefreshAsync()
    {
        if (!Elevation.IsAdmin || _busy) return;
        await ReadAsync();
        await LoadTabAsync(reload: true);
    }

    /// <summary>Never throws (it also runs after a change, inside its error handling): what can't be read goes to the notes.</summary>
    async Task ReadAsync()
    {
        try { await ReadCoreAsync(); }
        catch (Exception ex)
        {
            Log.Error("Firewall: reading the state failed", ex);
            Notes = [ex.Message];
        }
    }

    async Task ReadCoreAsync()
    {
        var r = await Task.Run(() =>
        {
            var config = KosFirewall.Load();
            var notes = new List<string>();
            string? bfe;
            try { bfe = KosFirewall.BfeProblem(); } catch (Exception ex) { bfe = ex.Message; }
            if (bfe is not null) notes.Add(bfe);
            try { if (KosFirewall.Problem() is { } folder) notes.Add(folder); } catch (Exception ex) { notes.Add(ex.Message); }
            int? filters = null;
            var ids = new List<ulong>();
            if (bfe is null)
                try { ids = KosFirewall.Filters().Select(f => f.Id).ToList(); filters = ids.Count; }
                catch (Exception ex) { notes.Add(Txt.FwStatusUnread(ex.Message)); }
            bool[] windows = new bool[WindowsFirewall.Profiles.Length];
            string windowsText;
            try
            {
                windows = WindowsFirewall.State();
                windowsText = WindowsFirewall.ServiceRunning
                    ? string.Join(" · ", WindowsFirewall.Profiles.Select((p, i) => Txt.FwProfile(p, windows[i], WindowsFirewall.ForcedByPolicy(i))))
                    : Txt.FwWindowsServiceStopped;
            }
            catch (Exception ex) { windowsText = ex.Message; }
            try { if (config is { Enabled: true, Popups: true } && !GovernorFanCurve.EngineRunning) notes.Add(Txt.FwEngineNotRunning); }
            catch (Exception ex) { notes.Add(ex.Message); }
            return (config, bfe, filters, ids, notes, windows, windowsText);
        });
        _config = r.config;
        _bfe = r.bfe;
        _filters = r.filters;
        _kosIds.UnionWith(r.ids);
        _anyWindowsOff = r.windows.Any(on => !on);
        WindowsState = Txt.FwWindows(r.windowsText);
        Notes = r.notes;
        (Status, StatusWarning) = (r.config.Enabled, r.filters) switch
        {
            (var on, null) => (on ? Txt.FwStatusOnUnread : Txt.FwStatusOff, on), // why it's unread is in the notes
            (true, 0) => (Txt.FwStatusOnNoFilters, true),
            (true, var n) => (Txt.FwStatusOn(n.Value), false),
            (false, 0) => (Txt.FwStatusOff, false),
            (false, var n) => (Txt.FwStatusOffLeftover(n.Value), true),
        };
        Raise(nameof(KosOn));
        Raise(nameof(Popups));
        AppRows = _config.Rules.Where(x => x.Kind == FirewallRuleKind.App).Select(x => new FirewallRuleRow(x, this)).ToList();
        CustomRows = _config.Rules.Where(x => x.Kind == FirewallRuleKind.Custom).Select(x => new FirewallRuleRow(x, this)).ToList();
        foreach (var row in (_services ?? []).Concat(_storeApps ?? [])) row.Show(_config);
        CommandManager.InvalidateRequerySuggested();
    }

    async Task SelectTabAsync(string key)
    {
        if (key == _tab) return;
        _tab = key;
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        foreach (var name in new[] { nameof(IsAppsTab), nameof(IsServicesTab), nameof(IsStoreTab), nameof(IsRulesTab), nameof(IsLogTab), nameof(IsHostsTab) }) Raise(name);
        if (Elevation.IsAdmin) await LoadTabAsync(reload: false);
    }

    /// <summary>The open tab's own list: loaded on first open, again on a refresh (the hosts text only on first open and on Reload).</summary>
    async Task LoadTabAsync(bool reload)
    {
        switch (_tab)
        {
            case "services" when reload || _services is null:
                await LoadListAsync(() => ServiceControl.Enumerate(ServiceControl.TypeWin32)
                    .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(s => new FirewallTargetRow(FirewallRuleKind.Service, s.DisplayName.Length > 0 ? s.DisplayName : s.Name, s.Name, s.Name, null, this)).ToList(),
                    rows => { _services = rows; ServicesView = View(rows); });
                break;
            case "store" when reload || _storeApps is null:
                await LoadListAsync(() => StoreApps.List().Where(a => a.Kind != StoreAppKind.Framework)
                    .DistinctBy(a => a.FamilyName, StringComparer.OrdinalIgnoreCase)
                    .Select(a => (App: a, Sid: FirewallIdentity.PackageSid(a.FamilyName))).Where(x => x.Sid is not null)
                    .Select(x => new FirewallTargetRow(FirewallRuleKind.StoreApp, x.App.Name is { Length: > 0 } n ? n : x.App.FamilyName, x.App.FamilyName, null, x.Sid, this)).ToList(),
                    rows => { _storeApps = rows; StoreView = View(rows); });
                break;
            case "log":
                await LoadLogAsync();
                break;
            case "hosts" when !_hostsLoaded:
                await LoadHostsAsync();
                break;
        }
    }

    async Task LoadListAsync(Func<List<FirewallTargetRow>> read, Action<List<FirewallTargetRow>> show)
    {
        ListNote = Txt.FwLoading;
        try
        {
            var rows = await Task.Run(read);
            foreach (var row in rows) row.Show(_config);
            show(rows);
            ListNote = null;
        }
        catch (Exception ex)
        {
            Log.Error("Firewall: reading a list failed", ex);
            ListNote = ex.Message;
        }
    }

    ListCollectionView View(List<FirewallTargetRow> rows) => new(rows)
    {
        Filter = o => _search.Trim() is not { Length: > 0 } q || o is FirewallTargetRow r && (MainViewModel.Matches(r.Name, q) || MainViewModel.Matches(r.Detail, q)),
    };

    async Task LoadLogAsync()
    {
        if (_bfe is not null) { (Events, LogNote) = ([], _bfe); return; }
        try
        {
            var known = _kosIds.ToHashSet();
            var (events, collects) = await Task.Run(() =>
            {
                using var s = WfpSession.Open(dynamic: false);
                known.UnionWith(s.Filters().Select(f => f.Id));
                var dropped = s.RecentEvents().Where(e => e.Dropped && known.Contains(e.FilterId)).Reverse()
                    .Select(e => (Event: e, Path: FirewallIdentity.DosPath(e.AppDevicePath))).ToList();
                return (dropped, s.CollectsNetEvents);
            });
            _kosIds.UnionWith(known);
            Events = events.Select(x => new FirewallEventRow(x.Event, x.Path, this)).ToList();
            LogNote = !collects ? Txt.FwLogOff : events.Count == 0 ? Txt.FwLogEmpty : null;
        }
        catch (Exception ex)
        {
            Log.Error("Firewall: reading the net-event log failed", ex);
            (Events, LogNote) = ([], ex.Message);
        }
    }

    async Task LoadHostsAsync()
    {
        try
        {
            var text = await Task.Run(() => HostsFile.Read());
            _hostsSaved = text;
            _hostsLoaded = true;
            HostsText = text;
            Raise(nameof(HostsDirty));
        }
        catch (Exception ex)
        {
            Log.Error("Firewall: reading the hosts file failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    // ---------- changes (each one a click; the result is what was read back) ----------

    /// <summary>Runs a change, reads everything back and reports it; rules the filters left out become a warning.</summary>
    async Task RunAsync(Func<Task<IReadOnlyList<string>?>> change, Func<string> done)
    {
        if (!CanEdit) return;
        IsBusy = true;
        try
        {
            var skipped = await change();
            await ReadAsync();
            Toasts.Show(done(), StatusWarning ? ToastKind.Warning : ToastKind.Success);
            if (skipped is { Count: > 0 }) Toasts.Show(Txt.FwSkipped(skipped), ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("Firewall: a change failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
            await ReadAsync();
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(KosOn)); // the switch shows what was read, also after a failure
            Raise(nameof(Popups));
        }
    }

    /// <summary>Edits the saved config (never the copy this page shows: KOS Engine writes the same file) and rebuilds the filters when on.</summary>
    Task ChangeAsync(Action<FirewallConfig> change, Func<string> done) =>
        RunAsync(async () => (await Task.Run(() => KosFirewall.Change(change)))?.Skipped, () => done() + " " + Txt.FwSaved(_config.Enabled ? Status : null));

    Task ToggleKosAsync() => _config.Enabled
        ? RunAsync(async () => { await Task.Run(() => KosFirewall.TurnOff(restoreWindows: true)); return null; }, () => Txt.FwTurnedOff(WindowsState))
        : RunAsync(async () => (await Task.Run(KosFirewall.TurnOn)).Skipped, () => Txt.FwTurnedOn(Status));

    Task TogglePopupsAsync()
    {
        var on = !_config.Popups;
        return RunAsync(async () => (await Task.Run(() => KosFirewall.Change(c => c.Popups = on)))?.Skipped, () => Txt.FwPopups(_config.Popups));
    }

    /// <summary>Windows Firewall on first (an exception stops here), then KOS Firewall off: never a moment with neither.</summary>
    Task WindowsOnAsync() => RunAsync(async () =>
    {
        await Task.Run(() =>
        {
            WindowsFirewall.Set(on: true);
            if (KosFirewall.Load().Enabled) KosFirewall.TurnOff(restoreWindows: false);
        });
        return null;
    }, () => Txt.FwWindowsTurnedOn(WindowsState, _config.Enabled));

    internal Task EditAsync(string id, Action<FirewallRule> edit, string name) =>
        ChangeAsync(c => { if (c.Rules.Find(r => r.Id == id) is { } rule) edit(rule); }, () => name + ":");

    internal Task RemoveAsync(string id, string name) =>
        ChangeAsync(c => c.Rules.RemoveAll(r => r.Id == id), () => Txt.FwRuleSet(name, null));

    internal Task SetAppAsync(string path, bool allow, FirewallDirection direction) =>
        ChangeAsync(c => FirewallRules.Choose(c, new FirewallRule { Kind = FirewallRuleKind.App, Path = path, Direction = direction }, allow),
            () => Txt.FwRuleSet(Path.GetFileName(path), allow));

    internal async Task ChooseAsync(FirewallTargetRow row, bool? allow)
    {
        var rule = row.NewRule();
        if (FirewallRules.Current(_config, rule) == allow) return; // already so: no rebuild
        if (rule.Kind == FirewallRuleKind.Service && allow is not null)
        {
            rule.Sid = await Task.Run(() => FirewallIdentity.ServiceSid(rule.Service!));
            if (rule.Sid is null) { Toasts.Show(Txt.FwNoServiceSid(rule.Service!), ToastKind.Error); row.Show(_config); return; }
        }
        await ChangeAsync(c => FirewallRules.Choose(c, rule, allow), () => Txt.FwRuleSet(row.Name, allow));
        row.Show(_config); // also when nothing changed: the segment shows the saved choice
    }

    async Task AddProgramAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Txt.FwAddProgramTitle, Filter = Txt.FwExeFilter };
        if (dialog.ShowDialog() == true) await SetAppAsync(dialog.FileName, true, FirewallDirection.Out);
    }

    void BrowseRuleProgram()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Txt.FwRuleProgram, Filter = Txt.FwExeFilter };
        if (dialog.ShowDialog() == true) RuleProgram = dialog.FileName;
    }

    FirewallRule FormRule() => new()
    {
        Kind = FirewallRuleKind.Custom,
        Name = _ruleName.Trim(),
        Path = _ruleProgram.Trim() is { Length: > 0 } p ? p : null,
        Allow = RuleActions[0].IsSelected,
        Direction = _ruleDirection.Key switch { "in" => FirewallDirection.In, "both" => FirewallDirection.Both, _ => FirewallDirection.Out },
        Protocol = int.TryParse(_ruleProtocol.Key, out var protocol) ? protocol : null,
        RemotePorts = Blank(_ruleRemotePorts), LocalPorts = Blank(_ruleLocalPorts), RemoteAddresses = Blank(_ruleAddresses),
    };

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    string? FormProblem
    {
        get
        {
            if (_ruleName.Trim().Length == 0) return Txt.FwRuleNeedsName;
            var rule = FormRule();
            var problem = FirewallPolicy.Problem(rule) ?? (rule.Path is { } p && !File.Exists(p) ? Txt.FwMissingProgram : null);
            return problem is null ? null : Txt.FwRuleProblem(problem);
        }
    }

    void RaiseForm()
    {
        Raise(nameof(RuleProblem));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task AddRuleAsync()
    {
        if (FormProblem is not null) return;
        var rule = FormRule();
        await ChangeAsync(c => c.Rules.Add(rule), () => Txt.FwRuleAdded(rule.Name));
        if (_config.Rules.Any(r => r.Id == rule.Id)) // read back: clear the form only once the rule is in the saved config
            (RuleName, RuleProgram, RuleRemotePorts, RuleLocalPorts, RuleAddresses) = ("", "", "", "", "");
    }

    async Task PresetAsync(string key)
    {
        if (key != "windows-update")
        {
            var rules = key == "ntp" ? FirewallRules.Ntp() : FirewallRules.NetworkDiscovery();
            var added = 0;
            await ChangeAsync(c => added = FirewallRules.AddMissing(c, rules), () => Txt.FwPresetAdded(added));
            return;
        }
        var services = await Task.Run(() => FirewallRules.WindowsUpdateServices
            .Select(s => new FirewallRule { Kind = FirewallRuleKind.Service, Name = s, Service = s, Sid = FirewallIdentity.ServiceSid(s) }).ToList());
        var missing = services.Where(s => s.Sid is null).Select(s => s.Service!).ToList();
        var found = services.Where(s => s.Sid is not null).ToList();
        if (found.Count == 0) { Toasts.Show(Txt.FwPresetServices([], missing), ToastKind.Error); return; }
        await ChangeAsync(c => { foreach (var s in found) FirewallRules.Choose(c, s, true); },
            () => Txt.FwPresetServices(found.Select(s => s.Service!), missing));
    }

    async Task SaveHostsAsync()
    {
        if (!CanEdit) return;
        IsBusy = true;
        try
        {
            var text = _hostsText;
            var backup = await Task.Run(() => HostsFile.Save(text));
            await LoadHostsAsync(); // what's on disk now
            Toasts.Show(backup is null ? Txt.FwHostsSavedNoBackup : Txt.FwHostsSaved(backup));
        }
        catch (Exception ex)
        {
            Log.Error("Firewall: saving the hosts file failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally { IsBusy = false; }
    }

    // ---------- text ----------

    internal static string DirectionText(FirewallDirection d) => d switch { FirewallDirection.In => Txt.FwIn, FirewallDirection.Both => Txt.FwBoth, _ => Txt.FwOut };

    internal static string ProtocolText(int? protocol) => protocol switch
    {
        null => Txt.FwAny, 6 => "TCP", 17 => "UDP", 1 => "ICMP", 58 => "ICMPv6", var p => p.ToString()!,
    };

    /// <summary>"TCP · remote ports 80, 443 · C:\x.exe" for a custom rule (its direction has its own column).</summary>
    internal static string Summary(FirewallRule r) => string.Join(" · ", new[]
    {
        ProtocolText(r.Protocol),
        r.RemotePorts is { Length: > 0 } rp ? $"{Txt.FwRuleRemotePorts.ToLowerInvariant()} {rp}" : null,
        r.LocalPorts is { Length: > 0 } lp ? $"{Txt.FwRuleLocalPorts.ToLowerInvariant()} {lp}" : null,
        r.RemoteAddresses is { Length: > 0 } ra ? ra : null,
        r.Path,
    }.Where(s => !string.IsNullOrEmpty(s)));
}
