using System.Globalization;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;

namespace KosToolbox.ViewModels;

/// <summary>Option of a list (numeric driver/Windows value and the text shown).</summary>
public sealed record Choice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Color adjustments of a monitor made here (the driver/Windows does not keep the gamma ramp); reapplied when the toolbox opens.</summary>
public sealed class DisplayColorProfile
{
    public int[] Brightness { get; set; } = { 50, 50, 50 };
    public int[] Contrast { get; set; } = { 50, 50, 50 };
    /// <summary>Gamma × 100 (100 = 1.00).</summary>
    public int[] Gamma { get; set; } = { 100, 100, 100 };
    /// <summary>Null = never touched here (does not overwrite what the NVIDIA control panel left).</summary>
    public int? Vibrance { get; set; }
    public int? Hue { get; set; }
    public bool Reference { get; set; }

    public bool NeutralRamp => Brightness.All(v => v == 50) && Contrast.All(v => v == 50) && Gamma.All(v => v == 100);
    public ChannelAdjust Channel(int i) => new(Brightness[i], Contrast[i], Gamma[i] / 100.0);
}

/// <summary>Profile of the Settings tab (separate from the overclock profiles): output color, scaling, HDR, counters and color adjustments.</summary>
public sealed class SettingsProfile
{
    public string Name { get; set; } = "";
    public int Format { get; set; }
    public int Range { get; set; }
    public int Bpc { get; set; }
    public int ScaleMode { get; set; }
    public int ScaleOn { get; set; }
    public bool Hdr { get; set; }
    public bool Counters { get; set; }
    public DisplayColorProfile Color { get; set; } = new();
}

/// <summary>%LocalAppData%\KOS\Toolbox\gpu-settings-profiles.json.</summary>
public sealed class SettingsProfileStore
{
    public List<SettingsProfile> Profiles { get; set; } = new();
    public string? Selected { get; set; }
    static string FilePath => System.IO.Path.Combine(AppPaths.DataRoot, "gpu-settings-profiles.json");
    public static SettingsProfileStore Load() => JsonFile.Load<SettingsProfileStore>(FilePath);
    public void Save() => JsonFile.Save(FilePath, this);
}

/// <summary>%LocalAppData%\KOS\Toolbox\display-color.json, by monitor GDI name.</summary>
public sealed class DisplayColorStore
{
    public Dictionary<string, DisplayColorProfile> Displays { get; set; } = new();
    static string FilePath => System.IO.Path.Combine(AppPaths.DataRoot, "display-color.json");
    public static DisplayColorStore Load() => JsonFile.Load<DisplayColorStore>(FilePath);
    public void Save() => JsonFile.Save(FilePath, this);
}

/// <summary>
/// Settings tab of the GPU page: the NVIDIA control panel options for the monitor, applied immediately (no Apply).
/// Output color, vibrance and hue through NVAPI; HDR and scaling through Windows; brightness/contrast/gamma through the gamma ramp.
/// </summary>
public sealed class GpuSettingsViewModel : ObservableObject
{
    List<DisplayTarget> _targets = new();
    DisplayTarget? _target;
    DisplayColorStore _store = new();
    DisplayColorProfile _profile = new();
    IReadOnlyList<Choice> _depths = Array.Empty<Choice>();
    (int Min, int Max, int Default) _dvc = (0, 100, 50);
    int _format, _range, _bpc, _scaleMode = (int)DisplaySettings.ScalingAspect, _scaleOn = ScaleOnDisplay, _channel, _vibrance = 50, _hue;
    bool _loading, _loaded, _busy, _hdr, _hdrSupported, _counters, _colorOk, _dvcOk, _hueOk, _isError, _nvScaling;
    string? _message;

    const int ScaleOnDisplay = 1, ScaleOnGpu = 2;

    public IReadOnlyList<Choice> Formats { get; } = new[] { new Choice(0, "RGB"), new Choice(1, "YCbCr422"), new Choice(2, "YCbCr444"), new Choice(3, "YCbCr420") };
    public IReadOnlyList<Choice> Ranges { get; } = new[] { new Choice(0, "Full"), new Choice(1, "Limited") };
    static readonly Choice[] BaseScaleModes =
    {
        new((int)DisplaySettings.ScalingAspect, "Aspect ratio"), new((int)DisplaySettings.ScalingStretched, "Full-screen"),
        new((int)DisplaySettings.ScalingCentered, "No scaling"),
    };
    /// <summary>Integer scaling only through NVAPI and only when the GPU scales (as in the NVIDIA control panel).</summary>
    public IReadOnlyList<Choice> ScaleModes => _nvScaling && _scaleOn == ScaleOnGpu
        ? BaseScaleModes.Append(new Choice((int)DisplaySettings.ScalingInteger, "Integer scaling")).ToList() : BaseScaleModes;
    public IReadOnlyList<Choice> ScaleTargets { get; } = new[] { new Choice(ScaleOnDisplay, "Display"), new Choice(ScaleOnGpu, "GPU") };
    public IReadOnlyList<Choice> Channels { get; } = new[] { new Choice(0, "All channels"), new Choice(1, "Red"), new Choice(2, "Green"), new Choice(3, "Blue") };

    public IReadOnlyList<DisplayTarget> Targets => _targets;
    public bool HasManyTargets => _targets.Count > 1;
    public string TargetName => _target?.Name ?? "—";
    public DisplayTarget? SelectedTarget
    {
        get => _target;
        set { if (value is not null && value != _target) { _target = value; MainViewModel.Fire(LoadTargetAsync()); } }
    }

    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    // ---------- output color (NVAPI) ----------
    public bool ColorAvailable => _colorOk && !_busy;
    public IReadOnlyList<Choice> Depths => _depths;
    public int Format { get => _format; set { if (Set(ref _format, value) && !_loading) MainViewModel.Fire(ApplyColorAsync()); } }
    public int Range { get => _range; set { if (Set(ref _range, value) && !_loading) MainViewModel.Fire(ApplyColorAsync()); } }
    public int Depth { get => _bpc; set { if (Set(ref _bpc, value) && !_loading) MainViewModel.Fire(ApplyColorAsync()); } }

    // ---------- scaling (Windows) ----------
    public int ScaleMode { get => _scaleMode; set { if (Set(ref _scaleMode, value) && !_loading) MainViewModel.Fire(ApplyScalingAsync()); } }
    public int ScaleOn
    {
        get => _scaleOn;
        set
        {
            if (!Set(ref _scaleOn, value)) return;
            if (_scaleOn == ScaleOnDisplay && _scaleMode == (int)DisplaySettings.ScalingInteger) _scaleMode = (int)DisplaySettings.ScalingAspect;
            Raise(nameof(ScaleModes));
            Raise(nameof(ScaleMode));
            Raise(nameof(ScaleModeEnabled));
            if (!_loading) MainViewModel.Fire(ApplyScalingAsync());
        }
    }
    bool? _override;
    /// <summary>NVIDIA's "Override the scaling mode set by games and programs"; null when the driver has no setting saved for the monitor.</summary>
    public bool OverrideAvailable => _override is not null && !_busy;
    public bool ScalingOverride
    {
        get => _override == true;
        set
        {
            if (_override is null || value == _override || _loading || _target is not { } t) return;
            _override = value;
            var product = t.Product;
            MainViewModel.Fire(RunSlowAsync(() => Core.Display.Monitors.SetScalingOverrideAsync(product, value).GetAwaiter().GetResult(),
                value ? "Games and programs can no longer change the scaling." : "Games and programs can change the scaling again."));
        }
    }

    /// <summary>With NVIDIA the mode also applies when the display scales; through Windows only the GPU's scaling has modes.</summary>
    public bool ScaleModeEnabled => _nvScaling || _scaleOn == ScaleOnGpu;

    // ---------- HDR / counters ----------
    public bool HdrSupported => _hdrSupported && !_busy;
    public bool Hdr { get => _hdr; set { if (Set(ref _hdr, value) && !_loading) MainViewModel.Fire(ApplyHdrAsync()); } }
    public bool CountersForAllUsers
    {
        get => _counters;
        set
        {
            if (!Set(ref _counters, value) || _loading) return;
            Run(() => DisplaySettings.SetCountersForAllUsers(value), "GPU performance counters updated.", () => { _counters = DisplaySettings.CountersForAllUsers(); Raise(nameof(CountersForAllUsers)); });
        }
    }

    // ---------- brightness / contrast / gamma / vibrance / hue ----------
    public bool Adjustable => _target is not null && !_profile.Reference;
    public int Channel
    {
        get => _channel;
        set { if (Set(ref _channel, value)) RaiseRamp(); }
    }
    int Index => Math.Max(0, _channel - 1);
    public int Brightness { get => _profile.Brightness[Index]; set => SetRamp(_profile.Brightness, value); }
    public int Contrast { get => _profile.Contrast[Index]; set => SetRamp(_profile.Contrast, value); }
    public int Gamma { get => _profile.Gamma[Index]; set => SetRamp(_profile.Gamma, Math.Clamp(value, 30, 280)); }
    public string GammaText
    {
        get => (Gamma / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse((value ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
                Gamma = (int)Math.Round(g * 100);
            Raise();
        }
    }
    public bool VibranceAvailable => _dvcOk && Adjustable;
    public int Vibrance
    {
        get => _vibrance;
        set
        {
            if (!Set(ref _vibrance, Math.Clamp(value, 0, 100)) || _loading || _target is not { } t) return;
            _profile.Vibrance = _vibrance;
            Save();
            Run(() => DisplaySettings.SetVibrance(t.GdiName, ToLevel(_vibrance)), null, null);
        }
    }
    public bool HueAvailable => _hueOk && Adjustable;
    public int Hue
    {
        get => _hue;
        set
        {
            if (!Set(ref _hue, Math.Clamp(value, 0, 359)) || _loading || _target is not { } t) return;
            _profile.Hue = _hue;
            Save();
            Run(() => DisplaySettings.SetHue(t.GdiName, _hue), null, null);
        }
    }
    /// <summary>
    /// Reference mode: while on, the monitor has none of the adjustments from here (neutral ramp, default vibrance and hue).
    /// The values are kept and come back when it is turned off.
    /// </summary>
    public bool ReferenceMode
    {
        get => _profile.Reference;
        set
        {
            if (value == _profile.Reference || _target is null) return;
            _profile.Reference = value;
            Save();
            var target = _target;
            Run(() => ApplyAdjustments(target, _profile), value ? "Reference mode: no color adjustments from here." : "Color adjustments restored.", null);
            MainViewModel.Fire(LoadTargetAsync()); // vibrance/hue changed: re-read the values shown
            RaiseAdjustable();
        }
    }

    void SetRamp(int[] values, int value)
    {
        if (_target is null) return;
        if (_channel == 0) Array.Fill(values, value); else values[Index] = value;
        Save();
        RaiseRamp();
        if (!_profile.Reference) Run(() => DisplaySettings.SetGamma(_target.GdiName, _profile.Channel(0), _profile.Channel(1), _profile.Channel(2)), null, null);
    }

    void RaiseRamp()
    {
        Raise(nameof(Brightness));
        Raise(nameof(Contrast));
        Raise(nameof(Gamma));
        Raise(nameof(GammaText));
    }

    void RaiseAdjustable()
    {
        Raise(nameof(Adjustable));
        Raise(nameof(VibranceAvailable));
        Raise(nameof(HueAvailable));
        Raise(nameof(ReferenceMode));
    }

    // Vibrance in % (50 = driver default), as in the panel; NVAPI uses levels with their own minimum, maximum and default.
    int ToLevel(int pct) => pct >= 50
        ? _dvc.Default + (int)Math.Round((_dvc.Max - _dvc.Default) * (pct - 50) / 50.0)
        : _dvc.Default - (int)Math.Round((_dvc.Default - _dvc.Min) * (50 - pct) / 50.0);
    int ToPercent(int level) => level >= _dvc.Default
        ? 50 + (_dvc.Max == _dvc.Default ? 0 : (int)Math.Round(50.0 * (level - _dvc.Default) / (_dvc.Max - _dvc.Default)))
        : 50 - (_dvc.Default == _dvc.Min ? 0 : (int)Math.Round(50.0 * (_dvc.Default - level) / (_dvc.Default - _dvc.Min)));

    // ---------- profiles (this tab only; the overclock ones are separate) ----------
    SettingsProfileStore _profiles = SettingsProfileStore.Load();
    string? _selectedProfile;
    public System.Collections.ObjectModel.ObservableCollection<string> ProfileNames { get; } = new();
    public System.Windows.Input.ICommand NewProfileCommand { get; }
    public System.Windows.Input.ICommand SaveProfileCommand { get; }
    public System.Windows.Input.ICommand DeleteProfileCommand { get; }

    public GpuSettingsViewModel()
    {
        foreach (var p in _profiles.Profiles) ProfileNames.Add(p.Name);
        _selectedProfile = ProfileNames.Contains(_profiles.Selected ?? "") ? _profiles.Selected : null;
        NewProfileCommand = new RelayCommand(NewProfile, () => _target is not null && !_busy);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => _target is not null && !_busy);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => _selectedProfile is not null);
    }

    /// <summary>Choosing a profile applies immediately (like everything in this tab).</summary>
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            _profiles.Selected = value;
            SaveProfiles();
            if (_profiles.Profiles.FirstOrDefault(p => p.Name == value) is { } profile) MainViewModel.Fire(ApplyProfileAsync(profile));
        }
    }

    static DisplayColorProfile Copy(DisplayColorProfile p) =>
        System.Text.Json.JsonSerializer.Deserialize<DisplayColorProfile>(System.Text.Json.JsonSerializer.Serialize(p))!;

    SettingsProfile Capture(string name) => new()
    {
        Name = name, Format = _format, Range = _range, Bpc = _bpc, ScaleMode = _scaleMode, ScaleOn = _scaleOn,
        Hdr = _hdr, Counters = _counters, Color = Copy(_profile),
    };

    async Task ApplyProfileAsync(SettingsProfile p)
    {
        if (_target is not { } t) return;
        var color = new NvColor(p.Format, p.Range, p.Bpc, 0);
        var changeColor = _colorOk && (p.Format != _format || p.Range != _range || p.Bpc != _bpc);
        var changeScaling = p.ScaleOn != _scaleOn || p.ScaleMode != _scaleMode;
        var nv = _nvScaling;
        var changeHdr = _hdrSupported && p.Hdr != _hdr;
        var changeCounters = p.Counters != _counters;
        _profile = Copy(p.Color);
        Save();
        var errors = new List<string>();
        IsBusy = true;
        await Task.Run(() =>
        {
            void Try(Action a) { try { a(); } catch (Exception ex) { errors.Add(ex.Message); } }
            if (changeColor) Try(() => DisplaySettings.SetColor(t.GdiName, color));
            if (changeScaling) Try(() => SetScaling(t, nv, p.ScaleOn, p.ScaleMode));
            if (changeHdr) Try(() => DisplaySettings.SetHdr(t, p.Hdr));
            if (changeCounters) Try(() => DisplaySettings.SetCountersForAllUsers(p.Counters));
            Try(() => ApplyAdjustments(t, _profile));
        });
        IsBusy = false;
        await LoadTargetAsync();
        Report(errors.Count == 0 ? $"Settings profile \"{p.Name}\" applied." : string.Join(" ", errors), errors.Count > 0);
    }

    void NewProfile()
    {
        var (ok, name, _) = Views.Dialog.Prompt("New settings profile", "Name for a profile with the current display settings:", $"Settings{ProfileNames.Count + 1}",
            text => text.Trim().Length == 0 ? "Enter a name." : ProfileNames.Contains(text.Trim()) ? "A profile with this name already exists." : null);
        if (!ok) return;
        name = name.Trim();
        _profiles.Profiles.Add(Capture(name));
        ProfileNames.Add(name);
        _selectedProfile = _profiles.Selected = name;
        Raise(nameof(SelectedProfile));
        SaveProfiles();
        Report($"Settings profile \"{name}\" created with the current settings. To update it later, press Save.", false);
    }

    void SaveProfile()
    {
        var i = _profiles.Profiles.FindIndex(p => p.Name == _selectedProfile);
        if (i < 0) { NewProfile(); return; }
        _profiles.Profiles[i] = Capture(_selectedProfile!);
        SaveProfiles();
        Report($"Current settings saved to \"{_selectedProfile}\" (replacing what it had).", false);
    }

    void DeleteProfile()
    {
        if (_selectedProfile is not { } name) return;
        _profiles.Profiles.RemoveAll(p => p.Name == name);
        ProfileNames.Remove(name);
        _selectedProfile = _profiles.Selected = null;
        Raise(nameof(SelectedProfile));
        SaveProfiles();
        Report($"Settings profile \"{name}\" deleted. The display keeps its current settings.", false);
    }

    void SaveProfiles()
    {
        try { _profiles.Save(); }
        catch (Exception ex) { Report(ex.Message, true); }
    }

    // ---------- reading ----------
    /// <summary>Reads everything on the first visit to the tab (and on the page's Refresh).</summary>
    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force) return;
        _loaded = true;
        _store = DisplayColorStore.Load();
        try { _targets = await Task.Run(DisplaySettings.Targets); }
        catch (Exception ex) { _targets = new(); Report(ex.Message, true); }
        Raise(nameof(Targets));
        Raise(nameof(HasManyTargets));
        _target = _targets.FirstOrDefault(t => t.GdiName == _target?.GdiName) ?? _targets.FirstOrDefault();
        await LoadTargetAsync();
    }

    async Task LoadTargetAsync()
    {
        Raise(nameof(SelectedTarget));
        Raise(nameof(TargetName));
        if (_target is not { } t) return;
        _profile = _store.Displays.TryGetValue(t.GdiName, out var saved) ? saved : new DisplayColorProfile();
        var r = await Task.Run(() => Read(t));
        _loading = true;
        try
        {
            _colorOk = r.Color is not null;
            if (r.Color is { } c) { _format = c.Format; _range = c.Range; _bpc = c.Bpc; }
            _depths = r.Depths;
            _nvScaling = r.NvScaling is not null;
            _override = r.Override;
            if (r.NvScaling is { } nv)
            {
                var (gpu, mode) = DisplaySettings.FromNvScaling(nv);
                (_scaleOn, _scaleMode) = (gpu ? ScaleOnGpu : ScaleOnDisplay, (int)mode);
            }
            else
            {
                _scaleOn = r.Scaling is DisplaySettings.ScalingIdentity or DisplaySettings.ScalingPreferred ? ScaleOnDisplay : ScaleOnGpu;
                if (_scaleOn == ScaleOnGpu) _scaleMode = (int)r.Scaling;
            }
            (_hdrSupported, _hdr) = r.Hdr;
            _counters = r.Counters;
            _dvcOk = r.Dvc is not null;
            if (r.Dvc is { } d) { _dvc = (d.Min, d.Max, d.Default); _vibrance = ToPercent(d.Current); }
            _hueOk = r.Hue is not null;
            _hue = r.Hue ?? 0;
        }
        finally { _loading = false; }
        Raise(""); // all properties
        if (r.Problem is not null) Report(r.Problem, true);
    }

    sealed record Reading(NvColor? Color, IReadOnlyList<Choice> Depths, uint Scaling, int? NvScaling, bool? Override, (bool, bool) Hdr, bool Counters,
        (int Current, int Min, int Max, int Default)? Dvc, int? Hue, string? Problem);

    static Reading Read(DisplayTarget t)
    {
        string? problem = null;
        NvColor? color = null;
        try { color = DisplaySettings.ReadColor(t.GdiName); }
        catch (Exception ex) { problem = ex.Message; }
        var depths = color is null ? Array.Empty<Choice>() : new[] { (1, "6 bpc"), (2, "8 bpc"), (3, "10 bpc"), (4, "12 bpc") }
            .Where(d => d.Item1 == color.Bpc || DisplaySettings.ColorSupported(t.GdiName, color with { Bpc = d.Item1, Policy = 0 }))
            .Select(d => new Choice(d.Item1, d.Item2)).ToArray();
        uint scaling = DisplaySettings.ScalingIdentity;
        try { scaling = DisplaySettings.ReadScaling(t); } catch (Exception ex) { problem ??= ex.Message; }
        (int, int, int, int)? dvc = null;
        try { dvc = DisplaySettings.ReadVibrance(t.GdiName); } catch { /* no DVC on this monitor */ }
        int? hue = null;
        try { hue = DisplaySettings.ReadHue(t.GdiName).Current; } catch { /* no hue on this monitor */ }
        bool counters = false;
        try { counters = DisplaySettings.CountersForAllUsers(); } catch { /* no NVIDIA driver */ }
        return new Reading(color, depths, scaling, DisplaySettings.ReadNvScaling(t.GdiName), DisplaySettings.ReadScalingOverride(t.Product), DisplaySettings.ReadHdr(t), counters, dvc, hue, problem);
    }

    // ---------- writing ----------
    async Task ApplyColorAsync()
    {
        if (_target is not { } t) return;
        var wanted = new NvColor(_format, _range, _bpc, 0);
        await RunSlowAsync(() => DisplaySettings.SetColor(t.GdiName, wanted), "Output color updated.");
    }

    async Task ApplyScalingAsync()
    {
        if (_target is not { } t) return;
        var (nv, on, mode) = (_nvScaling, _scaleOn, _scaleMode);
        await RunSlowAsync(() => SetScaling(t, nv, on, mode), "Scaling updated.");
    }

    /// <summary>Through NVAPI when it drives the monitor (all the panel's modes); otherwise Windows (display = no GPU scaling).</summary>
    static void SetScaling(DisplayTarget t, bool nv, int on, int mode)
    {
        if (nv) DisplaySettings.SetNvScaling(t.GdiName, DisplaySettings.ToNvScaling(on == ScaleOnGpu, (uint)mode));
        else DisplaySettings.SetScaling(t, on == ScaleOnDisplay || mode == (int)DisplaySettings.ScalingInteger ? DisplaySettings.ScalingIdentity : (uint)mode);
    }

    async Task ApplyHdrAsync()
    {
        if (_target is not { } t) return;
        var on = _hdr;
        await RunSlowAsync(() => DisplaySettings.SetHdr(t, on), on ? "HDR on." : "HDR off.");
    }

    /// <summary>Changes that switch the monitor mode (the screen flickers): run off the UI and re-read at the end.</summary>
    async Task RunSlowAsync(Action action, string done)
    {
        IsBusy = true;
        Raise(nameof(ColorAvailable));
        Raise(nameof(HdrSupported));
        try
        {
            await Task.Run(action);
            Report(done, false);
        }
        catch (Exception ex)
        {
            Core.Persistence.Log.Warn($"Display setting failed: {ex.Message}");
            Report(ex.Message, true);
        }
        finally { IsBusy = false; }
        await LoadTargetAsync();
    }

    /// <summary>Quick changes (sliders): immediate, only the error is shown.</summary>
    void Run(Action action, string? done, Action? onError)
    {
        try
        {
            action();
            if (done is not null) Report(done, false);
            else if (IsError) Report(null, false);
        }
        catch (Exception ex)
        {
            Report(ex.Message, true);
            onError?.Invoke();
        }
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }

    void Save()
    {
        if (_target is null) return;
        _store.Displays[_target.GdiName] = _profile;
        try { _store.Save(); }
        catch (Exception ex) { Report(ex.Message, true); }
    }

    /// <summary>Ramp, vibrance and hue of the profile (or the neutral ones, in reference mode).</summary>
    static void ApplyAdjustments(DisplayTarget t, DisplayColorProfile p)
    {
        var neutral = ChannelAdjust.Neutral;
        DisplaySettings.SetGamma(t.GdiName, p.Reference ? neutral : p.Channel(0), p.Reference ? neutral : p.Channel(1), p.Reference ? neutral : p.Channel(2));
        if (p.Reference)
        {
            TryRun(() => DisplaySettings.SetVibrance(t.GdiName, DisplaySettings.ReadVibrance(t.GdiName).Default));
            TryRun(() => DisplaySettings.SetHue(t.GdiName, DisplaySettings.ReadHue(t.GdiName).Default));
            return;
        }
        if (p.Vibrance is { } v) TryRun(() => DisplaySettings.SetVibrance(t.GdiName, LevelFor(v, DisplaySettings.ReadVibrance(t.GdiName))));
        if (p.Hue is { } h) TryRun(() => DisplaySettings.SetHue(t.GdiName, h));
    }

    static int LevelFor(int pct, (int Current, int Min, int Max, int Default) d) => pct >= 50
        ? d.Default + (int)Math.Round((d.Max - d.Default) * (pct - 50) / 50.0)
        : d.Default - (int)Math.Round((d.Default - d.Min) * (50 - pct) / 50.0);

    static void TryRun(Action a)
    {
        try { a(); } catch (Exception ex) { Core.Persistence.Log.Warn($"Display color: {ex.Message}"); }
    }

    /// <summary>When the toolbox opens: reapplies what was adjusted here (the gamma ramp does not survive a restart).</summary>
    public static void ApplySaved()
    {
        try
        {
            var store = DisplayColorStore.Load();
            if (store.Displays.Count == 0) return;
            foreach (var t in DisplaySettings.Targets())
                if (store.Displays.TryGetValue(t.GdiName, out var p) && (p.Reference || !p.NeutralRamp || p.Vibrance is not null || p.Hue is not null))
                    ApplyAdjustments(t, p);
        }
        catch (Exception ex) { Core.Persistence.Log.Warn($"Saved display color not applied: {ex.Message}"); }
    }
}
