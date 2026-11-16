using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>
/// Everything the GPU page lets you adjust, in absolute values (not "pending relative to the card"): serves as an undo step and
/// as a saved profile. Absolute values stay correct after an Apply, when the card has already changed.
/// </summary>
public sealed class GpuProfile
{
    public string Name { get; set; } = "";
    public string? Power { get; set; }
    public string? Thermal { get; set; }
    public bool? ThermalSlowdown { get; set; }
    public string? Core { get; set; }
    public string? Mem { get; set; }
    public string? Fan { get; set; }
    public string FanMode { get; set; } = "auto";
    /// <summary>
    /// Offset (kHz) of each V/F point by index, as the card shows it with the profile's core offset (the offset
    /// is already in the points); only restored on a curve with the same number of points.
    /// </summary>
    public Dictionary<int, int>? VfDeltas { get; set; }
    public int? VfLock { get; set; }
    public int? VfClockLock { get; set; }
    public bool VfActive { get; set; }
    public List<FanPoint>? Curve { get; set; }
    public string? Hysteresis { get; set; }
}

/// <summary>Per-user saved profiles (%LocalAppData%\KOS\Toolbox\gpu-profiles.json).</summary>
public sealed class GpuProfileStore
{
    public List<GpuProfile> Profiles { get; set; } = new();
    /// <summary>Applied automatically when the toolbox opens.</summary>
    public string? Selected { get; set; }
    /// <summary>The apply-on-open started and did not finish: on the next open it is skipped (the profile may be crashing the card).</summary>
    public bool StartApplyUnfinished { get; set; }

    static string FilePath => System.IO.Path.Combine(AppPaths.DataRoot, "gpu-profiles.json");
    public static GpuProfileStore Load() => JsonFile.Load<GpuProfileStore>(FilePath);
    public void Save() => JsonFile.Save(FilePath, this);
}

public sealed partial class GpuPageViewModel
{
    const int UndoLimit = 50;
    static readonly TimeSpan UndoBurst = TimeSpan.FromMilliseconds(700);
    readonly List<GpuProfile> _undo = new();
    GpuProfile? _lastSnapshot;
    DateTime _lastChange;
    bool _restoring;
    GpuProfileStore _profiles = new();
    string? _selectedProfile;
    bool _profileLoadedOnOpen;

    public ObservableCollection<string> ProfileNames { get; } = new();
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand NewProfileCommand { get; private set; } = null!;
    public ICommand SaveProfileCommand { get; private set; } = null!;
    public ICommand DeleteProfileCommand { get; private set; } = null!;
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Choosing a profile marks its values (as edits); the card only changes with Apply.</summary>
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            _profiles.Selected = value;
            SaveProfiles();
            LoadProfile(value);
        }
    }

    /// <summary>Marks the profile's values as edits (the fan changes immediately, as when choosing the mode).</summary>
    void LoadProfile(string? name)
    {
        if (_profiles.Profiles.FirstOrDefault(p => p.Name == name) is not { } profile || _state is null) return;
        var before = Capture();
        Restore(profile);
        if (!Same(before, Capture())) PushUndo(before);
        Report(PendingCount > 0 ? $"Profile \"{profile.Name}\" loaded. Press Apply to write it to the card."
            : $"Profile \"{profile.Name}\" loaded. The card already has these settings.", false);
    }

    void InitProfiles()
    {
        UndoCommand = new RelayCommand(Undo, () => !_busy && CanUndo);
        NewProfileCommand = new RelayCommand(NewProfile, () => _state is not null);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => _state is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => _selectedProfile is not null);
        _profiles = GpuProfileStore.Load();
        foreach (var p in _profiles.Profiles) ProfileNames.Add(p.Name);
        FillGamemodeChoices();
        // The last chosen profile comes back loaded on the first read of the card (RefreshAsync).
        _selectedProfile = ProfileNames.Contains(_profiles.Selected ?? "") ? _profiles.Selected : null;
    }

    GpuProfile Capture(string name = "") => new()
    {
        Name = name,
        Power = _power,
        Thermal = _thermal,
        ThermalSlowdown = _pSlowdown,
        Core = _core,
        Mem = _mem,
        Fan = _fan,
        FanMode = _fanMode,
        // With a pending core offset: the curve as it will be after Apply (shifted along with it, see CurveChanges).
        VfDeltas = VfPoints.ToDictionary(p => p.Index, p => _stagedVf.GetValueOrDefault(p.Index, p.Point.DeltaKhz) + (int)VfShiftKhz),
        VfLock = _stagedVfLock ?? _state?.LockVoltageUv,
        VfClockLock = _stagedClockLock ?? _state?.ClockLockMhz,
        VfActive = _vfActive,
        Curve = ReadCurveInts(),
        Hysteresis = _hysteresis,
    };

    List<FanPoint> ReadCurveInts() => CurvePoints
        .Select(p => int.TryParse(p.Temp, out var t) && int.TryParse(p.Percent, out var pct) ? new FanPoint(t, pct) : null)
        .OfType<FanPoint>().ToList();

    static bool Same(GpuProfile a, GpuProfile b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    void Restore(GpuProfile s)
    {
        _restoring = true;
        try
        {
            if (s.Power is { } power && PowerEnabled) PowerText = power;
            if (s.Thermal is { } thermal && ThermalEnabled) ThermalText = thermal;
            if (s.ThermalSlowdown is { } slowdown) ThermalSlowdownOn = slowdown;
            if (s.Core is { } core && CoreEnabled) CoreText = core;
            if (s.Mem is { } mem && MemEnabled) MemText = mem;
            if (s.VfDeltas is { } deltas && deltas.Count == VfPoints.Count && VfEnabled)
            {
                _vfActive = s.VfActive;
                // The profile's core was already marked above: remove its offset, which Apply puts back on the whole curve.
                foreach (var point in VfPoints.Skip(1)) if (deltas.TryGetValue(point.Index, out var d)) StageVf(point, d - (int)VfShiftKhz);
                StageLock(s.VfLock ?? 0);
                if (CanLockClock) StageClockLock(s.VfClockLock ?? 0);
                FitVfAxis();
                UpdateVfPlot();
                Raise(nameof(VfActive));
                Raise(nameof(VfLockEnabled));
            }
            // Points before the mode: choosing Curve starts the curve with whatever points are on screen.
            if (s.Curve is { Count: >= 2 } curve)
            {
                CurvePoints.Clear();
                foreach (var p in curve) CurvePoints.Add(new FanPointViewModel(p.TempC, p.Percent, this));
                Hysteresis = s.Hysteresis ?? _hysteresis;
                CurveChanged();
            }
            if (FanEnabled)
            {
                _fan = s.Fan ?? _fan;
                SelectFanMode(s.FanMode);
            }
        }
        finally { _restoring = false; }
        _lastSnapshot = Capture();
        _lastChange = DateTime.MinValue;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Called after every edit. A rapid sequence (dragging a slider or a point) becomes a single undo step:
    /// it stores the state from before the sequence when the edit arrives after a pause.
    /// </summary>
    void TrackUndo()
    {
        if (_restoring || _state is null) return;
        var now = Capture();
        if (_lastSnapshot is { } before && DateTime.UtcNow - _lastChange > UndoBurst && !Same(before, now)) PushUndo(before);
        _lastChange = DateTime.UtcNow;
        _lastSnapshot = now;
    }

    void PushUndo(GpuProfile snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        Raise(nameof(CanUndo));
        CommandManager.InvalidateRequerySuggested();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
        Raise(nameof(CanUndo));
        Message = null;
    }

    /// <summary>After re-reading the card (Apply, Discard, Refresh): what was on screen before becomes an undo step.</summary>
    void AfterReload()
    {
        var now = Capture();
        if (_lastSnapshot is { } before && !Same(before, now)) PushUndo(before);
        _lastSnapshot = now;
        _lastChange = DateTime.MinValue;
    }

    void NewProfile()
    {
        var (ok, name, _) = Dialog.Prompt("New profile", "Name for a profile with the current GPU settings:", $"Profile{ProfileNames.Count + 1}",
            text => text.Trim().Length == 0 ? "Enter a name." : ProfileNames.Contains(text.Trim()) ? "A profile with this name already exists." : null);
        if (!ok) return;
        name = name.Trim();
        _profiles.Profiles.Add(Capture(name));
        ProfileNames.Add(name);
        _selectedProfile = name;
        _profiles.Selected = name;
        Raise(nameof(SelectedProfile));
        SaveProfiles();
        FillGamemodeChoices();
        Report($"Profile \"{name}\" created with the current settings. To update it later, press Save.", false);
    }

    void SaveProfile()
    {
        if (_selectedProfile is not { } name) { NewProfile(); return; }
        var i = _profiles.Profiles.FindIndex(p => p.Name == name);
        if (i < 0) { NewProfile(); return; }
        _profiles.Profiles[i] = Capture(name);
        SaveProfiles();
        Report($"Current settings saved to \"{name}\" (replacing what it had).", false);
        if (_gamemodeOn == name || _gamemodeOff == name) SaveGamemode(); // KOS Governor gets the new values
    }

    void DeleteProfile()
    {
        if (_selectedProfile is not { } name) return;
        _profiles.Profiles.RemoveAll(p => p.Name == name);
        ProfileNames.Remove(name);
        _selectedProfile = null;
        _profiles.Selected = null;
        Raise(nameof(SelectedProfile));
        SaveProfiles();
        Report($"Profile \"{name}\" deleted. The card keeps its current settings.", false);
        if (_gamemodeOn == name || _gamemodeOff == name)
        {
            if (_gamemodeOn == name) _gamemodeOn = Txt.GpuGamemodeNone;
            if (_gamemodeOff == name) _gamemodeOff = Txt.GpuGamemodeBack;
            SaveGamemode();
        }
        else FillGamemodeChoices();
    }

    // ---------- gamemode (KOS Governor writes the chosen profiles) ----------

    GamemodeGpu? _gamemode;
    string _gamemodeOn = Txt.GpuGamemodeNone, _gamemodeOff = Txt.GpuGamemodeBack;
    public ObservableCollection<string> GamemodeOnChoices { get; } = new();
    public ObservableCollection<string> GamemodeOffChoices { get; } = new();
    public string? GamemodeStatus => _gamemode is { Suspended: true, On: { } on } ? Txt.GpuGamemodeSuspended(on.Name) : null;

    public string GamemodeOn
    {
        get => _gamemodeOn;
        set { if (value is not null && value != _gamemodeOn) { _gamemodeOn = value; SaveGamemode(); } }
    }

    public string GamemodeOff
    {
        get => _gamemodeOff;
        set { if (value is not null && value != _gamemodeOff) { _gamemodeOff = value; SaveGamemode(); } }
    }

    /// <summary>Rereads the saved choice; a profile missing from this user's list (deleted, or chosen by another user) stays listed.</summary>
    void FillGamemodeChoices()
    {
        _gamemode = GamemodeGpu.Load();
        var on = _gamemode?.On?.Name ?? Txt.GpuGamemodeNone;
        var off = _gamemode?.Off?.Name ?? Txt.GpuGamemodeBack;
        // Clearing the lists sends null to the bindings, which the setters ignore.
        Fill(GamemodeOnChoices, Txt.GpuGamemodeNone, on);
        Fill(GamemodeOffChoices, Txt.GpuGamemodeBack, off);
        (_gamemodeOn, _gamemodeOff) = (on, off);
        Raise(nameof(GamemodeOn));
        Raise(nameof(GamemodeOff));
        Raise(nameof(GamemodeStatus));

        void Fill(ObservableCollection<string> list, string first, string chosen)
        {
            list.Clear();
            list.Add(first);
            foreach (var n in ProfileNames) list.Add(n);
            if (!list.Contains(chosen)) list.Add(chosen);
        }
    }

    void SaveGamemode()
    {
        try
        {
            if (!Elevation.IsAdmin) throw new InvalidOperationException(Txt.GpuGamemodeNeedsAdmin);
            var on = ForGamemode(_gamemodeOn);
            var off = ForGamemode(_gamemodeOff);
            new GamemodeGpu { On = on, Off = off }.Save(); // choosing again also lifts a pause
            // The values come from the per-user profile file: show what KOS Governor will write.
            Report(Txt.GpuGamemodeSaved(_gamemodeOn, _gamemodeOff, on?.Tuning.ToString(), off?.Tuning.ToString()), false);
            Log.Info($"Gamemode GPU profiles saved: on = {on?.Tuning.ToString() ?? "none"}{(on?.Curve is null ? "" : " + fan curve")}; off = {off?.Tuning.ToString() ?? "back to how it was"}{(off?.Curve is null ? "" : " + fan curve")}.");
            if (on?.Tuning.ThermalSlowdown == false || off?.Tuning.ThermalSlowdown == false) Toasts.Show(Txt.GpuThermalSlowdownWarning, ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn($"Gamemode GPU profiles not saved: {ex.Message}");
            Report(ex.Message, true);
        }
        // After the combo box finishes its own selection change (a failed save puts the saved choice back).
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(FillGamemodeChoices));
    }

    /// <summary>The chosen profile as a complete request for this card; null for "don't change" / "back to how it was". Throws if it doesn't fit the card.</summary>
    GamemodeGpuProfile? ForGamemode(string choice)
    {
        if (choice == Txt.GpuGamemodeNone || choice == Txt.GpuGamemodeBack) return null;
        if (_profiles.Profiles.FirstOrDefault(p => p.Name == choice) is not { } p)
            return _gamemode?.On?.Name == choice ? _gamemode.On : _gamemode?.Off?.Name == choice ? _gamemode.Off : null;
        var s = _state ?? throw new InvalidOperationException(Txt.GpuGamemodeInvalid(p.Name, "the card hasn't been read yet."));
        var t = new GpuTuning { PciBusId = s.BusId };
        if (s.PowerProblem is null && GpuTuner.ParseWatts(p.Power ?? "") is { } power) t.PowerLimitMw = power;
        if (s.Thermal is not null && GpuTuner.ParseMhz(p.Thermal ?? "") is { } temp) t.TemperatureLimitC = temp;
        if (s.ThermalSlowdown is not null) t.ThermalSlowdown = p.ThermalSlowdown;
        if (s.Core is not null && GpuTuner.ParseMhz(p.Core ?? "") is { } core) t.CoreOffsetMhz = core;
        if (s.Memory is not null && GpuTuner.ParseMhz(p.Mem ?? "") is { } mem) t.MemOffsetMhz = mem;
        // The profile's points already include its core offset, which the engine writes first (as Apply does).
        if (s.Curve is { } curve && p.VfDeltas is { } deltas && deltas.Count == curve.Points.Count)
            t.CurveChanges = curve.Points.Skip(1).Select(x => new GpuVfChange(x.Index, x.VoltageUv,
                Math.Clamp(deltas.GetValueOrDefault(x.Index, x.DeltaKhz), curve.MinimumDeltaKhz, curve.MaximumDeltaKhz))).ToList();
        if (s.LockVoltageUv is not null) t.LockVoltageUv = p.VfLock ?? 0;
        if (s.ClockLockMhz is not null) t.ClockLockMhz = p.VfClockLock ?? 0;
        GovernorFanCurve? fan = null;
        if (s.FanProblem is null)
        {
            if (p.FanMode == "curve" && p.Curve is { Count: >= 2 } points && int.TryParse(p.Hysteresis, out var h))
                fan = new GovernorFanCurve { Enabled = true, BusId = s.BusId, Points = points, Hysteresis = h };
            else if (p.FanMode == "fixed" && GpuTuner.ParsePercent(p.Fan ?? "") is { } pct) t.FanPercent = pct;
            else t.FanAuto = true;
        }
        var result = new GamemodeGpuProfile { Name = p.Name, Tuning = t, Curve = fan };
        if ((GpuTuner.Problem(t, s) ?? (fan is null ? null : FanCurve.Problem(fan.Points, fan.Hysteresis, s.FanMin, s.FanMax)) ?? GamemodeGpu.Problem(result)) is { } problem)
            throw new InvalidOperationException(Txt.GpuGamemodeInvalid(p.Name, problem));
        return result;
    }

    void SaveProfiles()
    {
        try { _profiles.Save(); }
        catch (Exception ex) { Log.Error("Saving the GPU profiles failed", ex); Report(ex.Message, true); }
    }
}
