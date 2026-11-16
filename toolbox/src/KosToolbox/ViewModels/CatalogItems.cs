using System.Windows.Input;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>Catalog item shown on pages and in search.</summary>
public interface ICatalogItem
{
    string Page { get; }
    string? Tab { get; }
    string? Group { get; }
    string SearchText { get; }
    Task RefreshAsync();
    /// <summary>Reads the state only if it has not been read yet (used by search).</summary>
    Task EnsureLoadedAsync();
}

public sealed class TweakViewModel : OperationViewModel, ICatalogItem
{
    static TweakEngine Engine => TweakEngine.Instance;

    TweakState? _state;
    TweakBackup? _backup;
    string? _scriptProblem;
    bool _rebootPending, _showRestartExplorer;
    OptionItem? _pending;

    public TweakViewModel(Tweak tweak)
    {
        Tweak = tweak;
        Options = tweak.Options.Select(o => new OptionItem(o.Key, o.Label)).ToList();
        SelectCommand = new AsyncCommand(p => ApplyAsync((string)p!), _ => CanChange);
        ApplyPendingCommand = new AsyncCommand(() => ApplyAsync(_pending!.Key), () => CanChange && PendingDiffers);
        RestorePreviousCommand = new AsyncCommand(() => RestoreAsync(RestoreTarget.Previous), () => CanChange && _backup?.Previous is not null);
        RestoreOriginalCommand = new AsyncCommand(() => RestoreAsync(RestoreTarget.Original), () => CanChange && _backup?.Original is not null);
        KosDefaultCommand = new AsyncCommand(() => ApplyAsync(KosDefault!), () => CanChange && KosDefault is not null);
        WindowsDefaultCommand = new AsyncCommand(() => ApplyAsync(tweak.WindowsDefault!), () => CanChange && tweak.WindowsDefault is not null);
        DetailsCommand = new AsyncCommand(ShowDetailsAsync);
        RestartExplorerCommand = new AsyncCommand(async () =>
        {
            var action = KosCatalog.Actions.First(a => a.Id == "restart-explorer");
            await RunAsync(async (ct, started) => (true, await Engine.RunActionAsync(action, ct, started)), Txt.RestartingExplorer);
            ShowRestartExplorer = false;
        });
    }

    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Page => Tweak.Page;
    public string? Tab => Tweak.Tab;
    public string? Group => Tweak.Group;
    public string SearchText => Tweak.SearchText;
    public bool IsChoice => Tweak.Kind == TweakKind.Choice;
    public IReadOnlyList<OptionItem> Options { get; }
    public bool RequiresReboot => Tweak.RequiresReboot;
    public bool ShowAdminBadge => Tweak.NeedsAdmin && !Elevation.IsAdmin;

    /// <summary>KOS default for this installation's security profile.</summary>
    public string? KosDefault => Tweak.DefaultFor(KosProfiles.Current);
    public string KosDefaultLabel => Txt.KosDefaultMenu(Label(KosDefault), Tweak.ProfileDefaults is null ? null : KosProfiles.Current?.ToString());
    public string WindowsDefaultLabel => Txt.WindowsDefaultMenu(Label(Tweak.WindowsDefault));

    public ICommand SelectCommand { get; }
    public ICommand ApplyPendingCommand { get; }
    public ICommand RestorePreviousCommand { get; }
    public ICommand RestoreOriginalCommand { get; }
    public ICommand KosDefaultCommand { get; }
    public ICommand WindowsDefaultCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand RestartExplorerCommand { get; }

    public TweakState? State
    {
        get => _state;
        private set
        {
            var old = _state;
            if (!Set(ref _state, value)) return;
            foreach (var o in Options) o.IsSelected = value is { Status: TweakStatus.Matched } && value.Option == o.Key;
            // Readings changes on every read; the pending choice is discarded only if the state actually changed.
            if (old?.Status != value?.Status || old?.Option != value?.Option)
                PendingOption = Options.FirstOrDefault(o => o.IsSelected);
            OnStateChanged();
        }
    }

    /// <summary>Option chosen in the list and not yet applied (lists only; avoids applying on every arrow key).</summary>
    public OptionItem? PendingOption
    {
        get => _pending;
        set { if (Set(ref _pending, value)) Raise(nameof(PendingDiffers)); }
    }
    public bool PendingDiffers => _pending is { IsSelected: false } && CanChange;

    /// <summary>
    /// A full option can be applied once the state has been read (including Partial, Not configured and Unknown).
    /// Read error and Unavailable block it: what could not be read is not changed.
    /// </summary>
    public bool CanChange => !IsBusy && _scriptProblem is null
        && _state?.Status is TweakStatus.Matched or TweakStatus.Partial or TweakStatus.NotConfigured or TweakStatus.Unknown;

    /// <summary>State badge when no option matches. Unknown or Read error never become Disabled.</summary>
    public string? StatusText => _state is null ? Txt.Reading : _state.Status == TweakStatus.Matched ? null : Txt.StatusName(_state.Status, null);
    public bool IsProblem => _state?.Status is TweakStatus.ReadError or TweakStatus.Unavailable;

    public string? StatusDetail => _scriptProblem ?? (_state?.Status is TweakStatus.Matched or null ? null : _state.Detail);
    public bool ShowDetailLine => !string.IsNullOrEmpty(StatusDetail);
    public string? PartialTooltip => _state is { Status: TweakStatus.Partial } ? Txt.PartialTooltip : null;

    public bool RebootPending { get => _rebootPending; private set => Set(ref _rebootPending, value); }
    public bool ShowRestartExplorer { get => _showRestartExplorer; private set => Set(ref _showRestartExplorer, value); }

    public string? Label(string? key) => key is null ? null : Tweak.Options.FirstOrDefault(o => o.Key == key)?.Label;
    public string CurrentLabel => _state is { Status: TweakStatus.Matched } s ? Label(s.Option)! : StatusText ?? Txt.Unknown;

    protected override void OnStateChanged()
    {
        Raise(nameof(CanChange));
        Raise(nameof(StatusText));
        Raise(nameof(IsProblem));
        Raise(nameof(StatusDetail));
        Raise(nameof(ShowDetailLine));
        Raise(nameof(PartialTooltip));
        Raise(nameof(PendingDiffers));
        CommandManager.InvalidateRequerySuggested();
    }

    public Task EnsureLoadedAsync() => _state is null ? RefreshAsync() : Task.CompletedTask;

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            var (state, problem, backup, pending) = await Task.Run(() => (
                Engine.Query(Tweak),
                Tweak.Scripts.Values.Select(s => s.Problem()).FirstOrDefault(p => p is not null),
                Engine.Backups.Get(Tweak.Id),
                Engine.IsRebootPending(Tweak.Id)));
            _scriptProblem = problem;
            _backup = backup;
            RebootPending = pending;
            State = state;
        }
        catch (Exception ex)
        {
            // Never leaves a stale state on screen when the re-read fails.
            Log.Error($"Re-reading {Tweak.Id} failed", ex);
            State = TweakState.ReadError(Txt.ReadErrorWith(ex.Message));
        }
        OnStateChanged();
    }

    /// <summary>
    /// Applies directly, without asking. An option that lowers a protection (or deletes something irreversibly) warns in a notification
    /// afterwards; <paramref name="warn"/> false leaves the warning to whoever applies several at once.
    /// </summary>
    internal async Task<bool> ApplyAsync(string key, bool warn = true)
    {
        if (!CanChange) return false;
        ShowRestartExplorer = false;
        var ok = await RunAsync(async (ct, started) =>
        {
            var r = await Engine.ApplyAsync(Tweak, key, ct, started);
            return (r.Success, r.Message);
        }, Txt.Applying);
        await RefreshAsync();
        PendingOption = Options.FirstOrDefault(o => o.IsSelected); // the list returns to the real state if nothing was applied
        ShowRestartExplorer = ok && Tweak.RestartExplorer;
        if (ok && warn) Warn(key);
        return ok;
    }

    /// <summary>The applied option lowers a protection or deletes something irreversibly.</summary>
    internal bool NeedsWarning(string? option) => option is not null && (option == Tweak.LessSecureOption || option == Tweak.ConfirmationOption);

    void Warn(string? option)
    {
        if (WarningFor(Tweak, option) is { } note) Toasts.Show($"{Name}: {note}", ToastKind.Warning);
    }

    /// <summary>The warning to show after applying <paramref name="option"/>; null when it needs none.</summary>
    internal static string? WarningFor(Tweak tweak, string? option)
    {
        if (option is null) return null;
        if (option == tweak.ConfirmationOption) return tweak.ConfirmationNote;
        if (option != tweak.LessSecureOption) return null;
        string? detail = null;
        try { detail = tweak.SecurityDetail?.Invoke(); }
        catch (Exception ex) { Log.Warn($"{tweak.Id}: checking what the warning should add failed: {ex.Message}"); }
        return $"{tweak.SecurityNote} {detail}".TrimEnd();
    }

    async Task RestoreAsync(RestoreTarget target)
    {
        var snap = target == RestoreTarget.Previous ? _backup?.Previous : _backup?.Original;
        if (snap is null) return;
        var ok = await RunAsync(async (ct, started) =>
        {
            var r = await Engine.RestoreAsync(Tweak, target, ct, started);
            return (r.Success, r.Message);
        }, Txt.Restoring);
        await RefreshAsync();
        if (ok) Warn(snap.Option);
    }

    async Task ShowDetailsAsync()
    {
        if (!IsBusy) await RefreshAsync(); // the details explain the current read, not an old one
        var note = string.Join("\n", new[] { _state?.Detail, _scriptProblem }.Where(s => s is not null));
        var facts = new List<DetailFact>
        {
            new(Txt.DetailState, CurrentLabel, note.Length > 0 ? note : null),
            new(Txt.DetailAdmin, Tweak.NeedsAdmin ? Txt.DetailRequired : Txt.DetailNotRequired),
            new(Txt.DetailRestart, RequiresReboot ? Txt.DetailRequired : Txt.DetailNotRequired),
            new(Txt.DetailSource, Tweak.Source),
        };
        if (Tweak.Scripts.Count > 0) facts.Add(new(Txt.DetailScript, string.Join("\n", Tweak.Scripts.Values.Select(s => s.RelativePath).Distinct())));
        if (Engine.Backups.Get(Tweak.Id) is { } backup && (backup.Original ?? backup.Previous) is not null)
            facts.Add(new(Txt.DetailSaved, Txt.DetailSavedStates(backup.Original?.TakenAt, backup.Previous?.TakenAt)));
        // Highlights the current option or, in a partial state, the closest one: its ✗ marks are what is missing.
        var focus = _state is { Status: TweakStatus.Matched } s ? s.Option : _state?.ClosestOption;
        DetailsDialog.Show(Name, Description, facts, Tweak.Options.Select(o => (o.Key, o.Label)).ToList(), focus,
            _state?.Readings ?? Array.Empty<SettingReading>());
    }
}

public sealed class ActionViewModel : OperationViewModel, ICatalogItem
{
    readonly ActionItem _action;
    string? _problem;

    public ActionViewModel(ActionItem action)
    {
        _action = action;
        RunCommand = new AsyncCommand(RunActionAsync, () => !IsBusy && _problem is null);
    }

    public string Name => _action.Name;
    public string Description => _action.Description;
    public string ButtonText => _action.ButtonText;
    public string Page => _action.Page;
    public string? Tab => _action.Tab;
    public string? Group => _action.Group;
    public string SearchText => _action.SearchText;
    public bool RequiresReboot => _action.RequiresReboot;
    public bool ShowAdminBadge => _action.NeedsAdmin && !Elevation.IsAdmin;
    public string? Problem => _problem;
    public bool HasProblem => _problem is not null;
    public ICommand RunCommand { get; }

    public Task EnsureLoadedAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        _problem = _action.Script is { } s ? await Task.Run(s.Problem) : null;
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        CommandManager.InvalidateRequerySuggested();
    }

    async Task RunActionAsync()
    {
        await RunAsync(async (ct, started) => (true, await TweakEngine.Instance.RunActionAsync(_action, ct, started)), Txt.RunningAction);
    }
}
