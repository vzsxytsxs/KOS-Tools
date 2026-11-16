using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Display;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// Display › Selected display › Monitor controls (DDC/CI): brightness, contrast, input source and factory reset, only for codes
/// the monitor lists. Read after the display loads (the monitor can take a couple of seconds); slider moves are written once
/// the slider rests, then read back.
/// </summary>
public sealed partial class DisplayPageViewModel
{
    DdcState? _ddc;
    string? _ddcStatus;
    bool _ddcLoading;
    readonly Dictionary<byte, uint> _ddcPending = new();
    DispatcherTimer? _ddcTimer;
    ICommand? _ddcRestoreCommand;

    public string? DdcStatus { get => _ddcStatus; private set => Set(ref _ddcStatus, value); }
    public bool HasDdc => _ddc is not null;
    public string DdcCapabilities => _ddc?.Capabilities ?? "";
    public bool HasBrightness => _ddc?.Values.ContainsKey(Ddc.Brightness) == true;
    public bool HasContrast => _ddc?.Values.ContainsKey(Ddc.Contrast) == true;
    public double BrightnessMax => _ddc?.Values.GetValueOrDefault(Ddc.Brightness).Max ?? 100;
    public double ContrastMax => _ddc?.Values.GetValueOrDefault(Ddc.Contrast).Max ?? 100;
    public double DdcBrightness { get => Value(Ddc.Brightness); set => Queue(Ddc.Brightness, value); }
    public double DdcContrast { get => Value(Ddc.Contrast); set => Queue(Ddc.Contrast, value); }
    public bool HasInputs => DdcInputs.Count > 0;
    /// <summary>Only the inputs the monitor lists for code 60h.</summary>
    public IReadOnlyList<Choice> DdcInputs => _ddc?.Codes.GetValueOrDefault(Ddc.Input) is { Count: > 0 } inputs
        ? inputs.Select(v => new Choice((int)v, Ddc.InputName(v))).ToList() : Array.Empty<Choice>();
    public int DdcInput
    {
        get => _ddc?.Values.TryGetValue(Ddc.Input, out var v) == true ? (int)(v.Current & 0xFF) : 0;
        set { if (!_loadingWin && _ddc is not null && value > 0 && value != DdcInput) MainViewModel.Fire(WriteDdcAsync(Ddc.Input, (uint)value)); }
    }
    public bool CanRestoreMonitor => _ddc?.Codes.ContainsKey(Ddc.Restore) == true;
    public ICommand DdcRestoreCommand => _ddcRestoreCommand ??= new AsyncCommand(() => WriteDdcAsync(Ddc.Restore, 1), () => CanRestoreMonitor && !_ddcLoading);

    double Value(byte code) => _ddcPending.TryGetValue(code, out var p) ? p : _ddc?.Values.TryGetValue(code, out var v) == true ? v.Current : 0;

    async Task LoadDdcAsync(WinDisplay w)
    {
        _ddcLoading = true;
        _ddc = null;
        DdcStatus = Txt.DdcReading;
        RaiseDdc();
        DdcState? state = null;
        try { state = await Task.Run(() => Ddc.Read(w.GdiName)); }
        catch (Exception ex) { Log.Warn($"DDC/CI on {w.GdiName}: {ex.Message}"); }
        _ddcLoading = false;
        if (_win != w) return;
        _ddc = state;
        DdcStatus = state is null ? Txt.DdcNotAvailable : state.Values.Count == 0 && !CanRestoreMonitor ? Txt.DdcNothingListed : null;
        RaiseDdc();
    }

    void Queue(byte code, double value)
    {
        if (_loadingWin || _ddc is null) return;
        _ddcPending[code] = (uint)Math.Round(value);
        _ddcTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => FlushDdc(), Dispatcher.CurrentDispatcher);
        _ddcTimer.Stop();
        _ddcTimer.Start();
    }

    void FlushDdc()
    {
        _ddcTimer!.Stop();
        foreach (var (code, value) in _ddcPending.ToList()) MainViewModel.Fire(WriteDdcAsync(code, value));
    }

    async Task WriteDdcAsync(byte code, uint value)
    {
        if (_win is not { } w || _ddc is not { } state) return;
        uint? back;
        try { back = await Task.Run(() => Ddc.Write(w.GdiName, code, value)); }
        catch (Exception ex)
        {
            Log.Warn($"DDC/CI write {code:X2}h = {value} on {w.GdiName} failed: {ex.Message}");
            Toasts.Show(Txt.DdcWriteFailed(ex.Message), ToastKind.Error);
            back = null;
        }
        _ddcPending.Remove(code);
        if (_win != w || _ddc != state) return;
        if (code == Ddc.Restore)
        {
            Toasts.Show(Txt.DdcRestored, ToastKind.Success);
            await LoadDdcAsync(w); // every value changed
            return;
        }
        var values = state.Values.ToDictionary(p => p.Key, p => p.Value);
        if (back is { } current && values.TryGetValue(code, out var old)) values[code] = (current, old.Max);
        _ddc = state with { Values = values };
        if (back is null) Toasts.Show(Txt.DdcNotConfirmed, ToastKind.Warning);
        else if ((back & 0xFF) != (value & 0xFF) && code == Ddc.Input) Toasts.Show(Txt.DdcInputNotConfirmed, ToastKind.Warning);
        RaiseDdc();
    }

    void RaiseDdc()
    {
        _loadingWin = true;
        try
        {
            foreach (var name in new[] { nameof(HasDdc), nameof(DdcCapabilities), nameof(HasBrightness), nameof(HasContrast), nameof(BrightnessMax), nameof(ContrastMax),
                nameof(DdcBrightness), nameof(DdcContrast), nameof(HasInputs), nameof(DdcInputs), nameof(DdcInput), nameof(CanRestoreMonitor) })
                Raise(name);
        }
        finally { _loadingWin = false; }
        CommandManager.InvalidateRequerySuggested();
    }
}
