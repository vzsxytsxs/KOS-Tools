using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Updates;

namespace KosToolbox.ViewModels;

public sealed class WuUpdateRowViewModel(WuUpdate update) : ObservableObject
{
    bool _checked;
    public WuUpdate Update { get; } = update;
    public string Title => Update.Title;
    public string Detail => string.Join(" · ", new[] { Update.Kb, Update.Category, Update.Size > 0 ? Txt.Size(Update.Size) : null, Update.Hidden ? "Hidden" : null }
        .Where(s => !string.IsNullOrEmpty(s)));
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) CommandManager.InvalidateRequerySuggested(); } }
}

/// <summary>
/// Windows Update: the updates offered to this PC, installed or hidden one by one while Windows Update itself stays off. Nothing runs
/// on its own: each button lets Windows Update run for that operation only (see <see cref="WindowsUpdates"/>).
/// </summary>
public sealed class WindowsUpdatePageViewModel : PageViewModel
{
    IReadOnlyList<WuUpdateRowViewModel> _rows = Array.Empty<WuUpdateRowViewModel>();
    string? _status;
    bool _busy, _isError, _searched, _installed;

    public WindowsUpdatePageViewModel() : base("windows-update", "Windows Update", "",
        "Check, install and hide Windows updates one by one while Windows Update stays off.")
    {
        SearchCommand = new AsyncCommand(SearchAsync, () => !_busy && Elevation.IsAdmin);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_busy && Checked.Any(r => !r.Update.Hidden));
        HideCommand = new AsyncCommand(() => SetHiddenAsync(true), () => !_busy && Checked.Any(r => !r.Update.Hidden && !r.Update.Mandatory));
        ShowCommand = new AsyncCommand(() => SetHiddenAsync(false), () => !_busy && Checked.Any(r => r.Update.Hidden));
        OpenKosStateCommand = new RelayCommand(() => KosStatePageViewModel.Open?.Invoke());
    }

    public ICommand SearchCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand OpenKosStateCommand { get; }
    public ICommand RestartAsAdminCommand { get; } = new RelayCommand(SettingsViewModel.RestartAsAdmin);
    public bool NeedsAdmin => !Elevation.IsAdmin;
    public IReadOnlyList<WuUpdateRowViewModel> Rows { get => _rows; private set { Set(ref _rows, value); Raise(nameof(HasRows)); } }
    public bool HasRows => _rows.Count > 0;
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    /// <summary>Updates were installed in this run: they can put back what the KOS changed, so the page points to the KOS state check.</summary>
    public bool SuggestKosState { get => _installed; private set => Set(ref _installed, value); }
    IEnumerable<WuUpdateRowViewModel> Checked => _rows.Where(r => r.IsChecked);

    /// <summary>The search only runs on the button: it lets Windows Update run and goes online.</summary>
    public override Task RefreshAsync() => Task.CompletedTask;

    void Report(string text, bool error = false) { Status = text; IsError = error; }

    async Task SearchAsync()
    {
        IsBusy = true;
        Report("Searching Windows Update… This can take a few minutes the first time.");
        try
        {
            var found = await WindowsUpdates.SearchAsync();
            Rows = found.Select(u => new WuUpdateRowViewModel(u)).ToList();
            _searched = true;
            var hidden = found.Count(u => u.Hidden);
            Report(found.Count == 0 ? "No updates are offered for this PC."
                : $"{found.Count - hidden} update(s) offered{(hidden > 0 ? $", {hidden} hidden" : "")}. Check the ones to install or hide.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows Update: search failed: {ex.Message}");
            Report(ex.Message, error: true);
        }
        finally { IsBusy = false; }
    }

    async Task InstallAsync()
    {
        var ids = Checked.Where(r => !r.Update.Hidden).Select(r => r.Update.Id).ToList();
        IsBusy = true;
        string? done = null;
        try
        {
            var result = await WindowsUpdates.InstallAsync(ids, new Progress<string>(s => Report(s)));
            var failed = result.Updates.Where(u => u.Error is not null).ToList();
            SuggestKosState |= result.Installed > 0;
            var text = $"{result.Installed} of {result.Updates.Count} installed." +
                       (failed.Count > 0 ? " Not installed: " + string.Join("; ", failed.Select(f => $"{f.Title}: {f.Error}")) : "") +
                       (result.RebootRequired ? " Restart Windows to finish." : "");
            Report(text, error: failed.Count > 0);
            Toasts.Show(text, failed.Count > 0 ? ToastKind.Warning : ToastKind.Success);
            done = text;
        }
        catch (Exception ex)
        {
            Log.Error("Windows Update: install failed", ex);
            Report(ex.Message, error: true);
        }
        finally { IsBusy = false; }
        if (done is not null) await SearchAgainAsync(done);
    }

    /// <summary>Lists the updates again after a change, keeping what the change did in front of the new count.</summary>
    async Task SearchAgainAsync(string done)
    {
        if (!_searched) return;
        var error = IsError;
        await SearchAsync();
        if (!IsError) Report($"{done} {Status}", error);
    }

    async Task SetHiddenAsync(bool hidden)
    {
        var ids = Checked.Where(r => r.Update.Hidden != hidden).Select(r => r.Update.Id).ToList();
        IsBusy = true;
        string? done = null;
        try
        {
            var changed = await WindowsUpdates.SetHiddenAsync(ids, hidden);
            done = hidden ? $"{changed} update(s) hidden: Windows Update won't offer them." : $"{changed} update(s) offered again.";
        }
        catch (Exception ex)
        {
            Log.Error("Windows Update: hiding failed", ex);
            Report(ex.Message, error: true);
        }
        finally { IsBusy = false; }
        if (done is not null) await SearchAgainAsync(done);
    }
}
