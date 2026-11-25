using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Audio;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.ViewModels;

namespace KosEqualizerApp;

/// <summary>A filter of the parametric editor: its numbers as typed (a dot or a comma), written into the profile's band once they parse.</summary>
public sealed class EqBandRow : ObservableObject
{
    readonly EqBand _band;
    readonly Action _changed;
    string _frequency, _gain, _q;

    public EqBandRow(EqBand band, int number, Action changed, Action<EqBand> remove)
    {
        (_band, _changed, Number) = (band, changed, number);
        (_frequency, _gain, _q) = (Txt.EqNumber(band.Frequency), Txt.EqNumber(band.Gain), Txt.EqNumber(band.Q));
        RemoveCommand = new RelayCommand(() => remove(band));
    }

    public static IReadOnlyList<OptionItem> Types { get; } = Enum.GetValues<EqFilterType>().Select(t => new OptionItem(t.ToString(), Txt.EqFilterName(t))).ToList();

    /// <summary>From 1, as <see cref="Equalizer.Problem"/> numbers the bands.</summary>
    public int Number { get; }
    public bool Enabled { get => _band.Enabled; set { if (_band.Enabled == value) return; _band.Enabled = value; Raise(); _changed(); } }
    public OptionItem Type
    {
        get => Types.First(t => t.Key == _band.Type.ToString());
        set
        {
            if (value is null || value.Key == _band.Type.ToString()) return; // null: the list being rebuilt
            _band.Type = Enum.Parse<EqFilterType>(value.Key);
            Raise();
            Raise(nameof(HasGain));
            Raise(nameof(TextProblem));
            _changed();
        }
    }
    /// <summary>The pass, notch and band-pass filters have no gain.</summary>
    public bool HasGain => _band.Type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf;
    public string Frequency { get => _frequency; set => Edit(ref _frequency, value, v => _band.Frequency = v); }
    public string Gain { get => _gain; set => Edit(ref _gain, value, v => _band.Gain = v); }
    public string Q { get => _q; set => Edit(ref _q, value, v => _band.Q = v); }
    public ICommand RemoveCommand { get; }

    /// <summary>A field that isn't a number (the band keeps its last number until it is one).</summary>
    public string? TextProblem =>
        Parse(_frequency) is null ? Txt.EqNotANumber(Number, Txt.EqFrequency)
        : HasGain && Parse(_gain) is null ? Txt.EqNotANumber(Number, Txt.EqGain)
        : Parse(_q) is null ? Txt.EqNotANumber(Number, Txt.EqQ) : null;

    void Edit(ref string field, string? value, Action<double> apply, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value ?? "", name)) return;
        if (Parse(field) is { } v) apply(v);
        Raise(nameof(TextProblem));
        _changed();
    }

    static double? Parse(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}

/// <summary>A slider of the graphic editor: a peak band at a fixed frequency.</summary>
public sealed class EqGraphicRow(EqBand band, Action changed) : ObservableObject
{
    /// <summary>The sliders' range in dB; an imported band beyond it shows at the end and keeps its gain until moved.</summary>
    public const double Range = 15;

    public string Label { get; } = Txt.EqHz(band.Frequency);
    public string Tooltip => Txt.EqGraphicTip(band.Frequency, band.Gain);
    public string GainText => Txt.EqDbShort(band.Gain);
    public double Gain
    {
        get => band.Gain;
        set
        {
            if (value == Math.Clamp(band.Gain, -Range, Range)) return; // the slider's own limit coming back: not an edit
            band.Gain = Math.Round(value * 2) / 2;
            Raise();
            Raise(nameof(GainText));
            Raise(nameof(Tooltip));
            changed();
        }
    }
}

/// <summary>
/// A playback or recording device in KOS Equalizer: whether KOS Equalizer is on for it (read from the system, and again after every click), its
/// profiles, the active one's editor, import and export, and its Windows audio settings. A device KOS Equalizer is on for that
/// isn't connected is listed from the registration journal and only offers Turn off.
/// </summary>
public sealed class EqDeviceViewModel : ObservableObject
{
    readonly EqualizerPageViewModel _page;
    readonly string _name; // the journal's name, for a device that isn't connected
    EqStatus? _status;
    string? _statusError;
    bool _selected, _busy, _gamemodeOn, _loading;
    IReadOnlyList<OptionItem> _activeItems = [], _gamemodeItems = [];
    OptionItem? _activeItem, _gamemodeItem;
    EqProfile? _profile;
    IReadOnlyList<EqBandRow> _bands = [];
    IReadOnlyList<EqGraphicRow> _graphic = [];
    IReadOnlyList<Point> _curve = [];
    string _renameText = "", _pasteText = "";

    public EqDeviceViewModel(EqualizerPageViewModel page, string id, string name, AudioDevice? device)
    {
        (_page, Id, _name) = (page, id, name);
        if (device is not null)
        {
            Settings = new AudioDeviceSettingsViewModel(device);
            Settings.Applied += (_, _) => MainViewModel.Fire(page.ListAsync(readSelected: false, profiles: false)); // another device may have lost the default
            Settings.PropertyChanged += (_, _) => { Raise(nameof(Name)); Raise(nameof(Sub)); };
        }
        Modes = [new("0", Txt.EqParametric), new("10", Txt.EqGraphicBands(10)), new("15", Txt.EqGraphicBands(15)), new("31", Txt.EqGraphicBands(31))];
        ToggleCommand = new AsyncCommand(() => CanTurnOff ? TurnOffAsync() : TurnOnAsync(), () => !_busy && HasToggle);
        NewCommand = new RelayCommand(NewProfile, () => CanEdit);
        DuplicateCommand = new RelayCommand(Duplicate, () => CanEdit && _profile is not null);
        DeleteCommand = new RelayCommand(Delete, () => CanEdit && _profile is not null);
        RenameCommand = new RelayCommand(Rename, () => CanEdit && _profile is not null && _renameText.Trim() != _profile.Name && RenameProblem is null);
        SetModeCommand = new RelayCommand(p => SetMode(int.Parse((string)p!, CultureInfo.InvariantCulture)), _ => CanEdit && _profile is not null);
        SuggestedCommand = new RelayCommand(Suggested, () => CanEdit && _profile is not null);
        AddBandCommand = new RelayCommand(AddBand, () => CanEdit && _profile is { Bands.Count: < 64 });
        ImportFileCommand = new AsyncCommand(ImportFileAsync, () => CanEdit);
        ImportPasteCommand = new RelayCommand(() => Import(_pasteText, Txt.EqImportedName, pasted: true), () => CanEdit && _pasteText.Trim().Length > 0);
        ExportCommand = new RelayCommand(Export, () => _profile is not null);
        ListenCommand = new RelayCommand(() => ToggleMonitor(live: true), () => CanMonitor && (_monitor is null || _live));
        RecordCommand = new RelayCommand(() => ToggleMonitor(live: false), () => CanMonitor && (_monitor is null || !_live));
    }

    // ---------- hearing a microphone through the equalizer ----------
    MicMonitor? _monitor;
    bool _live;
    string? _monitorText;
    double _level;
    DispatcherTimer? _levelTimer; // made on first use: a device's view model can be built off the UI thread

    /// <summary>A connected microphone: the pinned bar with Listen and Record shows (the sound is the endpoint's, so the equalizer is in it when it is on).</summary>
    public bool CanMonitor => IsRecording && IsConnected;
    public ICommand ListenCommand { get; }
    public ICommand RecordCommand { get; }
    public string ListenText => _monitor is not null && _live ? Txt.EqListenStop : Txt.EqListen;
    public string RecordText => _monitor is not null && !_live ? Txt.EqRecordStop : Txt.EqRecord;
    public string? MonitorText { get => _monitorText; private set => Set(ref _monitorText, value); }
    public double Level { get => _level; private set => Set(ref _level, value); }

    /// <summary>Starts the live listen (or the 5 s recording and playback); the same button again stops it.</summary>
    void ToggleMonitor(bool live)
    {
        if (_monitor is not null) { StopMonitor(); return; }
        if (!CanMonitor) return;
        var monitor = live ? MicMonitor.Listen(Id) : MicMonitor.RecordAndPlay(Id, 5);
        (_monitor, _live) = (monitor, live);
        MonitorText = live ? Txt.EqMonitoringLive : Txt.EqRecordingNow;
        monitor.Progress += text => Dispatcher.BeginInvoke(() => { if (_monitor == monitor) MonitorText = text.StartsWith("Playing", StringComparison.Ordinal) ? Txt.EqPlayingBack : Txt.EqRecordingNow; });
        monitor.Ended += problem => Dispatcher.BeginInvoke(() =>
        {
            if (_monitor != monitor) return; // stopped by the user: already cleaned up
            _monitor = null;
            monitor.Dispose();
            MonitorEnded(problem);
        });
        if (_levelTimer is null)
        {
            _levelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            _levelTimer.Tick += (_, _) => Level = _monitor?.Peak ?? 0;
        }
        _levelTimer.Start();
        RaiseMonitor();
    }

    static Dispatcher Dispatcher => Application.Current.Dispatcher;

    internal void StopMonitor()
    {
        if (_monitor is not { } monitor) return;
        _monitor = null;
        monitor.Dispose();
        MonitorEnded(null);
    }

    void MonitorEnded(string? problem)
    {
        _levelTimer?.Stop();
        Level = 0;
        MonitorText = problem is null ? null : Txt.EqMonitorFailed(problem);
        RaiseMonitor();
    }

    void RaiseMonitor()
    {
        foreach (var name in new[] { nameof(ListenText), nameof(RecordText) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- the list's row (the Audio page's AudioDeviceRow template) ----------
    public string Id { get; }
    /// <summary>Null when the device isn't connected.</summary>
    public AudioDeviceSettingsViewModel? Settings { get; }
    public bool IsConnected => Settings is not null;
    public string Name => Settings?.Name ?? _name;
    /// <summary>A recording endpoint's id starts "{0.0.1." (playback "{0.0.0."); also true for a microphone that isn't connected.</summary>
    public bool IsRecording => Id.StartsWith("{0.0.1.", StringComparison.Ordinal);
    public string Glyph => IsRecording ? "" : ""; // microphone, speakers
    public string FlowText => IsRecording ? Txt.AudioRecordingDevice : Txt.AudioPlaybackDevice;
    public string Sub => Txt.EqRowText(Settings?.IsDefault == true, IsConnected, StateShort, Find() is { } entry ? entry.Find(entry.Active)?.Name : null);
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value) && !value) StopMonitor(); } }

    internal EqDeviceViewModel Update(AudioDevice? device)
    {
        if (device is not null) Settings?.Update(device);
        return this;
    }

    // ---------- KOS Equalizer on this device ----------
    EqDeviceState? State => _status?.State;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(BusyText)); CommandManager.InvalidateRequerySuggested(); } } }
    public string? BusyText => _busy ? Txt.EqBusy : null;
    public string StateText => _statusError ?? State switch
    {
        EqDeviceState.On => Txt.EqStateOn, EqDeviceState.Off => Txt.EqStateOff, EqDeviceState.OtherEqualizer => Txt.EqStateOther,
        EqDeviceState.ChangedSince => Txt.EqStateChanged, EqDeviceState.Unsupported => Txt.EqStateUnsupported, _ => "",
    };
    string? StateShort => State switch
    {
        EqDeviceState.On => Txt.EqRowOn, EqDeviceState.Off => Txt.EqRowOff, EqDeviceState.OtherEqualizer => Txt.EqRowOther,
        EqDeviceState.ChangedSince => Txt.EqRowChanged, EqDeviceState.Unsupported => Txt.EqRowUnsupported, _ => null,
    };
    public bool StateWarning => _statusError is not null || State is EqDeviceState.ChangedSince or EqDeviceState.Unsupported;
    /// <summary>Why it changed since, or why it isn't available.</summary>
    public string? Detail => _status?.Detail;
    public bool ShowOtherNote => State == EqDeviceState.OtherEqualizer && IsConnected;
    /// <summary>Read from the journal, so it lasts until KOS Equalizer is turned off.</summary>
    public bool ShowReplacedNote => _status is { ReplacedOther: true, State: EqDeviceState.On or EqDeviceState.ChangedSince };
    public string? EffectText => State != EqDeviceState.On ? null : _status!.Effect is { } line ? Txt.EqEffect(line, _status.EffectTime) : Txt.EqEffectNone;
    public bool IsOn => State == EqDeviceState.On;
    bool CanTurnOn => IsConnected && State is EqDeviceState.Off or EqDeviceState.OtherEqualizer;
    bool CanTurnOff => State is EqDeviceState.On or EqDeviceState.ChangedSince;
    public bool HasToggle => CanTurnOn || CanTurnOff;
    public string ToggleText => CanTurnOff ? Txt.EqTurnOff : Txt.EqTurnOn;
    public ICommand ToggleCommand { get; }
    public string? OffHint => CanEdit && State is EqDeviceState.Off or EqDeviceState.OtherEqualizer ? Txt.EqOffHint : null;

    /// <summary>The state as read now (the list reads every device's; a click reads its own again).</summary>
    internal void Show(EqStatus? status, string? error, bool gamemodeOn)
    {
        (_status, _statusError, _gamemodeOn) = (status, error, gamemodeOn);
        foreach (var name in new[] { nameof(StateText), nameof(StateWarning), nameof(Detail), nameof(ShowOtherNote), nameof(ShowReplacedNote), nameof(EffectText),
                     nameof(IsOn), nameof(HasToggle), nameof(ToggleText), nameof(OffHint), nameof(GamemodeNote), nameof(Sub) }) Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task ReadStatusAsync()
    {
        var id = Id;
        try
        {
            var (status, gamemode) = await Task.Run(() => (EqualizerPageViewModel.ReadStatus(id), EqConfig.GamemodeOn));
            Show(status, null, gamemode);
        }
        catch (Exception ex)
        {
            Log.Warn($"Equalizer: reading the state of {Name} failed: {ex.Message}");
            Show(null, Txt.EqStatusFailed(ex.Message), _gamemodeOn);
        }
    }

    async Task TurnOnAsync()
    {
        IsBusy = true;
        string? error = null;
        try { await _page.RunExclusiveAsync(() => EqRegistration.Activate(Id, Name)); }
        catch (Exception ex)
        {
            Log.Error($"Equalizer: turning on {Name} failed", ex);
            error = ex.Message;
        }
        await ReadStatusAsync(); // what the system has now, also after a failure (it can fail after the change, at the audio service)
        IsBusy = false;
        if (!IsOn)
        {
            Toasts.Show(error is not null ? Txt.EqTurnOnFailed(Name, error) : Txt.EqNotConfirmed(Name, StateText), error is not null ? ToastKind.Error : ToastKind.Warning);
            return;
        }
        if (_status!.ReplacedOther) Toasts.Show(Txt.EqReplacedToast(Name), ToastKind.Warning);
        if (error is not null) Toasts.Show(Txt.EqOnWithError(Name, error), ToastKind.Warning);
        // It starts flat: what the device should play goes to its settings file now.
        if (_page.Config is not null && Find() is not null) await _page.SaveAndPlayAsync(this, Txt.EqTurnedOn(Name));
        else Toasts.Show(Txt.EqTurnedOn(Name) + " " + Txt.EqPlays(Name, true, null));
    }

    async Task TurnOffAsync()
    {
        IsBusy = true;
        List<string>? left = null;
        string? error = null;
        try { left = await _page.RunExclusiveAsync(() => EqRegistration.Deactivate(Id)); }
        catch (Exception ex)
        {
            Log.Error($"Equalizer: turning off {Name} failed", ex);
            error = ex.Message;
        }
        await ReadStatusAsync();
        IsBusy = false;
        if (_statusError is not null || State is EqDeviceState.On or EqDeviceState.ChangedSince)
            Toasts.Show(error is not null ? Txt.EqTurnOffFailed(Name, error) : Txt.EqNotConfirmed(Name, StateText), error is not null ? ToastKind.Error : ToastKind.Warning);
        else if (left is { Count: > 0 }) Toasts.Show(Txt.EqTurnedOffLeft(Name, left), ToastKind.Warning);
        else if (error is not null) Toasts.Show(Txt.EqOffWithError(Name, error), ToastKind.Warning);
        else Toasts.Show(Txt.EqTurnedOff(Name, State == EqDeviceState.OtherEqualizer));
        if (!IsConnected) await _page.ListAsync(readSelected: false, profiles: false); // its journal record is gone, and so is its row
    }

    // ---------- profiles ----------
    /// <summary>Profiles change only when the config was read (never saved over one that couldn't be) and the device is connected.</summary>
    internal bool CanEdit => _page.Config is not null && IsConnected;
    public bool IsEditable => CanEdit;
    public string? ConfigError => _page.ConfigError;
    public string? GamemodeNote => _page.Bypassed ? Txt.EqBypassedNote : _gamemodeOn ? Txt.EqGamemodeNowOn : null;

    EqDevice? Find() => _page.Config?.Devices.FirstOrDefault(d => string.Equals(d.EndpointId, Id, StringComparison.OrdinalIgnoreCase));
    /// <summary>For a change only: adds the device to the config when it has no entry yet.</summary>
    EqDevice Entry() => _page.Config!.Device(Id, Name);

    public IReadOnlyList<OptionItem> ActiveItems { get => _activeItems; private set => Set(ref _activeItems, value); }
    public OptionItem? ActiveItem
    {
        get => _activeItem;
        set
        {
            if (_loading || value is null || value == _activeItem || !CanEdit) return; // null: the list being rebuilt
            var entry = Entry();
            entry.Active = Adopt(entry, value.Key);
            ShowProfiles();
            _page.Apply(this, Txt.EqActiveSet(_profile?.Name));
        }
    }
    public IReadOnlyList<OptionItem> GamemodeItems { get => _gamemodeItems; private set => Set(ref _gamemodeItems, value); }
    public OptionItem? GamemodeItem
    {
        get => _gamemodeItem;
        set
        {
            if (_loading || value is null || value == _gamemodeItem || !CanEdit) return;
            var entry = Entry();
            entry.Gamemode = Adopt(entry, value.Key);
            ShowProfiles();
            _page.Apply(this, Txt.EqGamemodeSet(entry.Gamemode));
        }
    }
    /// <summary>
    /// The picked name (null: "" = flat or none). A built-in preset the device doesn't have yet (<see cref="EqPresets"/>, listed with
    /// its profiles) becomes one of its profiles now, its own copy.
    /// </summary>
    static string? Adopt(EqDevice entry, string key)
    {
        if (key.Length == 0) return null;
        if (entry.Find(key) is null && EqPresets.Find(key) is { } preset) entry.Profiles.Add(preset);
        return key;
    }
    public bool HasProfile => _profile is not null;
    public string RenameText
    {
        get => _renameText;
        set { if (Set(ref _renameText, value ?? "")) { Raise(nameof(RenameProblem)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public string? RenameProblem
    {
        get
        {
            var name = _renameText.Trim();
            if (_profile is null || name == _profile.Name) return null;
            if (name.Length is 0 or > 64) return Txt.EqNameLength;
            return Find()?.Profiles.Any(p => p.Name == name) == true ? Txt.EqNameTaken : null;
        }
    }
    public ICommand NewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RenameCommand { get; }

    /// <summary>The profile lists and the editor from the config as it is now (after a read or a change); nothing is saved here.</summary>
    internal void ShowProfiles()
    {
        _loading = true;
        try
        {
            var entry = Find();
            // The device's profiles, then the built-in presets it doesn't have (picking one adds it: Adopt).
            var names = (entry?.Profiles.Select(p => p.Name) ?? []).Concat(EqPresets.All().Select(p => p.Name)).Distinct().ToList();
            // The lists are swapped only when the names changed (a pick from one of them runs this too, inside its own selection).
            if (_activeItems.Count == 0 || !_activeItems.Skip(1).Select(i => i.Key).SequenceEqual(names))
            {
                ActiveItems = [new("", Txt.EqFlat), .. names.Select(n => new OptionItem(n, n))];
                GamemodeItems = [new("", Txt.EqGamemodeNone), .. names.Select(n => new OptionItem(n, n))];
            }
            _profile = entry?.Find(entry.Active);
            _activeItem = ActiveItems.First(i => i.Key == (_profile?.Name ?? ""));
            _gamemodeItem = GamemodeItems.First(i => i.Key == (entry?.Find(entry.Gamemode)?.Name ?? ""));
            _renameText = _profile?.Name ?? "";
            foreach (var name in new[] { nameof(ActiveItem), nameof(GamemodeItem), nameof(RenameText), nameof(RenameProblem), nameof(HasProfile),
                         nameof(IsEditable), nameof(ConfigError), nameof(Sub) }) Raise(name);
            ShowEditor();
        }
        finally { _loading = false; }
        CommandManager.InvalidateRequerySuggested();
    }

    void NewProfile()
    {
        var profile = Equalizer.Graphic(10, new double[10]);
        profile.Name = UniqueName(Txt.EqNewName);
        Add(profile, Txt.EqCreated(profile.Name));
    }

    void Duplicate()
    {
        var copy = EqualizerPageViewModel.Copy(_profile!);
        copy.Name = UniqueName(Txt.EqCopyName(_profile!.Name));
        Add(copy, Txt.EqCreated(copy.Name));
    }

    void Add(EqProfile profile, string done)
    {
        var entry = Entry();
        entry.Profiles.Add(profile);
        entry.Active = profile.Name;
        ShowProfiles();
        _page.Apply(this, done);
    }

    /// <summary>Deleting the active profile leaves the device flat (and without a gamemode profile, if it was that one).</summary>
    void Delete()
    {
        var entry = Entry();
        var name = _profile!.Name;
        entry.Profiles.Remove(_profile);
        entry.Active = null;
        if (entry.Gamemode == name) entry.Gamemode = null;
        ShowProfiles();
        _page.Apply(this, Txt.EqDeleted(name));
    }

    /// <summary>The device refers to its profiles by name: both references follow.</summary>
    void Rename()
    {
        var entry = Entry();
        var (from, to) = (_profile!.Name, _renameText.Trim());
        _profile.Name = to;
        if (entry.Active == from) entry.Active = to;
        if (entry.Gamemode == from) entry.Gamemode = to;
        ShowProfiles();
        _page.Apply(this, Txt.EqRenamed(from, to));
    }

    /// <summary>The name, or with " 2", " 3"... when another profile has it; at most 64 characters.</summary>
    string UniqueName(string wanted)
    {
        var taken = Find()?.Profiles.Select(p => p.Name).ToHashSet() ?? [];
        wanted = wanted.Trim() is { Length: > 0 } w ? w : Txt.EqImportedName;
        var name = wanted.Length > 64 ? wanted[..64] : wanted;
        for (var i = 2; taken.Contains(name); i++)
        {
            var suffix = $" {i}";
            name = wanted[..Math.Min(wanted.Length, 64 - suffix.Length)] + suffix;
        }
        return name;
    }

    // ---------- the active profile's editor ----------
    public IReadOnlyList<OptionItem> Modes { get; }
    public ICommand SetModeCommand { get; }
    public bool IsParametric => _profile is { GraphicBands: 0 };
    public bool IsGraphic => _profile is { GraphicBands: > 0 };
    public IReadOnlyList<EqBandRow> Bands { get => _bands; private set { Set(ref _bands, value); Raise(nameof(NoBands)); } }
    public bool NoBands => IsParametric && _bands.Count == 0;
    public IReadOnlyList<EqGraphicRow> Graphic { get => _graphic; private set => Set(ref _graphic, value); }
    public ICommand AddBandCommand { get; }
    public IReadOnlyList<Point> Curve { get => _curve; private set => Set(ref _curve, value); }
    /// <summary>Why the profile can't play (shown, not played; edits aren't saved until it can).</summary>
    public string? Problem => _profile is null ? null : _bands.Select(b => b.TextProblem).FirstOrDefault(p => p is not null) ?? EqSettingsFile.Problem(_profile);

    public double Preamp
    {
        get => _profile?.Preamp ?? 0;
        set
        {
            if (_profile is null || value == Math.Clamp(_profile.Preamp, -30, 30)) return; // the slider's own limit coming back: not an edit
            _profile.Preamp = Math.Round(value * 2) / 2;
            Raise();
            Raise(nameof(PreampText));
            Edited();
        }
    }
    public string PreampText => Txt.EqDb(Preamp);
    public string SuggestedText => _profile is null ? "" : Txt.EqSuggested(Equalizer.SuggestedPreamp(_profile));
    public ICommand SuggestedCommand { get; }

    void ShowEditor()
    {
        var p = _profile;
        Bands = p is { GraphicBands: 0 } ? p.Bands.Select((b, i) => new EqBandRow(b, i + 1, Edited, RemoveBand)).ToList() : [];
        Graphic = p is { GraphicBands: > 0 } ? p.Bands.Select(b => new EqGraphicRow(b, Edited)).ToList() : [];
        foreach (var m in Modes) m.IsSelected = p is not null && m.Key == p.GraphicBands.ToString(CultureInfo.InvariantCulture);
        foreach (var name in new[] { nameof(Preamp), nameof(PreampText), nameof(IsParametric), nameof(IsGraphic), nameof(NoBands) }) Raise(name);
        ShowCurve();
    }

    void ShowCurve()
    {
        Curve = _profile is null ? [new(20, 0), new(20000, 0)] : Equalizer.Curve(_profile, 240).Select(c => new Point(c.Frequency, c.Db)).ToList();
        Raise(nameof(Problem));
        Raise(nameof(SuggestedText));
    }

    /// <summary>An edit of the active profile: the graph and the problem now, the save and Play once the edits rest.</summary>
    void Edited()
    {
        if (_loading) return;
        ShowCurve();
        _page.EditedLater(this);
    }

    /// <summary>
    /// Parametric keeps the graphic bands as peak filters; graphic takes the curve so far at its frequencies (within the sliders'
    /// range), so a switch changes the sound as little as the sliders allow.
    /// </summary>
    void SetMode(int bands)
    {
        var p = _profile!;
        if (bands == p.GraphicBands) return;
        if (bands > 0)
        {
            var shape = new EqProfile { Bands = p.Bands };
            var frequencies = bands switch { 10 => Equalizer.Graphic10, 15 => Equalizer.Graphic15, _ => Equalizer.Graphic31 };
            p.Bands = Equalizer.Graphic(bands, frequencies.Select(f => Math.Round(Math.Clamp(Equalizer.Response(shape, f), -EqGraphicRow.Range, EqGraphicRow.Range) * 2) / 2).ToList()).Bands;
        }
        p.GraphicBands = bands;
        ShowEditor();
        Edited();
    }

    void Suggested()
    {
        _profile!.Preamp = Equalizer.SuggestedPreamp(_profile);
        Raise(nameof(Preamp));
        Raise(nameof(PreampText));
        Edited();
    }

    void AddBand()
    {
        _profile!.Bands.Add(new EqBand { Type = EqFilterType.Peak, Frequency = 1000, Gain = 0, Q = 1 });
        ShowEditor();
        Edited();
    }

    void RemoveBand(EqBand band)
    {
        if (!CanEdit || _profile is null) return;
        _profile.Bands.Remove(band);
        ShowEditor();
        Edited();
    }

    // ---------- import and export ----------
    public string PasteText { get => _pasteText; set { if (Set(ref _pasteText, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }
    public ICommand ImportFileCommand { get; }
    public ICommand ImportPasteCommand { get; }
    public ICommand ExportCommand { get; }

    async Task ImportFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Txt.EqImportDialogTitle, Filter = Txt.EqFileFilter };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;
        string text;
        try { text = await Task.Run(() => new FileInfo(path).Length > 1 << 20 ? throw new InvalidDataException(Txt.EqImportTooBig) : File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Toasts.Show(Txt.EqImportFailed(ex.Message), ToastKind.Error);
            return;
        }
        Import(text, Path.GetFileNameWithoutExtension(path), pasted: false);
    }

    /// <summary>A new profile, made the active one; the lines it left out go to a warning.</summary>
    void Import(string text, string name, bool pasted)
    {
        if (!CanEdit) return;
        var (profile, ignored) = EqImport.Parse(text);
        if (profile.Bands.Count == 0 && profile.Preamp == 0)
        {
            Toasts.Show(Txt.EqImportNothing, ToastKind.Error);
            return;
        }
        profile.Name = UniqueName(name);
        if (pasted) PasteText = "";
        Add(profile, Txt.EqImported(profile.Name));
        if (ignored.Count > 0) Toasts.Show(Txt.EqImportIgnored(ignored), ToastKind.Warning);
    }

    void Export()
    {
        var p = _profile!;
        var invalid = Path.GetInvalidFileNameChars();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Txt.EqExportDialogTitle, Filter = Txt.EqFileFilter, DefaultExt = ".txt",
            FileName = string.Concat(p.Name.Select(c => invalid.Contains(c) ? '_' : c)) + ".txt",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, EqImport.Export(p));
            Toasts.Show(Txt.EqExported(p.Name, dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.Show(Txt.EqExportFailed(ex.Message), ToastKind.Error);
        }
    }
}

/// <summary>
/// KOS Equalizer: the playback and recording devices (and any it's still on for that aren't connected) on the left, the selected one on the right.
/// A visit only reads: the profiles (%ProgramData%\TheKOS\Audio\equalizer.json), each device and KOS Equalizer's state on it. Each
/// click changes one thing and reads it back; edits of the numbers are saved and played once they rest for 300 ms.
/// </summary>
public sealed class EqualizerPageViewModel : PageViewModel
{
    // What the page reads; the ui-shot probe swaps them for samples (the config and journal must be admin-owned, which a sample isn't).
    public static Func<List<AudioDevice>> ListDevices { get; set; } = AudioDevices.List;
    public static Func<IReadOnlyList<EqEndpointRecord>> JournalEndpoints { get; set; } = () => EqRegistration.LoadJournal().Endpoints;
    public static Func<string, EqStatus> ReadStatus { get; set; } = EqRegistration.Status;
    public static Func<EqConfig> LoadConfig { get; set; } = EqConfig.Load;

    readonly SemaphoreSlim _gate = new(1, 1); // the config, the settings files and the registration: one change at a time
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    List<EqDeviceViewModel> _devices = new();
    EqDeviceViewModel? _selected, _pending;
    string? _listError;
    bool _loaded, _bypassed;
    AsyncCommand? _master;

    public EqualizerPageViewModel() : base("equalizer", Txt.PageEqualizer, "", Txt.PageEqualizerSubtitle) // equalizer sliders
    {
        _debounce.Tick += (_, _) => MainViewModel.Fire(FlushAsync());
        SelectCommand = new RelayCommand(p => { if (p is EqDeviceViewModel d && d != _selected) MainViewModel.Fire(SelectAsync(d)); });
    }

    /// <summary>The list and the selected device scroll on their own, side by side.</summary>
    public override bool ScrollsItself => true;

    // The header's master switch: off plays every device flat (EqConfig.SetBypass); the registration and the profiles stay.
    public override ICommand? MasterCommand => _master ??= new AsyncCommand(ToggleMasterAsync, () => Config is not null && !IsBusy);
    public override bool MasterOn => !_bypassed;
    public override string? MasterText => _bypassed ? Txt.EqMasterOff : Txt.EqMasterOn;
    public override string? MasterTooltip => Txt.EqMasterTip;
    internal bool Bypassed => _bypassed;

    async Task ToggleMasterAsync()
    {
        var bypass = !_bypassed;
        string? error = null;
        List<string> differ = [];
        try { differ = await RunExclusiveAsync(() => EqConfig.SetBypass(bypass)); } // the devices whose file reads back otherwise
        catch (Exception ex)
        {
            Log.Error($"Equalizer: turning every device {(bypass ? "off" : "on")} failed", ex);
            error = ex.Message;
        }
        await ListAsync(readSelected: false, profiles: false); // the switch and every device as read now
        if (error is null && _bypassed == bypass && differ.Count == 0) Toasts.Show(bypass ? Txt.EqMasterTurnedOff : Txt.EqMasterTurnedOn);
        else if (error is null && _bypassed == bypass) Toasts.Show(Txt.EqMasterNotAll(bypass, differ), ToastKind.Warning);
        else Toasts.Show(Txt.EqMasterFailed(error ?? MasterText!), error is null ? ToastKind.Warning : ToastKind.Error);
    }
    public ICommand SelectCommand { get; }
    public IReadOnlyList<EqDeviceViewModel> Devices => _devices;
    public IReadOnlyList<EqDeviceViewModel> PlaybackDevices => _devices.Where(d => !d.IsRecording).ToList();
    public IReadOnlyList<EqDeviceViewModel> RecordingDevices => _devices.Where(d => d.IsRecording).ToList();
    public bool HasRecording => _devices.Any(d => d.IsRecording);
    public bool IsEmpty => _loaded && _devices.Count == 0 && _listError is null;
    public string? ListError { get => _listError; private set => Set(ref _listError, value); }
    public EqDeviceViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var d in _devices) d.IsSelected = ReferenceEquals(d, value);
        }
    }

    /// <summary>Null when it couldn't be read: then nothing is saved (an empty one would replace it).</summary>
    internal EqConfig? Config { get; private set; }
    internal string? ConfigError { get; private set; }
    /// <summary>A turn on or off is running.</summary>
    internal bool IsBusy => _devices.Any(d => d.IsBusy);
    /// <summary>Nothing waits to be saved and nothing is being written.</summary>
    internal bool Idle => _pending is null && _gate.CurrentCount == 1;

    public override async Task RefreshAsync()
    {
        await FlushAsync(); // edits waiting for their pause are saved before the config is read again
        await _gate.WaitAsync();
        try
        {
            Config = await Task.Run(LoadConfig);
            ConfigError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Equalizer: reading the profiles failed", ex);
            (Config, ConfigError) = (null, Txt.EqConfigFailed(ex.Message));
        }
        finally { _gate.Release(); }
        foreach (var d in _devices) d.ShowProfiles(); // the new read's profiles now, before an edit could land on the old ones
        await ListAsync(readSelected: true, profiles: false);
    }

    sealed record DeviceRead(string Id, string Name, AudioDevice? Device, EqStatus? Status, string? StatusError);

    /// <summary>
    /// Lists the devices again; those still there keep their view model. <paramref name="profiles"/>: the config was read again, so
    /// every device shows its profiles from it. The selected device is read again (with its formats) when asked or when it moved.
    /// </summary>
    internal async Task ListAsync(bool readSelected, bool profiles)
    {
        var (reads, error, gamemode, bypassed) = await Task.Run(ReadDevices);
        ListError = error;
        _bypassed = bypassed; // before the devices' notes are raised
        Raise(nameof(MasterOn));
        Raise(nameof(MasterText));
        var old = _devices;
        _devices = reads.Select(r =>
        {
            var vm = old.FirstOrDefault(o => o.Id == r.Id && o.IsConnected == (r.Device is not null))?.Update(r.Device);
            if (vm is null || profiles) (vm ??= new EqDeviceViewModel(this, r.Id, r.Name, r.Device)).ShowProfiles();
            vm.Show(r.Status, r.StatusError, gamemode);
            return vm;
        }).ToList();
        _loaded = true;
        Raise(nameof(Devices));
        Raise(nameof(PlaybackDevices));
        Raise(nameof(RecordingDevices));
        Raise(nameof(HasRecording));
        Raise(nameof(IsEmpty));
        var next = _selected is { } s && _devices.Contains(s) ? s : _devices.FirstOrDefault(d => d.Settings?.IsDefault == true) ?? _devices.FirstOrDefault();
        var moved = next != _selected;
        Selected = next;
        if (next?.Settings is { } settings && (readSelected || moved)) await settings.RefreshAsync();
    }

    /// <summary>Off the UI thread: the playback and recording devices, the journal's devices that aren't among them, and each one's state.</summary>
    static (List<DeviceRead> Reads, string? Error, bool Gamemode, bool Bypassed) ReadDevices()
    {
        var errors = new List<string>();
        var devices = new List<AudioDevice>();
        try { devices = ListDevices().Where(d => d.Flow is AudioFlow.Playback or AudioFlow.Recording).ToList(); }
        catch (Exception ex)
        {
            Log.Error("Equalizer: listing the audio devices failed", ex);
            errors.Add(Txt.AudioReadFailed(ex.Message));
        }
        IReadOnlyList<EqEndpointRecord> records = [];
        try { records = JournalEndpoints(); }
        catch (Exception ex)
        {
            Log.Error("Equalizer: reading the registration journal failed", ex);
            errors.Add(Txt.EqJournalFailed(ex.Message));
        }
        // A device KOS Equalizer is on for stays listed when it isn't connected: its record keeps the registration until it's turned off.
        var missing = records.Where(r => !devices.Any(d => EqRegistration.SameEndpoint(d.Id, r.EndpointId))).ToList();
        missing = missing.Where((r, i) => !missing.Take(i).Any(m => EqRegistration.SameEndpoint(m.EndpointId, r.EndpointId))).ToList();
        var reads = devices.Select(d => (d.Id, d.Name, Device: (AudioDevice?)d))
            .Concat(missing.Select(r => (Id: r.EndpointId, r.Name, Device: (AudioDevice?)null)))
            .Select(x =>
            {
                try { return new DeviceRead(x.Id, x.Name, x.Device, ReadStatus(x.Id), null); }
                catch (Exception ex)
                {
                    Log.Warn($"Equalizer: reading the state of {x.Name} failed: {ex.Message}");
                    return new DeviceRead(x.Id, x.Name, x.Device, null, Txt.EqStatusFailed(ex.Message));
                }
            }).ToList();
        bool gamemode, bypassed;
        try { (gamemode, bypassed) = (EqConfig.GamemodeOn, EqConfig.Bypassed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { (gamemode, bypassed) = (false, false); }
        return (reads, errors.Count == 0 ? null : string.Join(" ", errors), gamemode, bypassed);
    }

    async Task SelectAsync(EqDeviceViewModel d)
    {
        var flush = FlushAsync(); // the other device's edits
        Selected = d;
        await flush;
        await d.ReadStatusAsync();
        if (d.Settings is { } settings) await settings.RefreshAsync();
    }

    // ---------- saving and playing ----------

    /// <summary>An edit of the numbers: saved and played once the edits rest for 300 ms.</summary>
    internal void EditedLater(EqDeviceViewModel d)
    {
        _pending = d;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The edits waiting for their pause, now; not when the profile has a problem (shown on the page instead).</summary>
    internal async Task FlushAsync()
    {
        _debounce.Stop();
        if (_pending is not { } d) return;
        _pending = null;
        if (d.Problem is null) await SaveAndPlayAsync(d, null);
    }

    /// <summary>A change to the profiles (not to the numbers): saved and played now, with a toast.</summary>
    internal void Apply(EqDeviceViewModel d, string done)
    {
        _debounce.Stop();
        _pending = null; // saved with it
        MainViewModel.Fire(SaveAndPlayAsync(d, done));
    }

    /// <summary>Waits for what's pending and in progress (the window closes after it).</summary>
    internal async Task SettleAsync()
    {
        await FlushAsync();
        await _gate.WaitAsync();
        _gate.Release();
    }

    /// <summary>
    /// Saves the profiles, then what the device should play goes to its settings file (<see cref="EqConfig.Play"/>, which writes
    /// nothing while KOS Equalizer is off for it and reads the file back): off the UI thread, on a copy taken now, one change at a time.
    /// </summary>
    internal async Task SaveAndPlayAsync(EqDeviceViewModel d, string? done)
    {
        if (Config is null) return;
        await _gate.WaitAsync();
        try
        {
            var copy = Copy(Config); // the latest edits; the page goes on editing its own while this one is written
            var (id, on) = (d.Id, d.IsOn);
            var playing = await Task.Run(() =>
            {
                copy.Save();
                var entry = copy.Devices.FirstOrDefault(x => string.Equals(x.EndpointId, id, StringComparison.OrdinalIgnoreCase));
                if (entry is null) return null;
                EqConfig.Play(entry);
                return EqConfig.Bypassed ? null : entry.Playing(EqConfig.GamemodeOn)?.Name; // flat while the master switch is off
            });
            if (done is not null) Toasts.Show($"{done} {Txt.EqPlays(d.Name, on, playing)}");
        }
        catch (Exception ex)
        {
            Log.Error($"Equalizer: saving or playing {d.Name}'s profile failed", ex);
            Toasts.Show(Txt.EqApplyFailed(d.Name, ex.Message), ToastKind.Error);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Turning on or off: after the edits waiting, off the UI thread, never during a save.</summary>
    internal async Task<T> RunExclusiveAsync<T>(Func<T> change)
    {
        await FlushAsync();
        await _gate.WaitAsync();
        try { return await Task.Run(change); }
        finally { _gate.Release(); }
    }

    /// <summary>A deep copy (every property, also ones added to the Core later).</summary>
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
