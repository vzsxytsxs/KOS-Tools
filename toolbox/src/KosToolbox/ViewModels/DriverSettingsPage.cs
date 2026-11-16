using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>Value in the editor list of a setting ("Driver default" has Value null).</summary>
public sealed record DrsOptionItem(uint? Value, string Label, string Hex, bool IsCurrent);

/// <summary>A bit in the editor's Bitmask mode.</summary>
public sealed class DrsBitItem(int bit, DrsRow row) : ObservableObject
{
    public int Bit { get; } = bit;
    public string Label => $"Bit {Bit}";
    public string Hex => $"0x{1u << Bit:X8}";
    public bool IsOn
    {
        get => (row.EffectiveDword & (1u << Bit)) != 0;
        set { if (value != IsOn) row.Choose(row.EffectiveDword ^ (1u << Bit), keepOpen: true); }
    }
    internal void Refresh() => Raise(nameof(IsOn));
}

/// <summary>
/// A row of the DRS tab: value stored in the profile (own), inherited from the global profile, or the driver default; and the
/// pending change (until Apply).
/// </summary>
public sealed class DrsRow : ObservableObject
{
    readonly DriverSettingsViewModel _owner;
    DrsStored? _own, _inherited;
    (bool Restore, uint Dword, string? Text)? _pending;
    bool _editing, _bitmask, _onGlobal;
    string _custom = "";
    IReadOnlyList<DrsBitItem>? _bits;

    public DrsRow(DrsMeta meta, DriverSettingsViewModel owner, bool unsupported = false, bool notOnGlobal = false)
    {
        Meta = meta;
        Unsupported = unsupported;
        NotOnGlobal = notOnGlobal;
        _owner = owner;
        ChooseCommand = new RelayCommand(p => { if (p is DrsOptionItem o) { if (o.Value is { } v) Choose(v, false); else RestoreDefault(); } });
        CustomCommand = new RelayCommand(ApplyCustom);
        ResetCommand = new RelayCommand(RestoreDefault);
    }

    public DrsMeta Meta { get; }
    public string Name => Meta.Name;
    public string Group => Meta.Group;
    public string Description => Meta.Description.Length > 0 ? Meta.Description : $"0x{Meta.Id:X8}";
    public string Tooltip => $"{Meta.Name} (0x{Meta.Id:X8})\n{Meta.Description}"
        + (Unsupported ? "\n\nThe installed driver doesn't have this setting (another GPU family or driver version), so it can't be written."
            : NotOnGlobal ? "\n\nThe driver accepts this setting only in game profiles, not in the global profile." : "");
    /// <summary>The installed driver rejects this setting (NVAPI_SETTING_NOT_FOUND): it cannot be edited.</summary>
    public bool Unsupported { get; }
    /// <summary>The driver rejects it in the global profile (NVAPI_NOT_SUPPORTED) but accepts it in game profiles (e.g. Ansel).</summary>
    public bool NotOnGlobal { get; }
    bool BlockedHere => Unsupported || (NotOnGlobal && _onGlobal);
    public bool CanEdit => !IsBinary && !BlockedHere;
    public ICommand ChooseCommand { get; }
    public ICommand CustomCommand { get; }
    public ICommand ResetCommand { get; }

    internal DrsStored? Own => _own;
    internal (bool Restore, uint Dword, string? Text)? Pending => _pending;
    public bool IsPending => _pending is not null;
    /// <summary>Changed by the user in this profile (green dot).</summary>
    public bool IsModified => _pending is null && _own is { IsCurrentPredefined: false };
    /// <summary>Value NVIDIA sets in this profile (gray dot).</summary>
    public bool IsPredefined => _pending is null && _own is { IsCurrentPredefined: true };
    public bool IsInherited => _pending is null && _own is null && _inherited is not null;
    public bool IsString => Meta.IsString || (_own ?? _inherited)?.Type is DrsType.String or DrsType.WString;
    public bool IsBinary => (_own ?? _inherited)?.Type == DrsType.Binary;

    internal uint EffectiveDword => _pending is { Restore: false } p ? p.Dword : _pending is { Restore: true } ? Meta.Default ?? 0 : (_own ?? _inherited)?.Dword ?? Meta.Default ?? 0;

    public string ValueText
    {
        get
        {
            if (BlockedHere && _own is null && _pending is null) return Unsupported ? "Not supported by this driver" : "Game profiles only";
            if (_pending is { Restore: true }) return "Driver default";
            if (_pending is { } p) return IsString ? p.Text ?? "" : Label(p.Dword);
            if ((_own ?? _inherited) is { } s) return s.Type == DrsType.Dword ? Label(s.Dword) : s.Text ?? "";
            return "Driver default";
        }
    }

    string Label(uint v) => Meta.Options.FirstOrDefault(o => o.Value == v)?.Label is { Length: > 0 } l ? l : $"0x{v:X8}";

    public IReadOnlyList<DrsOptionItem> Options
    {
        get
        {
            var hasValue = _pending is { Restore: false } || (_pending is null && (_own ?? _inherited) is not null);
            var current = EffectiveDword;
            var list = new List<DrsOptionItem> { new(null, "Driver default", $"(0x{Meta.Default ?? 0:X8})", !hasValue) };
            list.AddRange(Meta.Options.Select(o => new DrsOptionItem(o.Value, o.Label.Length > 0 ? o.Label : $"0x{o.Value:X8}", $"(0x{o.Value:X8})", hasValue && o.Value == current)));
            return list;
        }
    }

    public bool IsEditing
    {
        get => _editing;
        set
        {
            if (!Set(ref _editing, value)) return;
            if (value) { _custom = IsString ? ValueText : $"0x{EffectiveDword:X8}"; Raise(nameof(CustomValue)); Raise(nameof(Options)); }
        }
    }
    public string CustomValue { get => _custom; set => Set(ref _custom, value ?? ""); }
    public bool Bitmask { get => _bitmask; set { if (Set(ref _bitmask, value)) Raise(nameof(Bits)); } }
    public bool CanBitmask => !IsString && !IsBinary;
    public IReadOnlyList<DrsBitItem> Bits => _bits ??= Enumerable.Range(0, 32).Select(b => new DrsBitItem(b, this)).ToList();

    internal void Load(DrsStored? own, DrsStored? inherited, bool onGlobal)
    {
        _onGlobal = onGlobal;
        _own = own;
        _inherited = inherited;
        _pending = null;
        RaiseValue();
    }

    internal void ClearPending() { _pending = null; RaiseValue(); }

    internal void StagePending(uint dword, string? text)
    {
        _pending = (false, dword, text);
        RaiseValue();
    }

    internal void Choose(uint value, bool keepOpen)
    {
        if (IsBinary) return;
        _pending = _own is { Type: DrsType.Dword } own && own.Dword == value ? null : (false, value, null);
        if (!keepOpen) IsEditing = false;
        RaiseValue();
        _owner.PendingChanged();
    }

    void RestoreDefault()
    {
        _pending = _own is null ? null : (true, 0, null);
        IsEditing = false;
        RaiseValue();
        _owner.PendingChanged();
    }

    void ApplyCustom()
    {
        var text = _custom.Trim();
        if (IsString)
        {
            _pending = _own?.Text == text ? null : (false, 0, text);
            IsEditing = false;
            RaiseValue();
            _owner.PendingChanged();
            return;
        }
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            : uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (!ok) { _owner.ReportError("Enter a number: decimal (240) or hexadecimal (0xF0)."); return; }
        Choose(v, false);
    }

    void RaiseValue()
    {
        foreach (var n in new[] { nameof(ValueText), nameof(IsPending), nameof(IsModified), nameof(IsPredefined), nameof(IsInherited), nameof(Options), nameof(CanEdit) }) Raise(n);
        if (_bits is not null) foreach (var b in _bits) b.Refresh();
    }
}

/// <summary>
/// DRS tab of the GPU page, like NVIDIA Profile Inspector: driver profiles (global and per-game), settings by
/// group with search and filters, editing with known values, a custom value or bits, and Apply to write to the driver.
/// </summary>
public sealed class DriverSettingsViewModel : ObservableObject
{
    DrsSession? _session;
    List<DrsProfile> _profiles = new();
    DrsProfile? _profile;
    List<DrsMeta> _catalog = new();
    string _search = "", _filter = "all", _profileQuery = "";
    bool _profilesOpen, _busy, _loaded, _isError;
    string? _message;

    public DriverSettingsViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DrsRow.Group)));
        View.Filter = o => o is DrsRow r && Visible(r);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !_busy && PendingCount > 0);
        UndoCommand = new RelayCommand(() => { foreach (var r in Rows) r.ClearPending(); PendingChanged(); }, () => PendingCount > 0);
        RestoreAllCommand = new AsyncCommand(RestoreAllAsync, () => !_busy && _session is not null);
        ReloadCommand = new AsyncCommand(() => LoadAsync(force: true), () => !_busy);
        ImportCommand = new RelayCommand(Import, () => _profile is not null && !_busy);
        ExportCommand = new RelayCommand(Export, () => _profile is not null && !_busy);
        ProgramProfileCommand = new AsyncCommand(ProgramProfileAsync, () => _session is not null && !_busy);
        DeleteProfileCommand = new AsyncCommand(DeleteProfileAsync, () => _session is not null && !_busy && _profile is { IsGlobal: false, Predefined: false });
        OpenInspectorCommand = new RelayCommand(() =>
        {
            try { ProfileInspector.Open(ProfileInspector.Exe); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* UAC cancelled */ }
            catch (Exception ex) { Report(ex.Message, true); }
        }, () => ProfileInspector.Available);
    }

    public ObservableCollection<DrsRow> Rows { get; } = new();
    public ICollectionView View { get; }
    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenInspectorCommand { get; }
    public ICommand ProgramProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }

    /// <summary>The programs the selected profile applies to ("" for the global profile).</summary>
    public string ProfilePrograms { get => _programs; private set => Set(ref _programs, value); }
    string _programs = "";

    /// <summary>Opens the profile of a chosen program, making one named after its file when the driver has none.</summary>
    async Task ProgramProfileAsync()
    {
        if (_session is not { } s) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Driver profile for a program" };
        if (dialog.ShowDialog() != true) return;
        if (PendingCount > 0) Toasts.Show(Txt.PendingDiscarded(PendingCount), ToastKind.Info);
        try
        {
            // Busy while the driver session changes, so Reload can't close it midway; LoadAsync below needs it free again.
            IsBusy = true;
            (string name, bool created) = (null!, false);
            try { (name, created) = await Task.Run(() => DriverSettings.ProfileForProgram(s, dialog.FileName)); }
            finally { IsBusy = false; }
            if (created) Log.Info($"Driver settings: profile '{name}' created for {dialog.FileName}");
            await LoadAsync(force: true, select: name);
            if (_profile?.Name != name) { Report($"The profile \"{name}\" wasn't found after reading the driver again.", true); return; }
            var file = System.IO.Path.GetFileName(dialog.FileName);
            Report(created ? $"New profile \"{name}\" for {file}: its settings apply only to this program (the rest come from the global profile)."
                           : $"{file} already has a driver profile: \"{name}\". Its settings apply only to this program and the others in it.", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Driver settings: profile for {dialog.FileName} failed: {ex.Message}");
            Report(ex.Message + (Elevation.IsAdmin ? "" : " (the toolbox needs to run as administrator)"), true);
        }
    }

    async Task DeleteProfileAsync()
    {
        if (_session is not { } s || _profile is not { IsGlobal: false, Predefined: false } p) return;
        try
        {
            IsBusy = true;
            try { await Task.Run(() => DriverSettings.DeleteProfile(s, p)); }
            finally { IsBusy = false; }
            Log.Info($"Driver settings: profile '{p.Name}' deleted");
            _profile = null;
            await LoadAsync(force: true);
            Report($"Profile \"{p.Name}\" deleted: its programs use the global profile again.", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Driver settings: deleting profile '{p.Name}' failed: {ex.Message}");
            Report(ex.Message, true);
        }
    }
    public ICommand OpenProfilesCommand => new RelayCommand(() =>
    {
        // Full list: clears the search if it only shows the current profile.
        if (_profile is not null && _profileQuery == _profile.ToString()) { _profileQuery = ""; Raise(nameof(ProfileQuery)); Raise(nameof(FilteredProfiles)); }
        ProfilesOpen = true;
    });

    public IReadOnlyList<Choice> Filters { get; } = new[] { new Choice(0, "All settings"), new Choice(1, "Modified"), new Choice(2, "Pending"), new Choice(3, "Hidden settings") };
    public int Filter
    {
        get => _filter switch { "modified" => 1, "pending" => 2, "hidden" => 3, _ => 0 };
        set { _filter = value switch { 1 => "modified", 2 => "pending", 3 => "hidden", _ => "all" }; Raise(); View.Refresh(); Raise(nameof(LoadedText)); }
    }
    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) { View.Refresh(); Raise(nameof(LoadedText)); } } }
    public int PendingCount => Rows.Count(r => r.IsPending);
    public string LoadedText => _loaded ? $"Loaded {Rows.Count(r => !r.Meta.Hidden)} settings" + (PendingCount > 0 ? $" · {PendingCount} pending" : "") : "Loading driver settings…";
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }

    bool Visible(DrsRow r)
    {
        var show = _filter switch
        {
            "modified" => r.Own is not null || r.IsPending,
            "pending" => r.IsPending,
            "hidden" => r.Meta.Hidden,
            _ => !r.Meta.Hidden,
        };
        if (!show || _search.Length == 0) return show;
        return MainViewModel.Matches(r.Name, _search) || MainViewModel.Matches(r.Meta.Description, _search)
            || r.Meta.Id.ToString("X8").Contains(_search.Replace("0x", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- profiles ----------
    public DrsProfile? SelectedProfile
    {
        get => _profile;
        set
        {
            // Null comes from the list when filtering (the current profile left the results): it is not a choice and closes nothing.
            if (value is null) return;
            if (value == _profile) { ProfilesOpen = false; return; }
            if (PendingCount > 0) Toasts.Show(Txt.PendingDiscarded(PendingCount), ToastKind.Info);
            _profile = value;
            _profileQuery = value.ToString();
            ProfilesOpen = false;
            Raise();
            Raise(nameof(ProfileQuery));
            Raise(nameof(FilteredProfiles));
            MainViewModel.Fire(LoadValuesAsync());
        }
    }
    public string ProfileQuery
    {
        get => _profileQuery;
        set
        {
            if (!Set(ref _profileQuery, value ?? "")) return;
            if (_profile is not null && _profileQuery == _profile.ToString()) return;
            Raise(nameof(FilteredProfiles));
            ProfilesOpen = true;
        }
    }
    public bool ProfilesOpen
    {
        get => _profilesOpen;
        set
        {
            if (!Set(ref _profilesOpen, value) || value || _profile is null || _profileQuery == _profile.ToString()) return;
            // Closed without choosing: the field shows the current profile again.
            _profileQuery = _profile.ToString();
            Raise(nameof(ProfileQuery));
            Raise(nameof(FilteredProfiles));
        }
    }
    public IReadOnlyList<DrsProfile> FilteredProfiles
    {
        get
        {
            var q = _profileQuery.Trim();
            if (q.Length == 0 || _profile is not null && q == _profile.ToString()) return _profiles;
            return _profiles.Where(p => MainViewModel.Matches(p.ToString(), q)).Take(500).ToList();
        }
    }

    // ---------- reading ----------
    /// <summary>Opens the driver session, the catalog and the profiles (on the first visit to the tab or on Reload).</summary>
    public async Task LoadAsync(bool force = false, string? select = null)
    {
        if (_busy || (_loaded && !force)) return;
        IsBusy = true;
        try
        {
            var keep = select ?? _profile?.Name;
            var (session, profiles, catalog, unsupported) = await Task.Run(() =>
            {
                _session?.Dispose();
                var s = DriverSettings.Open();
                var cat = _catalog.Count > 0 ? _catalog : DriverSettings.Catalog(ProfileInspector.Exe);
                (HashSet<uint> NotInDriver, HashSet<uint> NotOnGlobal) no;
                try { no = DriverSettings.Unsupported(cat); }
                catch (Exception ex) { Log.Warn($"Driver settings: checking which settings this driver has failed: {ex.Message}"); no = (new(), new()); }
                return (s, DriverSettings.Profiles(s), cat, no);
            });
            _session = session;
            _profiles = profiles;
            _catalog = catalog;
            Rows.Clear();
            foreach (var m in _catalog) Rows.Add(new DrsRow(m, this, unsupported.NotInDriver.Contains(m.Id), unsupported.NotOnGlobal.Contains(m.Id)));
            _profile = _profiles.FirstOrDefault(p => p.Name == keep) ?? _profiles.FirstOrDefault();
            _profileQuery = _profile?.ToString() ?? "";
            Raise(nameof(SelectedProfile));
            Raise(nameof(ProfileQuery));
            Raise(nameof(FilteredProfiles));
            await LoadValuesAsync();
            _loaded = true;
            if (force && select is null) Report("Driver settings reloaded.", false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Driver settings: {ex.Message}");
            Report(ex.Message, true);
        }
        finally { IsBusy = false; PendingChanged(); }
    }

    async Task LoadValuesAsync()
    {
        if (_session is not { } s || _profile is not { } p) return;
        var global = _profiles.FirstOrDefault(x => x.IsGlobal);
        var (own, inherited, programs) = await Task.Run(() =>
            (DriverSettings.Stored(s, p), global is not null && !p.IsGlobal ? DriverSettings.Stored(s, global) : new Dictionary<uint, DrsStored>(), Programs(s, p)));
        ProfilePrograms = programs;
        foreach (var r in Rows) r.Load(own.GetValueOrDefault(r.Meta.Id), inherited.GetValueOrDefault(r.Meta.Id), p.IsGlobal);
        View.Refresh();
        PendingChanged();
    }

    static string Programs(DrsSession s, DrsProfile p)
    {
        if (p.IsGlobal) return "";
        try
        {
            var list = DriverSettings.Programs(s, p);
            return list.Count == 0 ? "No program in this profile." : "Programs: " + string.Join(", ", list.Take(12)) + (list.Count > 12 ? $" and {list.Count - 12} more" : "");
        }
        catch (Exception ex) { return $"Programs couldn't be read: {ex.Message}"; }
    }

    internal void PendingChanged()
    {
        Raise(nameof(PendingCount));
        Raise(nameof(LoadedText));
        if (_filter is "pending" or "modified") View.Refresh();
        CommandManager.InvalidateRequerySuggested();
    }

    internal void ReportError(string message) => Report(message, true);
    void Report(string? message, bool error) { Message = message; IsError = error; }

    // ---------- writing ----------
    async Task ApplyAsync()
    {
        if (_session is not { } s || _profile is not { } p) return;
        var changes = Rows.Where(r => r.IsPending).Select(r => (r.Meta.Id, r.Meta.Name, r.IsString, Pending: r.Pending!.Value)).ToList();
        if (changes.Count == 0) return;
        IsBusy = true;
        string? error = null;
        List<string> notInDriver = new(), notHere = new();
        try
        {
            await Task.Run(() =>
            {
                // What the driver rejects is left out (and named in the message); the rest is written.
                foreach (var c in changes)
                {
                    if (c.Pending.Restore) { DriverSettings.RestoreDefault(s, p, c.Id); continue; }
                    var r = DriverSettings.Set(s, p, c.Id, c.IsString, c.Pending.Dword, c.Pending.Text);
                    if (r == DrsWrite.NotInDriver) notInDriver.Add(c.Name);
                    else if (r == DrsWrite.NotOnThisProfile) notHere.Add(c.Name);
                }
                if (notInDriver.Count + notHere.Count < changes.Count) DriverSettings.Save(s);
            });
            var applied = changes.Select(c => c.Name).Except(notInDriver).Except(notHere).ToList();
            if (applied.Count > 0) Log.Info($"Driver settings applied to '{p.Name}': {string.Join(", ", applied)}");
            if (notInDriver.Count + notHere.Count > 0) Log.Warn($"Driver settings refused by the driver on '{p.Name}': {string.Join(", ", notInDriver.Concat(notHere))}");
        }
        catch (Exception ex)
        {
            error = ex.Message + (Elevation.IsAdmin ? "" : " (the toolbox needs to run as administrator)");
            Log.Warn($"Driver settings apply failed: {ex.Message}");
        }
        finally { IsBusy = false; }
        if (error is not null)
        {
            // What was written before the error stayed only in the session (no Save): reopen to discard it and show what the driver has.
            await LoadAsync(force: true);
            Report(error, true);
            return;
        }
        await LoadValuesAsync();
        var refused = string.Join(" ", new[]
        {
            notInDriver.Count > 0 ? $"Not in the installed driver (another GPU family or driver version): {string.Join(", ", notInDriver)}." : null,
            notHere.Count > 0 ? $"Accepted only in game profiles, not in the global profile: {string.Join(", ", notHere)}." : null,
        }.OfType<string>());
        var count = changes.Count - notInDriver.Count - notHere.Count;
        Report(count == 0 ? refused : $"{count} driver setting(s) applied to {p}. {refused}".Trim(), refused.Length > 0);
    }

    async Task RestoreAllAsync()
    {
        if (_session is not { } s) return;
        IsBusy = true;
        string? error = null;
        try { await Task.Run(() => { DriverSettings.RestoreAll(s); DriverSettings.Save(s); }); }
        catch (Exception ex) { error = ex.Message; }
        finally { IsBusy = false; }
        await LoadAsync(force: true);
        Report(error ?? "All driver settings restored to NVIDIA defaults.", error is not null);
    }

    void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "NVIDIA Profile Inspector (*.nip)|*.nip", Title = "Import driver settings" };
        if (dialog.ShowDialog() != true || _profile is not { } p) return;
        try
        {
            var profiles = DriverSettings.ImportNip(dialog.FileName);
            // The profile with the same name; in a single-profile file (the common case), that one.
            var source = profiles.FirstOrDefault(x => x.Profile == p.Name);
            if (source.Settings is null && profiles.Count == 1) source = profiles[0];
            if (source.Settings is null) { Report($"The file has no settings for {p} (profiles in it: {string.Join(", ", profiles.Select(x => x.Profile))}).", true); return; }
            var staged = 0;
            foreach (var setting in source.Settings)
                if (Rows.FirstOrDefault(r => r.Meta.Id == setting.Id) is { } row && !row.IsBinary)
                {
                    row.StagePending(setting.Dword, setting.Text);
                    staged++;
                }
            PendingChanged();
            Report($"Imported {staged} setting(s) into {p} as pending changes. Press Apply to write them.", false);
        }
        catch (Exception ex) { Report($"Couldn't import the file: {ex.Message}", true); }
    }

    void Export()
    {
        if (_profile is not { } p) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "NVIDIA Profile Inspector (*.nip)|*.nip", FileName = $"{(p.IsGlobal ? "Base Profile" : p.Name)}.nip", Title = "Export driver settings" };
        if (dialog.ShowDialog() != true) return;
        var settings = Rows.Where(r => r.IsPending ? r.Pending is { Restore: false } : r.IsModified)
            .Select(r => r.Pending is { Restore: false } pe
                ? new DriverSettings.NipSetting(r.Meta.Id, r.Name, r.IsString, pe.Dword, pe.Text)
                : new DriverSettings.NipSetting(r.Meta.Id, r.Name, r.IsString, r.Own!.Dword, r.Own.Text)).ToList();
        try
        {
            DriverSettings.ExportNip(dialog.FileName, p.Name, settings);
            Report($"Exported {settings.Count} setting(s) to {System.IO.Path.GetFileName(dialog.FileName)}.", false);
        }
        catch (Exception ex) { Report(ex.Message, true); }
    }
}
