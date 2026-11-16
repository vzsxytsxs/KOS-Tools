using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tweaks;

namespace KosToolbox.ViewModels;

/// <summary>An option of a segmented control or a list.</summary>
public sealed class OptionItem(string key, string label) : ObservableObject
{
    bool _isSelected;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    // Lists without an item template show ToString(); the class name must never appear.
    public override string ToString() => Label;
}

/// <summary>Base of the items that run operations: queue, progress, result and error.</summary>
public abstract class OperationViewModel : ObservableObject
{
    bool _isBusy, _isQueued, _isError;
    string? _message;
    CancellationTokenSource? _cts;

    protected OperationViewModel() => CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsQueued);

    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) OnStateChanged(); } }
    /// <summary>Waiting for another operation to finish; only in this phase can it be cancelled without leaving anything half-done.</summary>
    public bool IsQueued { get => _isQueued; private set => Set(ref _isQueued, value); }
    public string? Message { get => _message; protected set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool IsError { get => _isError; protected set => Set(ref _isError, value); }
    public ICommand CancelCommand { get; }

    protected virtual void OnStateChanged() { }

    /// <summary>
    /// Runs <paramref name="op"/> off the UI thread. <paramref name="queued"/> indicates that the operation
    /// goes through the <see cref="TweakEngine"/> queue (one system change at a time).
    /// </summary>
    protected async Task<bool> RunAsync(Func<CancellationToken, Action, Task<(bool Ok, string Message)>> op, string busyText, bool queued = true)
    {
        if (IsBusy) return false;
        using var cts = _cts = new CancellationTokenSource();
        var ui = Dispatcher.CurrentDispatcher;
        IsError = false;
        IsQueued = queued && TweakEngine.Instance.IsBusy;
        Message = IsQueued ? Txt.WaitingForOther : busyText;
        IsBusy = true;
        void Started() => ui.BeginInvoke(() => { IsQueued = false; Message = busyText; });
        try
        {
            var (ok, msg) = await Task.Run(() => op(queued ? cts.Token : CancellationToken.None, Started));
            Message = msg;
            IsError = !ok;
            return ok;
        }
        catch (OperationCanceledException)
        {
            Message = Txt.CancelledBeforeStart;
            return false;
        }
        catch (Exception ex)
        {
            if (ex is not ElevationCancelledException) Log.Error("Operation failed", ex);
            Message = ex.Message;
            IsError = true;
            return false;
        }
        finally
        {
            _cts = null;
            IsQueued = false;
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
