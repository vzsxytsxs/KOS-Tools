using System.Windows.Input;
using KosToolbox.Core.Peripherals.Corsair;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// The Corsair settings the shared rows don't have: the SABRE's lift-off distance, motion sync, angle snapping, ripple control and
/// five buttons; the K65's Win Lock. Each is written, read back and reported by its driver.
/// </summary>
public sealed partial class PeripheralRowViewModel
{
    ICommand? _motionSyncCommand, _sabreAngleSnapCommand, _sabreRippleCommand, _liftOffCommand, _winLockCommand;

    SabreDriver? Sabre => Driver as SabreDriver;
    SabreExtras? SabreNow => Sabre?.Extras;
    BragiDriver? Bragi => Driver as BragiDriver;

    public bool HasSabre => SabreNow is not null;
    public bool HasWinLock => Bragi?.WinLockOn is not null;
    public bool HasCorsair => HasSabre || HasWinLock;
    public bool HasMotionSync => SabreNow?.MotionSync is not null;
    public bool HasAngleSnap => SabreNow?.AngleSnap is not null;
    public bool HasRipple => SabreNow?.Ripple is not null;
    public bool MotionSyncOn => SabreNow?.MotionSync == true;
    public bool AngleSnapOn => SabreNow?.AngleSnap == true;
    public bool RippleOn => SabreNow?.Ripple == true;
    public bool WinLockOn => Bragi?.WinLockOn == true;
    public bool HasLiftOff => SabreNow?.LiftOff is not null;
    public bool CanLiftOff => SabreNow is { LiftOff: not null, LiftOffLocked: false };
    public bool LiftOffLocked => HasLiftOff && !CanLiftOff;
    public IReadOnlyList<OptionItem> LiftOffs => SabreNow?.LiftOff is { } now
        ? new[] { (SabreLift.Low, Txt.CorsairLiftLow), (SabreLift.Medium, Txt.CorsairLiftMedium), (SabreLift.High, Txt.CorsairLiftHigh) }
            .Select(o => new OptionItem(o.Item1.ToString(), o.Item2) { IsSelected = o.Item1 == now }).ToList()
        : Array.Empty<OptionItem>();
    public IReadOnlyList<SabreButtonViewModel> SabreButtons { get; private set; } = Array.Empty<SabreButtonViewModel>();
    public bool HasSabreButtons => SabreButtons.Count > 0;

    public ICommand MotionSyncCommand => _motionSyncCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.CorsairMotionSync, d => ((SabreDriver)d).SetMotionSync(on)) : Task.CompletedTask);
    public ICommand SabreAngleSnapCommand => _sabreAngleSnapCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.CorsairAngleSnap, d => ((SabreDriver)d).SetAngleSnap(on)) : Task.CompletedTask);
    public ICommand SabreRippleCommand => _sabreRippleCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.CorsairRipple, d => ((SabreDriver)d).SetRipple(on)) : Task.CompletedTask);
    public ICommand WinLockCommand => _winLockCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.CorsairWinLock, d => ((BragiDriver)d).SetWinLock(on)) : Task.CompletedTask);
    public ICommand LiftOffCommand => _liftOffCommand ??= new AsyncCommand(p =>
        p is string k && Enum.TryParse<SabreLift>(k, out var lift) && lift != SabreNow?.LiftOff
            ? Write(Txt.CorsairLiftOff, d => ((SabreDriver)d).SetLiftOff(lift)) : Task.CompletedTask);

    /// <summary>Builds the button rows from what the driver last read (called by <see cref="Show"/>).</summary>
    void LoadCorsair()
    {
        var labels = new[] { Txt.CorsairButton1, Txt.CorsairButton2, Txt.CorsairButton3, Txt.CorsairButton4, Txt.CorsairButton5 };
        SabreButtons = SabreNow?.Buttons is { } b
            ? b.Select((a, i) => new SabreButtonViewModel(i + 1, labels[i], a, ApplyCorsairButton)).ToList()
            : Array.Empty<SabreButtonViewModel>();
    }

    Task ApplyCorsairButton(SabreButtonViewModel b)
    {
        if (b.Function?.Action is not { } action) return Invalid(Txt.CorsairButtonInvalid);
        var key = b.Key;
        return Write(b.Label, d => ((SabreDriver)d).SetButton(key, action));
    }
}

/// <summary>One of the SABRE's five buttons: a function from the list; Apply writes it to the mouse.</summary>
public sealed class SabreButtonViewModel : ObservableObject
{
    SabreFunction? _function;

    public SabreButtonViewModel(int key, string label, SabreAction? now, Func<SabreButtonViewModel, Task> apply)
    {
        Key = key;
        Label = label;
        _function = SabreButtons.Choices.First(f => f.Action == now);
        ApplyCommand = new AsyncCommand(() => apply(this));
    }

    public int Key { get; }
    public string Label { get; }
    public IReadOnlyList<SabreFunction> Functions => SabreButtons.Choices;
    public SabreFunction? Function { get => _function; set => Set(ref _function, value); }
    public ICommand ApplyCommand { get; }
    public override string ToString() => Label;
}

public sealed record SabreFunction(SabreAction? Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The functions the toolbox writes to a SABRE button: the mouse buttons, DPI changes and off. A button with anything else shows "Other".</summary>
public static class SabreButtons
{
    public static readonly IReadOnlyList<SabreFunction> Choices = new SabreFunction[]
    {
        new(SabreAction.Left, Txt.CorsairFnLeft), new(SabreAction.Right, Txt.CorsairFnRight), new(SabreAction.Middle, Txt.CorsairFnMiddle),
        new(SabreAction.Back, Txt.CorsairFnBack), new(SabreAction.Forward, Txt.CorsairFnForward), new(SabreAction.DpiCycle, Txt.CorsairFnDpiCycle),
        new(SabreAction.DpiUp, Txt.CorsairFnDpiUp), new(SabreAction.DpiDown, Txt.CorsairFnDpiDown), new(SabreAction.Disabled, Txt.CorsairFnOff),
        new(null, Txt.CorsairFnCustom),
    };
}
