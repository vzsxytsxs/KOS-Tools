using System.Windows.Input;
using KosToolbox.Core.Debloat;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.ViewModels;

/// <summary>
/// One function of an app's debloater, shown as a box: ticking it runs the function and unticking undoes it, when it can be undone.
/// The box follows what reading the system back says, not the click.
/// </summary>
public sealed class DebloatOptionViewModel(DebloatApp app, DebloatOption option, Func<Task> refreshAll) : OperationViewModel
{
    DebloatState _state = DebloatState.NotApplied;

    public string Name => option.Name;
    public string Description => option.ClosesApp ? $"{option.Description} {Txt.DebloatClosesApp}" : option.Description;
    public bool IsPreset => option.IsPreset;
    public bool ShowAdminBadge => option.NeedsAdmin && !Elevation.IsAdmin;
    /// <summary>Applied with no undo (what comes back only by reinstalling): nothing left to do on the box.</summary>
    public bool CanClick => !IsBusy && !(_state == DebloatState.Applied && option.Undo is null);

    /// <summary>
    /// Null is partly applied; a one-shot function has no state, so its box is never left ticked. A click goes by the state, not by the
    /// value the box proposes: from partly applied it finishes the job, while the box itself would propose unticking.
    /// </summary>
    public bool? IsChecked
    {
        get => _state switch { DebloatState.Applied => true, DebloatState.Partial => null, _ => false };
        set => MainViewModel.Fire(RunOptionAsync(undo: _state == DebloatState.Applied));
    }

    protected override void OnStateChanged() => Raise(nameof(CanClick));

    public void Update(DebloatState state)
    {
        _state = state;
        Raise(nameof(IsChecked));
        Raise(nameof(CanClick));
    }

    public DebloatState Read() => option.Read();

    async Task RunOptionAsync(bool undo)
    {
        var after = _state;
        var ok = await RunAsync(async (ct, started) =>
        {
            return await TweakEngine.Instance.RunExclusiveAsync<(bool, string)>(async () =>
            {
                started();
                if (option.NeedsAdmin && !Elevation.IsAdmin)
                    await ElevatedRunner.RunAsync(new ElevatedJob { Debloat = new DebloatJob { App = app.Id, Option = option.Id, Undo = undo } }, ct);
                else await DebloatCatalog.RunAsync(app, option, undo, ct);
                // Only what reading it back confirms is reported as done.
                after = option.Read();
                Log.Info($"Debloat {app.Id}/{option.Id}{(undo ? " (undo)" : "")}: {after}");
                return after switch
                {
                    DebloatState.OneShot => (true, Txt.DebloatDone),
                    DebloatState.Applied when !undo => (true, Txt.DebloatConfirmed),
                    DebloatState.NotApplied when undo => (true, Txt.DebloatUndone),
                    _ => (false, Txt.DebloatNotConfirmed),
                };
            });
        }, undo ? Txt.DebloatUndoing : Txt.DebloatApplying);
        // The box already says it; a one-shot function has only this message to show.
        if (ok && after != DebloatState.OneShot) Message = null;
        if (!undo && after != DebloatState.NotApplied && option.Warning is { } warning) Toasts.Show(warning, ToastKind.Warning);
        // A preset changes its functions and a function changes the presets; a failed run may also have done part of the work.
        await refreshAll();
    }
}

/// <summary>The form that creates an OBS profile: resolution, frame rate and encoder.</summary>
public sealed class ObsProfileViewModel : OperationViewModel
{
    readonly DebloatApp _app;
    readonly ObsInstall _obs = ObsDebloat.Default;
    OptionItem _resolution, _fps, _encoder;
    string _customFps = "144";
    string _active = "";

    public ObsProfileViewModel(DebloatApp app)
    {
        _app = app;
        Resolutions = ObsProfile.Resolutions.Keys.Select(h => new OptionItem(h.ToString(), $"{h}p")).ToList();
        FpsOptions = new[] { new OptionItem("30", "30"), new OptionItem("60", "60"), new OptionItem("120", "120"), new OptionItem("custom", Txt.ObsCustomFps) };
        Encoders = new[] { new OptionItem(nameof(ObsEncoder.Nvenc), Txt.ObsEncoderNvenc), new OptionItem(nameof(ObsEncoder.Amf), Txt.ObsEncoderAmf), new OptionItem(nameof(ObsEncoder.Qsv), Txt.ObsEncoderQsv) };
        _resolution = Resolutions[0];
        _fps = FpsOptions[1];
        _encoder = Encoders.First(e => e.Key == ObsDebloat.DefaultEncoder().ToString());
        CreateCommand = new AsyncCommand(CreateAsync, () => !IsBusy);
    }

    public IReadOnlyList<OptionItem> Resolutions { get; }
    public IReadOnlyList<OptionItem> FpsOptions { get; }
    public IReadOnlyList<OptionItem> Encoders { get; }
    public OptionItem Resolution { get => _resolution; set => Set(ref _resolution, value); }
    public OptionItem Encoder { get => _encoder; set => Set(ref _encoder, value); }
    public OptionItem Fps
    {
        get => _fps;
        set { if (Set(ref _fps, value)) Raise(nameof(IsCustomFps)); }
    }
    public bool IsCustomFps => _fps.Key == "custom";
    public string CustomFps { get => _customFps; set => Set(ref _customFps, value); }
    public string ActiveText { get => _active; private set => Set(ref _active, value); }
    public ICommand CreateCommand { get; }

    public void Refresh() => ActiveText = Txt.ObsActiveProfile(ObsDebloat.ActiveProfile(_obs));

    async Task CreateAsync()
    {
        var fpsText = IsCustomFps ? _customFps : _fps.Key;
        await RunAsync(async (ct, started) =>
        {
            if (!int.TryParse(fpsText, out var fps)) throw new InvalidOperationException(Txt.ObsInvalidProfile);
            var profile = new ObsProfile(int.Parse(_resolution.Key), fps, Enum.Parse<ObsEncoder>(_encoder.Key));
            return await TweakEngine.Instance.RunExclusiveAsync<(bool, string)>(async () =>
            {
                started();
                DebloatCatalog.RefuseElevatedUserChange();
                await DebloatCatalog.CloseAsync(_app, ct);
                ObsDebloat.CreateProfile(_obs, profile);
                return (ObsDebloat.ActiveProfile(_obs) == profile.Name, Txt.ObsProfileCreated(profile.Name));
            });
        }, Txt.DebloatApplying);
        Refresh();
    }
}

/// <summary>An installed app and its options; apps that aren't installed don't appear.</summary>
public sealed class DebloatAppViewModel : ObservableObject
{
    bool _isExpanded;
    string _header;

    public DebloatAppViewModel(DebloatApp app, string? version, bool expanded)
    {
        App = app;
        _header = Txt.DebloatHeader(app.Name, version);
        _isExpanded = expanded;
        Options = app.Options.Select(o => new DebloatOptionViewModel(app, o, RefreshAsync)).ToList();
        ObsProfile = app.Id == "obs" ? new ObsProfileViewModel(app) : null;
    }

    public DebloatApp App { get; }
    public string Icon => $"pack://application:,,,/KosToolbox;component/Assets/Setup/{App.Icon}.png";
    public string Header { get => _header; set => Set(ref _header, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public IReadOnlyList<DebloatOptionViewModel> Options { get; }
    public IEnumerable<DebloatOptionViewModel> Presets => Options.Where(o => o.IsPreset);
    public IEnumerable<DebloatOptionViewModel> Functions => Options.Where(o => !o.IsPreset);
    public bool HasPresets => Presets.Any();
    public ObsProfileViewModel? ObsProfile { get; }

    /// <summary>Reads off the UI thread; a failed read leaves that box as it was and goes to the log.</summary>
    public async Task RefreshAsync()
    {
        ObsProfile?.Refresh();
        foreach (var option in Options)
        {
            if (option.IsBusy) continue;
            try { option.Update(await Task.Run(option.Read)); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"Debloat: reading {App.Id}/{option.Name} failed: {ex.Message}"); }
        }
    }
}

/// <summary>Debloat page: the supported apps that are installed, each with its functions (formerly a tab of Setup).</summary>
public sealed class DebloatPageViewModel() : PageViewModel("debloat", Txt.PageDebloat, "\uE71C", Txt.PageDebloatSubtitle)
{
    IReadOnlyList<DebloatAppViewModel> _debloatApps = Array.Empty<DebloatAppViewModel>();

    public IReadOnlyList<DebloatAppViewModel> DebloatApps { get => _debloatApps; private set { if (Set(ref _debloatApps, value)) Raise(nameof(HasDebloatApps)); } }
    public bool HasDebloatApps => _debloatApps.Count > 0;

    public override Task RefreshAsync() => RefreshDebloaterAsync();

    /// <summary>Only the installed apps appear; the states are read again on every visit, because an app update can undo them.</summary>
    async Task RefreshDebloaterAsync()
    {
        var found = await Task.Run(() => DebloatCatalog.Apps.Select(a => (App: a, Exe: a.Executable())).Where(x => x.Exe is not null)
            .Select(x => (x.App, Version: x.App.InstalledVersion(x.Exe!))).ToList());
        if (!found.Select(f => f.App.Id).SequenceEqual(_debloatApps.Select(a => a.App.Id)))
            DebloatApps = found.Select((f, i) => new DebloatAppViewModel(f.App, f.Version, expanded: i == 0)).ToList();
        else for (var i = 0; i < found.Count; i++) _debloatApps[i].Header = Txt.DebloatHeader(found[i].App.Name, found[i].Version);
        foreach (var app in _debloatApps) await app.RefreshAsync();
    }
}
