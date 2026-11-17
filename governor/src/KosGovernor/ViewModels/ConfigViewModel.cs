using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using KosGovernor.Core;
using KosGovernor.Views;
using Microsoft.Win32;

namespace KosGovernor.ViewModels;

public sealed class RuleRow
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public Thickness Indent { get; init; }
    public FontWeight Weight { get; init; } = FontWeights.Normal;
    public bool IsProcess { get; init; }
    public ICommand? EditCommand { get; init; }
    public ICommand? DeleteCommand { get; init; }
    public ICommand? AddServiceCommand { get; init; }
    public ICommand? AddThreadCommand { get; init; }
}

public sealed class ColumnOption(string key, bool visible, Action<string, bool> changed) : ObservableObject
{
    bool _visible = visible;
    public string Key => key;
    public bool IsVisible { get => _visible; set { if (Set(ref _visible, value)) changed(key, value); } }
}

/// <summary>
/// Configuration screens (Rules, Masks, Gamemode, General) of the active profile. Every change rereads the file, applies the
/// change, validates and saves right away: the engine rereads on its own and nothing the tray changed is overwritten.
/// </summary>
public sealed class ConfigViewModel : ObservableObject
{
    public static readonly string[] ProcessColumns = { "Icon", "PID", "CPU %", "Memory", "GPU %", "VRAM", "CPU Priority", "I/O Priority", "Memory Priority", "Threads", "Status", "Boost", "Eco", "Affinity", "CPU Sets" };
    public static readonly string[] ThreadColumns = { "Description", "Priority", "Base", "I/O", "Memory", "Ideal CPU", "Status", "Boost", "Module" };

    readonly MainViewModel _main;
    GovernorConfig _config = new();
    string _tab = "Rules", _search = "";
    bool _enablePhase = true, _capturing;
    bool? _signIn;
    string? _loadProblem;

    public ConfigViewModel(MainViewModel main)
    {
        _main = main;
        AddRuleCommand = new RelayCommand(() => EditRule(RulesList, null));
        AddFromOpenCommand = new RelayCommand(AddFromOpen);
        AddGamemodeRuleCommand = new RelayCommand(() => EditRule(PhaseList, null));
        AddStartCommand = new RelayCommand(AddStart);
        AddMaskCommand = new RelayCommand(() => EditMask(null));
        AddGameCommand = new RelayCommand(AddGame);
        ShowGameListCommand = new RelayCommand(ShowGameList);
        ShowGameSettingsCommand = new RelayCommand(() => GameSettingsWindow.ShowSingle(_main));
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        ChangeHotkeyCommand = new RelayCommand(() => CapturingHotkey = true);
        ResetHotkeyCommand = new RelayCommand(() => Mutate(c => c.Hotkey = new Hotkey(), "Hotkey reset to Ctrl+Alt+G."));
        StartEngineCommand = new RelayCommand(() => { EngineHost.EnsureRunning(); _main.Report("Engine started."); });
        StopEngineCommand = new RelayCommand(() => { Names.Signal(Names.Stop); _main.Report("Engine stopped: rules aren't applied until it starts again."); });
        Load(firstRun: true);
    }

    public GovernorConfig Config => _config;
    public event Action? ColumnsChanged;

    public ICommand AddRuleCommand { get; }
    public ICommand AddFromOpenCommand { get; }
    public ICommand AddGamemodeRuleCommand { get; }
    public ICommand AddStartCommand { get; }
    public ICommand AddMaskCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand ShowGameListCommand { get; }
    public ICommand ShowGameSettingsCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ChangeHotkeyCommand { get; }
    public ICommand ResetHotkeyCommand { get; }
    public ICommand StartEngineCommand { get; }
    public ICommand StopEngineCommand { get; }

    public string? LoadProblem { get => _loadProblem; private set => Set(ref _loadProblem, value); }
    public string ProfileName => _config.Active.Name;

    // ---------- tabs ----------
    public string Tab
    {
        get => _tab;
        set { if (Set(ref _tab, value)) foreach (var n in new[] { nameof(IsRules), nameof(IsMasks), nameof(IsGamemode), nameof(IsRtss), nameof(IsGeneral) }) Raise(n); }
    }
    public bool IsRules { get => _tab == "Rules"; set { if (value) Tab = "Rules"; } }
    public bool IsMasks { get => _tab == "Masks"; set { if (value) Tab = "Masks"; } }
    public bool IsGamemode { get => _tab == "Gamemode"; set { if (value) Tab = "Gamemode"; } }
    public bool IsRtss { get => _tab == "Rtss"; set { if (value) Tab = "Rtss"; } }
    public bool IsGeneral { get => _tab == "General"; set { if (value) Tab = "General"; } }

    public string RuleSearch { get => _search; set { if (Set(ref _search, value ?? "")) Raise(nameof(RuleRows)); } }

    static List<ProcessRule> RulesList(Profile p) => p.Rules;
    List<ProcessRule> PhaseList(Profile p) => _enablePhase ? p.OnEnable.Rules : p.OnDisable.Rules;
    GamemodePhase Phase(Profile p) => _enablePhase ? p.OnEnable : p.OnDisable;

    public IReadOnlyList<RuleRow> RuleRows => Rows(RulesList, _search).ToList();
    public IReadOnlyList<RuleRow> GamemodeRows => StartRows().Concat(Rows(PhaseList, "")).ToList();

    public bool IsEnablePhase { get => _enablePhase; set { if (value && !_enablePhase) { _enablePhase = true; RaisePhase(); } } }
    public bool IsDisablePhase { get => !_enablePhase; set { if (value && _enablePhase) { _enablePhase = false; RaisePhase(); } } }
    public string PhaseHint => _enablePhase
        ? "While gamemode is on, these rules replace the profile's rules for the same program. Programs below open when it turns on."
        : "When gamemode turns off, these rules are applied once to the programs that are open. Programs below open when it turns off.";

    void RaisePhase()
    {
        Raise(nameof(IsEnablePhase));
        Raise(nameof(IsDisablePhase));
        Raise(nameof(GamemodeRows));
        Raise(nameof(PhaseHint));
    }

    // ---------- load and save ----------

    void Load(bool firstRun = false)
    {
        try
        {
            var fresh = !File.Exists(ConfigStore.FilePath);
            _config = ConfigStore.Load();
            LoadProblem = null;
            if (firstRun && fresh) ImportToolboxRules();
        }
        catch (Exception ex)
        {
            _config = new GovernorConfig();
            LoadProblem = $"The configuration can't be read: {ex.Message}";
            Log.Error("Loading the configuration failed", ex);
        }
        RaiseAll();
    }

    void ImportToolboxRules()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KOS", "Toolbox", "process-rules.json");
        var rules = ConfigStore.FromToolbox(path);
        if (rules.Count == 0) return;
        try
        {
            _config.Active.Rules.AddRange(rules);
            ConfigStore.Save(_config);
            Log.Info($"Imported {rules.Count} rule(s) from the toolbox's Processes page.");
        }
        catch (Exception ex)
        {
            _config = new GovernorConfig();
            Log.Warn($"The toolbox's rules weren't imported: {ex.Message}");
        }
    }

    public void Reload() => Load();

    void RaiseAll()
    {
        foreach (var n in new[] { nameof(ProfileName), nameof(RuleRows), nameof(GamemodeRows), nameof(MaskRows), nameof(HotkeyText), nameof(GuiInterval), nameof(AutoGamemode),
                     nameof(AutoOffDelay), nameof(AutoOpensPrograms), nameof(GameListText), nameof(GameRows),
                     nameof(EngineInterval), nameof(SessionReport), nameof(ProcessColumnOptions), nameof(ThreadColumnOptions), nameof(StartWithWindows), nameof(SignInProblem) })
            Raise(n);
        foreach (var n in SystemNames) Raise(n);
        ColumnsChanged?.Invoke();
        _main.ApplyInterval(_config.General.GuiIntervalMs);
    }

    /// <summary>Rereads the file, applies <paramref name="change"/>, validates and saves. False (with the message in the bar) if it failed.</summary>
    public bool Mutate(Action<GovernorConfig> change, string? done = null)
    {
        try
        {
            var fresh = ConfigStore.Load();
            change(fresh);
            ConfigStore.Save(fresh);
            _config = fresh;
            LoadProblem = null;
            RaiseAll();
            if (done is not null) _main.Report(done);
            return true;
        }
        catch (Exception ex)
        {
            _main.Report(ex.Message, true);
            Load();
            return false;
        }
    }

    static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ConfigStore.Json), ConfigStore.Json)!;

    // ---------- rules ----------

    IEnumerable<RuleRow> Rows(Func<Profile, List<ProcessRule>> list, string filter)
    {
        foreach (var r in list(_config.Active).Where(r => filter.Length == 0 || r.Exe.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(r => r.Exe, StringComparer.OrdinalIgnoreCase))
        {
            var exe = r.Exe;
            yield return new RuleRow
            {
                Title = exe, Summary = Labels.Summary(r), IsProcess = true, Weight = FontWeights.SemiBold, Indent = new Thickness(12, 0, 0, 0),
                EditCommand = new RelayCommand(() => EditRule(list, exe)),
                DeleteCommand = new RelayCommand(() => Delete($"the rule for {exe}", c => list(c.Active).RemoveAll(x => x.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))),
                AddServiceCommand = new RelayCommand(() => EditService(list, exe, null)),
                AddThreadCommand = new RelayCommand(() => EditThread(list, exe, null)),
            };
            foreach (var s in r.Services)
            {
                var name = s.Service;
                yield return new RuleRow
                {
                    Title = $"Service: {name}", Summary = Labels.Summary(s), Indent = new Thickness(36, 0, 0, 0),
                    EditCommand = new RelayCommand(() => EditService(list, exe, name)),
                    DeleteCommand = new RelayCommand(() => Delete($"the rule for the service {name}", c => Find(list(c.Active), exe)?.Services.RemoveAll(x => x.Service == name))),
                };
            }
            for (var i = 0; i < r.Threads.Count; i++)
            {
                var index = i;
                var t = r.Threads[i];
                var title = $"Thread: {(string.IsNullOrWhiteSpace(t.Description) ? "" : t.Description)}{(string.IsNullOrWhiteSpace(t.Module) ? "" : $" @ {t.Module}")}";
                yield return new RuleRow
                {
                    Title = title, Summary = Labels.Summary(t), Indent = new Thickness(36, 0, 0, 0),
                    EditCommand = new RelayCommand(() => EditThread(list, exe, index)),
                    DeleteCommand = new RelayCommand(() => Delete("this thread rule", c => Find(list(c.Active), exe)?.Threads.RemoveAt(index))),
                };
            }
        }
    }

    static ProcessRule? Find(List<ProcessRule> list, string exe) => list.FirstOrDefault(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    void Delete(string what, Action<GovernorConfig> remove)
    {
        if (Dialog.Confirm("Delete", $"Delete {what}?", "Delete")) Mutate(remove, "Deleted.");
    }

    void EditRule(Func<Profile, List<ProcessRule>> list, string? exe)
    {
        var existing = exe is null ? null : Find(list(_config.Active), exe);
        var edited = RuleDialog.EditProcess(existing is null ? null : Clone(existing), _main.RunningNames, _config,
            r => Validation.RuleProblem(r, list(_config.Active).Where(x => x != existing).Append(r), _config.Masks, _main.Topology));
        if (edited is null) return;
        Mutate(c =>
        {
            var l = list(c.Active);
            var old = exe is null ? null : Find(l, exe);
            edited.Services = old?.Services ?? new();
            edited.Threads = old?.Threads ?? new();
            if (old is null) l.Add(edited);
            else l[l.IndexOf(old)] = edited;
        }, $"Rule for {edited.Exe} saved.");
    }

    /// <summary>
    /// Rules for several open programs at once. Games get the cores as CPU sets (Windows can still use other cores when it has
    /// to, and anti-cheats accept it better); other programs get them as affinity. Existing rules keep everything else.
    /// </summary>
    void AddFromOpen()
    {
        if (AddRulesDialog.Pick(_main.OpenPrograms(new GameList(_config.AutoGamemode.Games)), _config) is not { } pick) return;
        Mutate(c =>
        {
            foreach (var p in pick.Programs)
            {
                if (Find(c.Active.Rules, p.Name) is not { } rule) c.Active.Rules.Add(rule = new ProcessRule { Exe = p.Name });
                if (p.IsGame) rule.CpuSets = pick.Cores;
                else rule.Affinity = pick.Cores;
                if (pick.Children) rule.IncludeChildren = true;
                if (Validation.RuleProblem(rule, c.Active.Rules, c.Masks, _main.Topology) is { } problem) throw new InvalidOperationException($"{p.Name}: {problem}");
            }
        }, $"Rules saved for {pick.Programs.Count} program(s).");
    }

    void EditService(Func<Profile, List<ProcessRule>> list, string exe, string? service)
    {
        var parent = Find(list(_config.Active), exe);
        var existing = service is null ? null : parent?.Services.FirstOrDefault(s => s.Service == service);
        var edited = RuleDialog.EditService(existing is null ? null : Clone(existing), exe, _main.ServicesOf(exe), _config,
            s => s.Service.Length == 0 ? "Enter the service name (as in services.msc → Service name)."
                : parent?.Services.Any(o => o != existing && o.Service.Equals(s.Service, StringComparison.OrdinalIgnoreCase)) == true ? $"There's already a rule for {s.Service}." : null);
        if (edited is null) return;
        Mutate(c =>
        {
            var p = Find(list(c.Active), exe) ?? throw new InvalidOperationException($"The rule for {exe} no longer exists.");
            var i = service is null ? -1 : p.Services.FindIndex(s => s.Service == service);
            if (i < 0) p.Services.Add(edited); else p.Services[i] = edited;
        }, $"Service rule for {edited.Service} saved.");
    }

    void EditThread(Func<Profile, List<ProcessRule>> list, string exe, int? index)
    {
        var parent = Find(list(_config.Active), exe);
        var existing = index is { } i && parent is not null && i < parent.Threads.Count ? parent.Threads[i] : null;
        var (descriptions, modules) = ThreadsWindow.KnownThreads(exe);
        var edited = RuleDialog.EditThread(existing is null ? null : Clone(existing), exe, descriptions, modules, _config);
        if (edited is null) return;
        Mutate(c =>
        {
            var p = Find(list(c.Active), exe) ?? throw new InvalidOperationException($"The rule for {exe} no longer exists.");
            if (index is { } at && at < p.Threads.Count) p.Threads[at] = edited; else p.Threads.Add(edited);
        }, "Thread rule saved.");
    }

    // ---------- gamemode ----------

    IEnumerable<RuleRow> StartRows()
    {
        var starts = Phase(_config.Active).Start;
        for (var i = 0; i < starts.Count; i++)
        {
            var index = i;
            var s = starts[i];
            yield return new RuleRow
            {
                Title = $"Start: {Path.GetFileName(s.Path)}", Summary = string.IsNullOrWhiteSpace(s.Arguments) ? s.Path : $"{s.Path}  {s.Arguments}",
                Indent = new Thickness(12, 0, 0, 0), Weight = FontWeights.SemiBold,
                EditCommand = new RelayCommand(() => EditStart(index)),
                DeleteCommand = new RelayCommand(() => Delete($"\"{Path.GetFileName(s.Path)}\" from the list", c => Phase(c.Active).Start.RemoveAt(index))),
            };
        }
    }

    void AddStart()
    {
        var pick = new OpenFileDialog { Title = "Program to open", Filter = "Programs (*.exe)|*.exe" };
        if (pick.ShowDialog() != true) return;
        var (ok, args) = Dialog.Prompt("Arguments", $"Arguments for {Path.GetFileName(pick.FileName)} (optional).", "", _ => null);
        if (!ok) return;
        var entry = new StartEntry { Path = pick.FileName, Arguments = args.Length == 0 ? null : args };
        Mutate(c => Phase(c.Active).Start.Add(entry), $"{Path.GetFileName(entry.Path)} added.");
    }

    void EditStart(int index)
    {
        var s = Phase(_config.Active).Start[index];
        var (ok, args) = Dialog.Prompt("Arguments", $"Arguments for {Path.GetFileName(s.Path)} (optional).", s.Arguments ?? "", _ => null);
        if (ok) Mutate(c => Phase(c.Active).Start[index].Arguments = args.Length == 0 ? null : args, "Saved.");
    }

    public string HotkeyText => _capturing ? "Press the new shortcut… (Esc cancels)"
        : Labels.Hotkey(_config.Hotkey, vk => System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString());

    public bool CapturingHotkey
    {
        get => _capturing;
        set { if (Set(ref _capturing, value)) Raise(nameof(HotkeyText)); }
    }

    /// <summary>Key captured by the window during "Change": needs at least one modifier.</summary>
    public void CaptureHotkey(ModifierKeys modifiers, Key key)
    {
        if (key == Key.Escape) { CapturingHotkey = false; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Hotkey.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Hotkey.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Hotkey.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= Hotkey.Win;
        CapturingHotkey = false;
        if (mods == 0) { _main.Report("Use at least one of Ctrl, Alt, Shift or Win with the key.", true); return; }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        Mutate(c => c.Hotkey = new Hotkey { Modifiers = mods, Key = vk }, "Hotkey saved.");
    }

    // ---------- masks ----------

    public sealed record MaskRow(string Name, string Cpus, ICommand EditCommand, ICommand DeleteCommand);

    public IReadOnlyList<MaskRow> MaskRows => _config.Masks.Select(m =>
    {
        var name = m.Name;
        return new MaskRow(name, m.Cpus, new RelayCommand(() => EditMask(name)),
            new RelayCommand(() => Delete($"the mask \"{name}\"", c => c.Masks.RemoveAll(x => x.Name == name))));
    }).ToList();

    void EditMask(string? name)
    {
        var existing = name is null ? null : _config.Masks.FirstOrDefault(m => m.Name == name);
        var initial = existing is null ? 0 : CpuSpec.ParseList(existing.Cpus, _main.Topology, out _) ?? 0;
        var (ok, mask, newName) = CpuDialog.Pick(existing is null ? "New mask" : "Edit mask", initial, _config, name ?? "", askName: true);
        if (!ok) return;
        Mutate(c =>
        {
            var m = name is null ? null : c.Masks.FirstOrDefault(x => x.Name == name);
            if (m is null) c.Masks.Add(m = new CpuMask());
            // Rename: rules that used the old name move to the new one.
            if (name is not null && name != newName)
                foreach (var r in c.Profiles.SelectMany(Validation.AllRules))
                {
                    if (r.Affinity == CpuSpec.MaskPrefix + name) r.Affinity = CpuSpec.MaskPrefix + newName;
                    if (r.CpuSets == CpuSpec.MaskPrefix + name) r.CpuSets = CpuSpec.MaskPrefix + newName;
                }
            m.Name = newName;
            m.Cpus = CpuSpec.FormatList(mask);
        }, $"Mask \"{newName}\" saved.");
    }

    // ---------- profiles ----------

    public void SwitchProfile(int index) => Mutate(c => c.ActiveProfile = index, null);

    public void NewProfile(bool copy)
    {
        var (ok, name) = Dialog.Prompt(copy ? "Duplicate profile" : "New profile", "Name of the profile.", copy ? $"{ProfileName} copy" : $"Profile {_config.Profiles.Count + 1}",
            n => NameProblem(n, null));
        if (!ok) return;
        Mutate(c =>
        {
            var p = copy ? Clone(c.Active) : new Profile();
            p.Name = name;
            c.Profiles.Add(p);
            c.ActiveProfile = c.Profiles.Count - 1;
        }, $"Profile \"{name}\" created and active.");
    }

    public void RenameProfile()
    {
        var current = ProfileName;
        var (ok, name) = Dialog.Prompt("Rename profile", "New name of the profile.", current, n => NameProblem(n, current));
        if (ok && name != current) Mutate(c => c.Active.Name = name, "Profile renamed.");
    }

    public void DeleteProfile()
    {
        if (_config.Profiles.Count == 1) { _main.Report("The only profile can't be deleted.", true); return; }
        if (!Dialog.Confirm("Delete profile", $"Delete the profile \"{ProfileName}\" and its rules?", "Delete")) return;
        Mutate(c =>
        {
            c.Profiles.RemoveAt(c.ActiveProfile);
            c.ActiveProfile = 0;
        }, "Profile deleted.");
    }

    string? NameProblem(string name, string? current) =>
        name.Length is 0 or > 60 ? "Use 1 to 60 characters."
        : _config.Profiles.Any(p => p.Name != current && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "There's already a profile with this name." : null;

    void Export()
    {
        var save = new SaveFileDialog { Title = "Export profile", FileName = $"{ProfileName}.json", Filter = "Governor profile (*.json)|*.json" };
        if (save.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(save.FileName, ConfigStore.Export(_config, _config.Active));
            _main.Report($"Profile exported to {save.FileName}.");
        }
        catch (Exception ex) { _main.Report($"Export failed: {ex.Message}", true); }
    }

    void Import()
    {
        var open = new OpenFileDialog { Title = "Import profile", Filter = "Governor profile (*.json)|*.json" };
        if (open.ShowDialog() != true) return;
        string json;
        try { json = File.ReadAllText(open.FileName); }
        catch (Exception ex) { _main.Report($"The file can't be read: {ex.Message}", true); return; }
        Profile? imported = null;
        Mutate(c =>
        {
            imported = ConfigStore.Import(c, json);
            var starts = imported.OnEnable.Start.Concat(imported.OnDisable.Start).Select(s => s.Path).ToList();
            var kills = Validation.AllRules(imported).OfType<ProcessRule>().Where(r => r.Action == RuleAction.Terminate).Select(r => r.Exe).ToList();
            // The imported profile runs as administrator: shows what it opens and terminates before accepting.
            if ((starts.Count > 0 || kills.Count > 0) && !Dialog.Confirm("Import profile",
                    $"This profile{(starts.Count > 0 ? $" opens these programs with gamemode: {string.Join(", ", starts)}" : "")}" +
                    $"{(starts.Count > 0 && kills.Count > 0 ? " and" : "")}{(kills.Count > 0 ? $" ends these programs: {string.Join(", ", kills)}" : "")}. Import it?", "Import"))
                throw new OperationCanceledException("Import cancelled.");
        }, null);
        if (imported is not null && _config.Profiles.Any(p => p.Name == imported.Name)) _main.Report($"Profile \"{imported.Name}\" imported. Pick it in the profile menu to use it.");
    }

    // ---------- general ----------

    public IReadOnlyList<ColumnOption> ProcessColumnOptions => ProcessColumns.Select(k => new ColumnOption(k, !_config.General.HiddenProcessColumns.Contains(k),
        (key, visible) => Mutate(c => { c.General.HiddenProcessColumns.Remove(key); if (!visible) c.General.HiddenProcessColumns.Add(key); }))).ToList();

    public IReadOnlyList<ColumnOption> ThreadColumnOptions => ThreadColumns.Select(k => new ColumnOption(k, !_config.General.HiddenThreadColumns.Contains(k),
        (key, visible) => Mutate(c => { c.General.HiddenThreadColumns.Remove(key); if (!visible) c.General.HiddenThreadColumns.Add(key); }))).ToList();

    // ---------- automatic gamemode ----------

    public bool AutoGamemode
    {
        get => _config.AutoGamemode.Enabled;
        set { if (value != _config.AutoGamemode.Enabled) Mutate(c => c.AutoGamemode.Enabled = value, value ? "Automatic gamemode on." : "Automatic gamemode off."); }
    }

    public int AutoOffDelay
    {
        get => _config.AutoGamemode.OffDelaySeconds;
        set { if (value != _config.AutoGamemode.OffDelaySeconds) Mutate(c => c.AutoGamemode.OffDelaySeconds = Math.Clamp(value, Validation.MinOffDelay, Validation.MaxOffDelay)); }
    }

    /// <summary>Of the active profile: also open the On Enable/On Disable programs on an automatic switch.</summary>
    public bool AutoOpensPrograms
    {
        get => _config.Active.AutoOpensPrograms;
        set { if (value != _config.Active.AutoOpensPrograms) Mutate(c => c.Active.AutoOpensPrograms = value); }
    }

    public string GameListText
    {
        get
        {
            var builtIn = GameList.BuiltIn;
            return $"Built-in list: {builtIn.Count(g => !g.IsBlacklisted)} games; {builtIn.Count(g => g.IsBlacklisted)} programs are never treated as games (launchers, browsers, tools). "
                   + (_config.AutoGamemode.Games.Count == 0 ? "Right-click a process › Gamemode to change it." : "Your changes:");
        }
    }

    public sealed record GameRow(string Name, string Kind, ICommand RemoveCommand);

    public IReadOnlyList<GameRow> GameRows => _config.AutoGamemode.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => new GameRow(g.Name + ".exe", g.IsBlacklisted ? "never a game" : "game", new RelayCommand(() => SetGame(g.Name, null)))).ToList();

    /// <summary>
    /// Changes the program in the list: true = is a game, false = never a game, null = back to what the built-in list says. Only stores
    /// what differs from the built-in list.
    /// </summary>
    public void SetGame(string processName, bool? isGame)
    {
        var key = GameList.Key(processName);
        var builtIn = GameList.BuiltIn.FirstOrDefault(g => g.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        var label = key + ".exe";
        Mutate(c =>
        {
            c.AutoGamemode.Games.RemoveAll(g => GameList.Key(g.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (isGame is { } game && (builtIn is null || builtIn.IsBlacklisted == game))
                c.AutoGamemode.Games.Add(new GameEntry(key, builtIn?.DisplayName, !game));
        }, isGame switch
        {
            true => $"{label} turns gamemode on when it starts.",
            false => $"{label} is never treated as a game.",
            null => $"{label} follows the built-in list again.",
        });
    }

    // ---------- Windows changes while gamemode is on (active profile) ----------

    static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    static readonly string[] SystemNames = { nameof(SysPowerPlan), nameof(SysPowerPlanId), nameof(PowerPlanOptions), nameof(SysStopUpdate), nameof(SysStopSearch),
        nameof(SysTransparency), nameof(SysCStates), nameof(SysCleanMemory), nameof(SysBackgroundEfficiency), nameof(SysGameCores), nameof(SysGameCoresSpec),
        nameof(GameCoresOptions), nameof(SysCleanDelay), nameof(SysPurgeStandby), nameof(SysPurgeStandbyMb), nameof(SysPurgeFreeMb), nameof(SysRestrain),
        nameof(SysKeepAwake), nameof(SysDwmMmcss), nameof(SysDirector), nameof(SysHeavyThreads), nameof(SysMoveBackground),
        nameof(SysNormalizePriorities), nameof(SysAvoidInterrupts), nameof(SysWifi), nameof(SysAudio), nameof(SysRtssOsd), nameof(SysRtssFrameLimit), nameof(RtssOsdOptions), nameof(SysRtssLimiterMode),
        nameof(TimerMode), nameof(TimerResolution), nameof(TimerOn), nameof(TimerNote) };
    GamemodeSystemSettings Sys => _config.Active.System;

    void SetSystem(Action<GamemodeSystemSettings> set, string done) => Mutate(c => set(c.Active.System), done);

    public IReadOnlyList<KosToolbox.Core.Power.PowerPlan> PowerPlanOptions
    {
        get
        {
            try { return KosToolbox.Core.Power.PowerPlans.List(); }
            catch (Exception ex) { Log.Warn($"Listing power plans failed: {ex.Message}"); return Array.Empty<KosToolbox.Core.Power.PowerPlan>(); }
        }
    }

    public bool SysPowerPlan
    {
        get => Sys.PowerPlan;
        set
        {
            if (value == Sys.PowerPlan) return;
            // No plan chosen: High performance, if it exists; otherwise the first one that isn't active.
            var plans = PowerPlanOptions;
            var pick = Sys.PowerPlanId ?? plans.FirstOrDefault(p => p.Id == HighPerformance)?.Id ?? plans.FirstOrDefault(p => !p.IsActive)?.Id;
            SetSystem(s => { s.PowerPlan = value; s.PowerPlanId ??= pick; }, value ? "Gamemode switches the power plan." : "Gamemode leaves the power plan alone.");
        }
    }

    public Guid? SysPowerPlanId
    {
        get => Sys.PowerPlanId;
        set { if (value is { } id && id != Sys.PowerPlanId) SetSystem(s => s.PowerPlanId = id, "Gamemode power plan saved."); }
    }

    public bool SysStopUpdate
    {
        get => Sys.StopWindowsUpdate;
        set { if (value != Sys.StopWindowsUpdate) SetSystem(s => s.StopWindowsUpdate = value, "Saved."); }
    }

    public bool SysStopSearch
    {
        get => Sys.StopSearch;
        set { if (value != Sys.StopSearch) SetSystem(s => s.StopSearch = value, "Saved."); }
    }

    public bool SysTransparency
    {
        get => Sys.TransparencyOff;
        set { if (value != Sys.TransparencyOff) SetSystem(s => s.TransparencyOff = value, "Saved."); }
    }

    public bool SysCStates
    {
        get => Sys.CStatesOff;
        set { if (value != Sys.CStatesOff) SetSystem(s => s.CStatesOff = value, "Saved."); }
    }

    public bool SysCleanMemory
    {
        get => Sys.CleanMemory;
        set { if (value != Sys.CleanMemory) SetSystem(s => s.CleanMemory = value, "Saved."); }
    }

    public sealed record IntOption(string Label, int Value);

    public IReadOnlyList<IntOption> CleanDelayOptions { get; } = new[] { 0, 15, 30, 60, 120, 300 }
        .Select(s => new IntOption(s == 0 ? "Right away" : s % 60 == 0 ? $"{s / 60} min" : $"{s} s", s)).ToList();

    public int SysCleanDelay
    {
        get => Sys.CleanMemoryDelaySeconds;
        set { if (value != Sys.CleanMemoryDelaySeconds) SetSystem(s => s.CleanMemoryDelaySeconds = value, "Saved."); }
    }

    public IReadOnlyList<int> MemoryMbOptions { get; } = new[] { 512, 1024, 2048, 4096, 8192 };

    public bool SysPurgeStandby
    {
        get => Sys.PurgeStandby;
        set { if (value != Sys.PurgeStandby) SetSystem(s => s.PurgeStandby = value, "Saved."); }
    }

    public int SysPurgeStandbyMb
    {
        get => Sys.PurgeStandbyMb;
        set { if (value != Sys.PurgeStandbyMb) SetSystem(s => s.PurgeStandbyMb = value, "Saved."); }
    }

    public int SysPurgeFreeMb
    {
        get => Sys.PurgeFreeMb;
        set { if (value != Sys.PurgeFreeMb) SetSystem(s => s.PurgeFreeMb = value, "Saved."); }
    }

    public bool SysRestrain
    {
        get => Sys.RestrainBackground;
        set { if (value != Sys.RestrainBackground) SetSystem(s => s.RestrainBackground = value, "Saved."); }
    }

    public bool SysKeepAwake
    {
        get => Sys.KeepAwake;
        set { if (value != Sys.KeepAwake) SetSystem(s => s.KeepAwake = value, "Saved."); }
    }

    public bool SysDwmMmcss
    {
        get => Sys.DwmMmcss;
        set { if (value != Sys.DwmMmcss) SetSystem(s => s.DwmMmcss = value, "Saved."); }
    }

    public bool SysRtssOsd
    {
        get => Sys.RtssOsd;
        set { if (value != Sys.RtssOsd) SetSystem(s => s.RtssOsd = value, "Saved."); }
    }

    static readonly (RtssOsdItems Item, string Label)[] OsdItems =
    {
        (RtssOsdItems.Fps, "FPS"), (RtssOsdItems.FrameTime, "Frame time"), (RtssOsdItems.FrametimeGraph, "Frame time graph"),
        (RtssOsdItems.GpuLoad, "GPU load"), (RtssOsdItems.GpuTemperature, "GPU temperature"), (RtssOsdItems.GpuClock, "GPU clock"),
        (RtssOsdItems.GpuPower, "GPU power"), (RtssOsdItems.Vram, "VRAM"), (RtssOsdItems.Cpu, "CPU load"), (RtssOsdItems.Ram, "Memory"),
    };

    /// <summary>One check box per item of the OSD (the GPU ones need an NVIDIA card).</summary>
    public IReadOnlyList<ColumnOption> RtssOsdOptions => OsdItems.Select(o => new ColumnOption(o.Label, (Sys.RtssOsdItems & o.Item) != 0,
        (_, on) => SetSystem(s => s.RtssOsdItems = on ? s.RtssOsdItems | o.Item : s.RtssOsdItems & ~o.Item, "Saved."))).ToList();

    /// <summary>The frame limit as typed: empty or 0 keeps RTSS's own; the range is checked by <see cref="Validation"/> when saving.</summary>
    public string SysRtssFrameLimit
    {
        get => Sys.RtssFrameLimit == 0 ? "" : Sys.RtssFrameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            var text = value?.Trim() ?? "";
            if (!int.TryParse(text.Length == 0 ? "0" : text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var fps))
            {
                _main.Report("Type the frame limit as a whole number of FPS (10–1000), or leave it empty for RTSS's own.", true);
                Raise(nameof(SysRtssFrameLimit));
                return;
            }
            if (fps != Sys.RtssFrameLimit) SetSystem(s => s.RtssFrameLimit = fps, fps == 0 ? "Saved: RTSS's own frame limit stays." : $"Saved: {fps} FPS while gamemode is on.");
            else Raise(nameof(SysRtssFrameLimit)); // "0141" shows as 141 again
        }
    }

    public sealed record LimiterOption(string Label, RtssLimiterMode Value);

    public IReadOnlyList<LimiterOption> LimiterModeOptions { get; } = new LimiterOption[]
    {
        new("RTSS's own setting", RtssLimiterMode.Own), new("Async", RtssLimiterMode.Async), new("Front edge sync", RtssLimiterMode.FrontEdgeSync),
        new("Back edge sync", RtssLimiterMode.BackEdgeSync), new("NVIDIA Reflex", RtssLimiterMode.Reflex),
    };

    public RtssLimiterMode SysRtssLimiterMode
    {
        get => Sys.RtssLimiterMode;
        set { if (value != Sys.RtssLimiterMode) SetSystem(s => s.RtssLimiterMode = value, "Saved."); }
    }

    public bool SysDirector
    {
        get => Sys.ThreadDirector;
        set { if (value != Sys.ThreadDirector) SetSystem(s => s.ThreadDirector = value, "Saved."); }
    }

    public IReadOnlyList<int> HeavyThreadOptions { get; } = new[] { 1, 2, 3, 4 };

    public int SysHeavyThreads
    {
        get => Sys.HeavyThreads;
        set { if (value != Sys.HeavyThreads) SetSystem(s => s.HeavyThreads = value, "Saved."); }
    }

    public bool SysMoveBackground
    {
        get => Sys.MoveBackground;
        set { if (value != Sys.MoveBackground) SetSystem(s => s.MoveBackground = value, "Saved."); }
    }

    public bool SysNormalizePriorities
    {
        get => Sys.NormalizePriorities;
        set { if (value != Sys.NormalizePriorities) SetSystem(s => s.NormalizePriorities = value, "Saved."); }
    }

    public bool SysAvoidInterrupts
    {
        get => Sys.AvoidInterrupts;
        set { if (value != Sys.AvoidInterrupts) SetSystem(s => s.AvoidInterrupts = value, "Saved."); }
    }

    public bool SysWifi
    {
        get => Sys.WifiLowLatency;
        set { if (value != Sys.WifiLowLatency) SetSystem(s => s.WifiLowLatency = value, "Saved."); }
    }

    public bool SysAudio
    {
        get => Sys.LowLatencyAudio;
        set { if (value != Sys.LowLatencyAudio) SetSystem(s => s.LowLatencyAudio = value, "Saved."); }
    }

    // ---------- timer resolution (all profiles) ----------

    public sealed record TimerModeOption(string Label, TimerMode Value);

    public IReadOnlyList<TimerModeOption> TimerModeOptions { get; } = new TimerModeOption[]
    {
        new("Off (Windows decides)", Core.TimerMode.Off), new("While gamemode is on", Core.TimerMode.Gamemode), new("Always", Core.TimerMode.Always),
    };

    public IReadOnlyList<IntOption> TimerResolutionOptions => new[] { 5000, 5040, 5070, 5100, 5200, 5500, 6000, 7500, 10000 }
        .Union(new[] { _config.Timer.Resolution }).Order().Select(v => new IntOption($"{v / 10000.0:0.0###} ms", v)).ToList();

    public TimerMode TimerMode
    {
        get => _config.Timer.Mode;
        set
        {
            if (value == _config.Timer.Mode) return;
            Mutate(c => c.Timer.Mode = value, value switch
            {
                Core.TimerMode.Off => "Timer resolution left to Windows.",
                Core.TimerMode.Gamemode => "The engine raises the timer resolution while gamemode is on.",
                _ => "The engine keeps the timer resolution raised.",
            });
        }
    }

    public int TimerResolution
    {
        get => _config.Timer.Resolution;
        set { if (value != _config.Timer.Resolution) Mutate(c => c.Timer.Resolution = value, "Saved."); }
    }

    public bool TimerOn => _config.Timer.Mode != Core.TimerMode.Off;

    /// <summary>Without "Global timer resolution requests", Windows 11 applies a process's request to that process only.</summary>
    public string? TimerNote => TimerOn && !Core.TimerResolution.GlobalRequests
        ? "\"Global timer resolution requests\" is off (KOS Toolbox › Windows › Performance): Windows only applies the engine's request to the engine itself. Turn it on there and restart for the timer to apply to games."
        : null;

    public bool SysBackgroundEfficiency
    {
        get => Sys.BackgroundEfficiency;
        set { if (value != Sys.BackgroundEfficiency) SetSystem(s => s.BackgroundEfficiency = value, "Saved."); }
    }

    public sealed record CoresOption(string Label, string Spec);

    /// <summary>This CPU's presets and the saved masks (the same choices as a rule's CPU sets, without "All" and "None").</summary>
    public IReadOnlyList<CoresOption> GameCoresOptions =>
        CpuSpec.Presets(_main.Topology).Select(p => new CoresOption(p.Label, p.Spec))
            .Concat(_config.Masks.Select(m => new CoresOption($"Mask: {m.Name}", CpuSpec.MaskPrefix + m.Name))).ToList();

    public bool SysGameCores
    {
        get => Sys.GameCores;
        set
        {
            if (value == Sys.GameCores) return;
            // Nothing chosen yet: the V-Cache CCD, else the P-cores, else the first option.
            var options = GameCoresOptions;
            var pick = Sys.GameCoresSpec ?? options.FirstOrDefault(o => o.Spec == CpuSpec.VCache)?.Spec ?? options.FirstOrDefault(o => o.Spec == CpuSpec.PCores)?.Spec
                ?? options.FirstOrDefault()?.Spec;
            if (value && pick is null) { _main.Report("This processor has no core groups to pick; make a mask first."); Raise(nameof(SysGameCores)); return; }
            SetSystem(s => { s.GameCores = value; s.GameCoresSpec ??= pick; }, value ? "Games get these cores in gamemode." : "Games keep all cores in gamemode.");
        }
    }

    public string? SysGameCoresSpec
    {
        get => Sys.GameCoresSpec;
        set { if (value is not null && value != Sys.GameCoresSpec) SetSystem(s => s.GameCoresSpec = value, "Saved."); }
    }

    GameListWindow? _gameList;

    /// <summary>Full list (single window; clicking again brings it to the front).</summary>
    void ShowGameList()
    {
        if (_gameList is { IsLoaded: true }) { _gameList.Activate(); return; }
        _gameList = new GameListWindow(_main) { Owner = Application.Current.MainWindow };
        _gameList.Closed += (_, _) => _gameList = null;
        _gameList.Show();
    }

    void AddGame()
    {
        var (ok, name) = Dialog.Prompt("Add game", "Program name of the game (as in Task Manager › Details), like game.exe.", "", Validation.GameNameProblem);
        if (ok) SetGame(name, true);
    }

    public int GuiInterval
    {
        get => _config.General.GuiIntervalMs;
        set { if (value != _config.General.GuiIntervalMs) Mutate(c => c.General.GuiIntervalMs = value); }
    }

    public int EngineInterval
    {
        get => _config.General.EngineIntervalMs;
        set { if (value != _config.General.EngineIntervalMs) Mutate(c => c.General.EngineIntervalMs = value); }
    }

    public bool SessionReport
    {
        get => _config.General.SessionReport;
        set { if (value != _config.General.SessionReport) Mutate(c => c.General.SessionReport = value); }
    }

    public string? SignInProblem => SignIn.FolderProblem(Environment.ProcessPath ?? "");
    public bool CanStartWithWindows => SignInProblem is null || StartWithWindows;

    public bool StartWithWindows
    {
        get => _signIn ??= SafeEnabled();
        set
        {
            try
            {
                if (value) SignIn.Enable(); else SignIn.Disable();
                _main.Report(value ? "KOS Governor starts with Windows (rules apply after sign-in)." : "KOS Governor no longer starts with Windows.");
            }
            catch (Exception ex) { _main.Report(ex.Message, true); }
            _signIn = SafeEnabled();
            Raise();
        }
    }

    static bool SafeEnabled()
    {
        try { return SignIn.Enabled(); }
        catch { return false; }
    }
}
