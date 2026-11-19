using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Audio;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// One audio device's settings: default device, volume and mute, format, exclusive mode and enhancements. The constructor reads
/// nothing; <see cref="RefreshAsync"/> reads the device and the formats it takes. Each control writes its own change once (off the UI
/// thread, one at a time), then the device is read back and a toast says what it has. The Audio page and KOS Equalizer show it
/// (its template is in Views/AudioTemplates.xaml). Create it on the UI thread: the volume's pause timer runs there.
/// </summary>
public sealed class AudioDeviceSettingsViewModel : ObservableObject
{
    readonly SemaphoreSlim _gate = new(1, 1); // reads and writes in the order they came
    readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    AudioDevice _device;
    IReadOnlyList<AudioFormat> _supported = Array.Empty<AudioFormat>(), _formats;
    AudioFormat? _chosen;
    int _volume;
    bool _selected, _gone;
    string? _error;

    public AudioDeviceSettingsViewModel(AudioDevice device)
    {
        _device = device;
        _volume = AudioSettings.Percent(device.Volume);
        _formats = AudioSettings.FormatChoices(_supported, device.Format);
        _chosen = device.Format;
        // The slider only moves the number; the write comes once it rests for a moment.
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            var level = _volume;
            if (_gone || level == AudioSettings.Percent(_device.Volume)) return;
            MainViewModel.Fire(Write(Txt.AudioVolumeTitle, id => AudioDevices.SetVolume(id, level / 100f),
                d => AudioSettings.Percent(d.Volume) == level, d => Txt.AudioVolumeState(AudioSettings.Percent(d.Volume))));
        };
        MakeDefaultCommand = new AsyncCommand(() => Write(Txt.AudioMakeDefault, id => AudioDevices.SetDefault(id, false), d => d.Default, DefaultState),
            () => !_gone && !_device.Default);
        MakeCommunicationsCommand = new AsyncCommand(() => Write(Txt.AudioMakeCommunications, id => AudioDevices.SetDefault(id, true), d => d.DefaultCommunications, DefaultState),
            () => !_gone && !_device.DefaultCommunications);
        ToggleMuteCommand = new AsyncCommand(() =>
        {
            var mute = !_device.Muted;
            return Write(Txt.AudioMute, id => AudioDevices.SetMute(id, mute), d => d.Muted == mute, d => Txt.AudioMuteState(d.Muted));
        }, () => !_gone);
        ApplyFormatCommand = new AsyncCommand(() => _chosen is { } f
            ? Write(Txt.AudioFormatTitle, id => AudioDevices.SetFormat(id, f), d => d.Format == f, d => Txt.AudioFormatText(d.Format))
            : Task.CompletedTask, () => !_gone && _chosen is not null && _chosen != _device.Format);
        ToggleExclusiveCommand = new AsyncCommand(() => Exclusive(!ExclusiveAllowed, _device.ExclusivePriority ?? true), () => !_gone && HasExclusive);
        TogglePriorityCommand = new AsyncCommand(() => Exclusive(true, !ExclusivePriority), () => !_gone && ExclusiveAllowed);
        ToggleEnhancementsCommand = new AsyncCommand(() =>
        {
            var off = !EnhancementsOff;
            return Write(Txt.AudioEnhancementsTitle, id => AudioDevices.SetEnhancementsOff(id, off), d => d.EnhancementsOff == off, d => Txt.AudioEnhancementsState(d.EnhancementsOff));
        }, () => !_gone && HasEnhancements);
    }

    /// <summary>After every write and its read-back (also a failed one): the Audio page lists the devices again (another one may have lost the default).</summary>
    public event EventHandler? Applied;

    public string Id => _device.Id;
    public string Name => _device.Name;
    public AudioFlow Flow => _device.Flow;
    public string FlowText => _device.Flow == AudioFlow.Playback ? Txt.AudioPlaybackDevice : Txt.AudioRecordingDevice;
    public string Glyph => _device.Flow == AudioFlow.Playback ? "" : ""; // speakers, microphone
    public string Sub => Txt.AudioRowText(_device);
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool IsGone => _gone;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public bool IsDefault => _device.Default;
    public bool IsCommunications => _device.DefaultCommunications;
    public string DefaultText => Txt.AudioDefaultLine(_device.Default, _device.DefaultCommunications);
    public ICommand MakeDefaultCommand { get; }
    public ICommand MakeCommunicationsCommand { get; }

    /// <summary>The slider's value in percent: the last read, or the one waiting to be written.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var v = (int)Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, v)) return;
            Raise(nameof(VolumeText));
            _volumeTimer.Stop();
            _volumeTimer.Start();
        }
    }
    public string VolumeText => Txt.AudioVolumeText(_volume);
    public bool Muted => _device.Muted;
    public ICommand ToggleMuteCommand { get; }

    public bool HasFormat => _device.Format is not null;
    public IReadOnlyList<AudioFormat> Formats => _formats;
    public bool FormatOnly => _formats.Count <= 1;
    /// <summary>The format picked in the list; only Apply writes it.</summary>
    public AudioFormat? ChosenFormat
    {
        get => _chosen;
        set { if (value is not null && Set(ref _chosen, value)) CommandManager.InvalidateRequerySuggested(); } // a list being swapped sets null
    }
    public ICommand ApplyFormatCommand { get; }

    public bool HasExclusive => _device.ExclusiveAllowed is not null;
    public bool ExclusiveAllowed => _device.ExclusiveAllowed == true;
    public bool ExclusivePriority => ExclusiveAllowed && _device.ExclusivePriority == true;
    public ICommand ToggleExclusiveCommand { get; }
    public ICommand TogglePriorityCommand { get; }

    /// <summary>Null on a device without enhancements (no effects to turn off).</summary>
    public bool HasEnhancements => _device.EnhancementsOff is not null;
    public bool EnhancementsOff => _device.EnhancementsOff == true;
    public ICommand ToggleEnhancementsCommand { get; }

    /// <summary>Reads the device and the formats it takes (COM, off the UI thread).</summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try { await ReadAsync(formats: true); }
        finally { _gate.Release(); }
    }

    async Task ReadAsync(bool formats)
    {
        var id = Id;
        try
        {
            var (read, supported) = await Task.Run(() =>
            {
                var d = AudioDevices.Read(id);
                return (d, formats && d is not null ? SupportedFormats(id) : null);
            });
            if (read is null) { Gone(); return; }
            if (supported is not null) _supported = supported;
            Error = null;
            Show(read);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or InvalidCastException)
        {
            Log.Warn($"Audio: reading {Name} failed: {ex.Message}");
            Error = Txt.AudioReadFailed(ex.Message);
        }
    }

    static List<AudioFormat>? SupportedFormats(string id)
    {
        try { return AudioDevices.SupportedFormats(id); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or InvalidCastException)
        {
            Log.Warn($"Audio: reading the formats of {id} failed: {ex.Message}");
            return null; // the current format is still offered
        }
    }

    /// <summary>The page's list read: the same device as read just now.</summary>
    internal AudioDeviceSettingsViewModel Update(AudioDevice device)
    {
        Show(device);
        return this;
    }

    /// <summary>
    /// The only place that changes the device: the write, then the read-back, both off the UI thread; the toast says what the device
    /// has (a warning when it isn't what was asked).
    /// </summary>
    async Task Write(string setting, Action<string> write, Func<AudioDevice, bool> took, Func<AudioDevice, string> state)
    {
        await _gate.WaitAsync();
        var id = Id;
        try
        {
            Log.Info($"Audio: {Name}: {setting}.");
            var read = await Task.Run(() => { write(id); return AudioDevices.Read(id); });
            if (read is null)
            {
                Gone();
                Toasts.Show(Txt.AudioFailed(Name, setting, Txt.AudioGone), ToastKind.Error);
            }
            else
            {
                Show(read);
                if (took(read)) Toasts.Show(Txt.AudioApplied(read.Name, state(read)));
                else
                {
                    Toasts.Show(Txt.AudioNotConfirmed(read.Name, setting, state(read)), ToastKind.Warning);
                    Log.Warn($"Audio: {Name}: {setting} not confirmed: {state(read)}.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or InvalidCastException)
        {
            Log.Warn($"Audio: {Name}: {setting} failed: {ex.Message}");
            Toasts.Show(Txt.AudioFailed(Name, setting, ex.Message), ToastKind.Error);
            await ReadAsync(formats: false); // the controls go back to what the device has
        }
        finally
        {
            _gate.Release();
            Raise(string.Empty); // a switch the click moved shows the read-back, also when the value didn't change
        }
        Applied?.Invoke(this, EventArgs.Empty);
    }

    Task Exclusive(bool allow, bool priority) => Write(Txt.AudioExclusiveTitle, id => AudioDevices.SetExclusive(id, allow, priority),
        d => AudioSettings.ExclusiveTook(d, allow, priority), d => Txt.AudioExclusiveState(d.ExclusiveAllowed, d.ExclusivePriority));

    static string DefaultState(AudioDevice d) => Txt.AudioDefaultState(d.Default, d.DefaultCommunications);

    void Show(AudioDevice read)
    {
        var picked = _chosen is not null && _chosen != _device.Format; // chosen in the list, not applied (or not taken)
        _device = read;
        if (!_volumeTimer.IsEnabled) _volume = AudioSettings.Percent(read.Volume); // a value waiting to be written stays on the slider
        var formats = AudioSettings.FormatChoices(_supported, read.Format);
        if (!formats.SequenceEqual(_formats)) _formats = formats;
        // Otherwise the list follows the device, also when another program changed its format.
        _chosen = picked && _formats.Contains(_chosen!) ? _chosen : read.Format;
        Raise(string.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    void Gone()
    {
        _gone = true;
        _volumeTimer.Stop();
        Error = Txt.AudioGone;
        Raise(nameof(IsGone));
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// Audio: the active playback and recording devices (as Windows enumerates them) and the selected one's settings. Reading only;
/// every change comes from a control of the selected device.
/// </summary>
public sealed class AudioPageViewModel : PageViewModel
{
    List<AudioDeviceSettingsViewModel> _devices = new();
    AudioDeviceSettingsViewModel? _selected;
    string? _loadError;
    bool _loaded;

    public AudioPageViewModel() : base("audio", Txt.PageAudio, "", Txt.PageAudioSubtitle) // a speaker with sound waves
    {
        SelectCommand = new RelayCommand(p =>
        {
            if (p is not AudioDeviceSettingsViewModel d || d == _selected) return;
            Selected = d;
            MainViewModel.Fire(d.RefreshAsync()); // its formats
        });
    }

    /// <summary>The list and the selected device scroll on their own, side by side.</summary>
    public override bool ScrollsItself => true;
    public ICommand SelectCommand { get; }
    public IReadOnlyList<AudioDeviceSettingsViewModel> Playback => _devices.Where(d => d.Flow == AudioFlow.Playback).ToList();
    public IReadOnlyList<AudioDeviceSettingsViewModel> Recording => _devices.Where(d => d.Flow == AudioFlow.Recording).ToList();
    public bool HasPlayback => _devices.Any(d => d.Flow == AudioFlow.Playback);
    public bool HasRecording => _devices.Any(d => d.Flow == AudioFlow.Recording);
    public bool IsEmpty => _loaded && _devices.Count == 0 && _loadError is null;
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }

    public AudioDeviceSettingsViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var d in _devices) d.IsSelected = ReferenceEquals(d, value);
        }
    }

    public override Task RefreshAsync() => ListAsync(readSelected: true);

    /// <summary>
    /// Lists the devices again; those still there keep their view model (the selected panel stays as it is). The selected device is read
    /// again (with its formats) when asked or when the selection moved.
    /// </summary>
    async Task ListAsync(bool readSelected)
    {
        List<AudioDevice> list;
        try
        {
            list = await Task.Run(AudioDevices.List);
            LoadError = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or InvalidCastException)
        {
            Log.Error("Reading audio devices failed", ex);
            list = new();
            LoadError = Txt.AudioReadFailed(ex.Message);
        }
        var old = _devices.ToDictionary(d => d.Id);
        _devices = list.Select(d => old.TryGetValue(d.Id, out var vm) ? vm.Update(d) : New(d)).ToList();
        _loaded = true;
        foreach (var n in new[] { nameof(Playback), nameof(Recording), nameof(HasPlayback), nameof(HasRecording), nameof(IsEmpty) }) Raise(n);
        var next = _selected is { } s && _devices.Contains(s) ? s
            : _devices.FirstOrDefault(d => d.Flow == AudioFlow.Playback && d.IsDefault) ?? _devices.FirstOrDefault();
        var moved = next != _selected;
        Selected = next;
        if (next is not null && (readSelected || moved)) await next.RefreshAsync();
    }

    AudioDeviceSettingsViewModel New(AudioDevice d)
    {
        var vm = new AudioDeviceSettingsViewModel(d);
        vm.Applied += (_, _) => MainViewModel.Fire(ListAsync(readSelected: false));
        return vm;
    }
}
