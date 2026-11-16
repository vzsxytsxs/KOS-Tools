using System.Windows.Data;
using System.Windows.Input;
using KosToolbox.Core.Apps;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.ViewModels;

/// <summary>A leftover found, with its checkbox (checked by default, as in Geek and HiBit).</summary>
public sealed class LeftoverRowViewModel : ObservableObject
{
    readonly Action _changed;
    bool _checked;

    public LeftoverRowViewModel(Leftover item, bool isChecked, Action changed)
    {
        Item = item;
        _changed = changed;
        Problem = Leftovers.CannotRemove(item);
        _checked = isChecked && Problem is null;
    }

    public Leftover Item { get; }
    /// <summary>Why it cannot be deleted now (e.g. needs administrator); null if it can.</summary>
    public string? Problem { get; }
    public bool CanRemove => Problem is null;
    public string Group => Item.Kind switch
    {
        LeftoverKind.Folder => Txt.GroupFiles, LeftoverKind.File => Txt.GroupShortcuts,
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => Txt.GroupRegistry, _ => Txt.GroupServicesTasks,
    };
    public string Kind => Item.Kind switch
    {
        LeftoverKind.Folder => Txt.KindFolder, LeftoverKind.File => Txt.KindFile, LeftoverKind.RegistryKey => Txt.KindRegistryKey,
        LeftoverKind.RegistryValue => Txt.KindRegistryValue, LeftoverKind.Service => Txt.KindService, _ => Txt.KindTask,
    };
    public string Display => Item.ValueName is null ? Item.Path : $"{Item.Path}  ›  {Item.ValueName}";
    public string Reason => Item.Reason;
    public string Size => Item.Size is { } s ? Txt.Size(s) : "";
    public string Tooltip => Problem ?? Display;

    public bool IsChecked
    {
        get => _checked;
        set { if (CanRemove && Set(ref _checked, value)) _changed(); }
    }
}

/// <summary>
/// List of a program's leftovers (after the uninstaller, or in Force removal) to review. Nothing is removed without
/// "Delete checked"; then the search runs again and only what remains stays in the list.
/// </summary>
public sealed class LeftoversViewModel : ObservableObject
{
    readonly IReadOnlyList<ProgramTrace> _traces;
    readonly bool _force;
    readonly Action _closed;
    List<LeftoverRowViewModel> _rows = new();
    ListCollectionView _view = new(new List<LeftoverRowViewModel>());
    bool _busy;
    string? _status;

    public LeftoversViewModel(ProgramTrace trace, bool force, IReadOnlyList<Leftover> items, Action closed) : this(new[] { trace }, force, items, closed) { }

    /// <summary>Leftovers of several programs at once (bulk uninstall): one list, one delete.</summary>
    public LeftoversViewModel(IReadOnlyList<ProgramTrace> traces, bool force, IReadOnlyList<Leftover> items, Action closed)
    {
        _traces = traces;
        _force = force;
        _closed = closed;
        SetItems(items, new HashSet<string>());
        DeleteCommand = new AsyncCommand(DeleteAsync, () => !_busy && _rows.Any(r => r.IsChecked));
        CloseCommand = new RelayCommand(_closed, () => !_busy);
        CheckAllCommand = new RelayCommand(() => Check(true), () => !_busy && _rows.Any(r => r.CanRemove && !r.IsChecked));
        UncheckAllCommand = new RelayCommand(() => Check(false), () => !_busy && _rows.Any(r => r.IsChecked));
    }

    string Name => _traces.Count == 1 ? _traces[0].Name : $"{_traces.Count} programs";
    public string Title => _force ? Txt.ForceRemovalTitle(Name) : Txt.LeftoversTitle(Name);

    /// <summary>Every trace's leftovers, each item once (programs of one vendor share folders and keys).</summary>
    public static List<Leftover> Scan(IReadOnlyList<ProgramTrace> traces, bool force) =>
        traces.SelectMany(t => Leftovers.Scan(t, force)).GroupBy(i => i.Id).Select(g => g.First()).ToList();
    public string Note => _force ? Txt.ForceRemovalNote : Txt.LeftoversNote;
    public ListCollectionView View { get => _view; private set => Set(ref _view, value); }
    public bool NeedsAdmin => _rows.Any(r => r.Problem == Txt.LeftoverNeedsAdmin);
    public string Stats => Txt.LeftoversStats(_rows.Count, _rows.Count(r => r.IsChecked),
        _rows.Where(r => r.IsChecked).Sum(r => r.Item.Size ?? 0));
    public string? Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status is not null;
    public ICommand DeleteCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand CheckAllCommand { get; }
    public ICommand UncheckAllCommand { get; }

    void SetItems(IReadOnlyList<Leftover> items, IReadOnlySet<string> uncheckedIds)
    {
        _rows = items.Select(i => new LeftoverRowViewModel(i, !uncheckedIds.Contains(i.Id), Changed)).ToList();
        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverRowViewModel.Group)));
        View = view;
        Raise(nameof(NeedsAdmin));
        Changed();
    }

    void Changed()
    {
        Raise(nameof(Stats));
        CommandManager.InvalidateRequerySuggested();
    }

    void Check(bool value)
    {
        foreach (var r in _rows) r.IsChecked = value;
    }

    async Task DeleteAsync()
    {
        var items = _rows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
        if (items.Count == 0) return;
        var uncheckedIds = _rows.Where(r => !r.IsChecked).Select(r => r.Item.Id).ToHashSet();
        var tried = items.Select(i => i.Id).ToHashSet();
        _busy = true;
        Status = Txt.DeletingLeftovers;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            // In the system-change queue (closing the window midway warns); then re-reads what remains.
            var (result, remaining) = await TweakEngine.Instance.RunExclusiveAsync(() => Task.Run(() =>
                (Leftovers.Remove(Name, items), Scan(_traces, _force))));
            Toasts.Show(Txt.LeftoversRemoved(result.Removed, result.Kept, result.Failed.Count, result.BackupFolder),
                result.Failed.Count == 0 ? ToastKind.Success : ToastKind.Warning);
            if (result.Failed.Count > 0)
                Toasts.Show(Txt.LeftoversFailed(result.Failed.Take(4).Select(f => $"{System.IO.Path.GetFileName(f.Item.Path.TrimEnd('\\'))}: {f.Error}")), ToastKind.Error);
            // What the user unchecked and the vendor folder/key that did not end up empty do not return to the list.
            var shown = remaining.Where(i => !uncheckedIds.Contains(i.Id) && !(i.OnlyIfEmpty && tried.Contains(i.Id))).ToList();
            if (shown.Count == 0) { _closed(); return; }
            SetItems(shown, uncheckedIds);
        }
        catch (Exception ex)
        {
            Log.Error($"Leftovers of {Name}: delete failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            _busy = false;
            Status = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
