using System.Windows.Input;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.AttackShark;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// The Attack Shark X11's own settings (no other driver has them): ripple control, angle snap, each DPI stage's value and colour,
/// deep sleep, key response, the five buttons and a macro on a button. Each is written, acknowledged and read back by the driver.
/// </summary>
public sealed partial class PeripheralRowViewModel
{
    string? _deepSleepText, _debounceText, _sharkMacroSteps, _sharkMacroLoops;
    int _sharkMacroMode;
    ICommand? _rippleCommand, _angleSnapCommand, _deepSleepCommand, _debounceCommand, _sharkMacroCommand;

    AttackSharkDriver? Shark => Driver as AttackSharkDriver;
    AttackSharkExtras? SharkExtras => Shark?.Extras;
    public bool HasShark => SharkExtras is not null;
    public bool SharkRipple => SharkExtras?.Ripple == true;
    public bool SharkAngleSnap => SharkExtras?.AngleSnap == true;
    public string? DeepSleepText { get => _deepSleepText; set => Set(ref _deepSleepText, value); }
    public string? DebounceText { get => _debounceText; set => Set(ref _debounceText, value); }
    public IReadOnlyList<SharkStageViewModel> SharkStages { get; private set; } = Array.Empty<SharkStageViewModel>();
    public IReadOnlyList<SharkButtonViewModel> SharkButtons { get; private set; } = Array.Empty<SharkButtonViewModel>();
    public IReadOnlyList<SharkButtonViewModel> SharkMacroButtons => SharkButtons.Where(b => b.Slot != 0).ToList();
    public SharkButtonViewModel? SharkMacroButton { get; set; }
    public string? SharkMacroSteps { get => _sharkMacroSteps; set => Set(ref _sharkMacroSteps, value); }
    public string? SharkMacroLoops { get => _sharkMacroLoops; set => Set(ref _sharkMacroLoops, value); }
    public IReadOnlyList<string> SharkMacroModes { get; } = new[] { Txt.SharkMacroTimes, Txt.SharkMacroUntilPressed, Txt.SharkMacroWhileHeld };
    public int SharkMacroMode { get => _sharkMacroMode; set => Set(ref _sharkMacroMode, value); }

    public ICommand RippleCommand => _rippleCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.SharkRipple, d => ((AttackSharkDriver)d).SetRipple(on)) : Task.CompletedTask);
    public ICommand AngleSnapCommand => _angleSnapCommand ??= new AsyncCommand(p => p is bool on ? Write(Txt.SharkAngleSnap, d => ((AttackSharkDriver)d).SetAngleSnap(on)) : Task.CompletedTask);
    public ICommand DeepSleepCommand => _deepSleepCommand ??= new AsyncCommand(() =>
        Int(_deepSleepText) is { } m ? Write(Txt.SharkDeepSleep, d => ((AttackSharkDriver)d).SetDeepSleep(m)) : Invalid());
    public ICommand DebounceCommand => _debounceCommand ??= new AsyncCommand(() =>
        Int(_debounceText) is { } ms ? Write(Txt.SharkDebounce, d => ((AttackSharkDriver)d).SetDebounce(ms)) : Invalid());
    public ICommand SharkMacroCommand => _sharkMacroCommand ??= new AsyncCommand(SaveSharkMacro);

    /// <summary>Fills the X11 fields from what the driver last read (called by <see cref="Show"/>).</summary>
    void LoadShark()
    {
        if (SharkExtras is not { } x) return;
        DeepSleepText = x.DeepSleepMinutes.ToString();
        DebounceText = x.DebounceMs.ToString();
        var stages = _state?.Dpi?.Stages ?? Array.Empty<DpiStage>();
        SharkStages = stages.Select((s, i) => new SharkStageViewModel(i + 1, s.X, x.StageColors[i], ApplyStage)).ToList();
        if (x.Buttons is { } b)
        {
            var keep = SharkMacroButton?.Slot;
            SharkButtons = AttackSharkButtons.Physical.Select(p => new SharkButtonViewModel(p.Slot, p.Label, b[p.Slot], ApplyButton)).ToList();
            SharkMacroButton = SharkMacroButtons.FirstOrDefault(m => m.Slot == keep) ?? SharkMacroButtons.FirstOrDefault(m => m.Slot == 6);
        }
        SharkMacroLoops ??= "1";
    }

    Task ApplyStage(SharkStageViewModel s)
    {
        if (Int(s.DpiText) is not { } dpi) return Invalid();
        if (HexColor(s.ColorText) is not { } color) return Invalid(Txt.SharkColorInvalid);
        var n = s.Stage;
        return Write(Txt.SharkStage(n), d =>
        {
            var shark = (AttackSharkDriver)d;
            var state = shark.SetStageDpi(n, dpi);
            return shark.Extras?.StageColors[n - 1] == color ? state : shark.SetStageColor(n, color);
        });
    }

    Task ApplyButton(SharkButtonViewModel b)
    {
        if (b.Action() is not { } action) return Invalid(Txt.SharkButtonInvalid);
        var slot = b.Slot;
        return Write(Txt.SharkButton(b.Label), d => ((AttackSharkDriver)d).SetButton(slot, action));
    }

    /// <summary>Stores the macro in the chosen button's slot (+1, as the vendor app does) and binds that button to it.</summary>
    Task SaveSharkMacro()
    {
        if (SharkMacroButton is not { } button) return Invalid(Txt.SharkButtonInvalid);
        var (steps, bad) = ParseSteps(_sharkMacroSteps);
        if (steps is null) return Invalid(Txt.PeripheralMacroInvalid(bad));
        if (steps.Length == 0 || steps.Length > 50) return Invalid(Txt.SharkMacroLength);
        if (steps.Any(s => s.DelayMs is < 0 or > 1270)) return Invalid(Txt.SharkMacroDelay);
        if (Int(_sharkMacroLoops) is not ({ } loops and >= 1 and <= 255)) return Invalid(Txt.SharkMacroLoopsInvalid);
        // Mouse buttons in a macro are F1..F5 on this mouse (HarukaYamamoto0's MacroActionMouseCode).
        var macro = steps.Select(s => new MacroStep(s.MouseButton ? (byte)(0xF1 + s.Code) : s.Code, s.Pressed, s.DelayMs)).ToList();
        var slot = button.Slot;
        var mode = _sharkMacroMode;
        return Write(Txt.SharkMacro(button.Label), d =>
        {
            var shark = (AttackSharkDriver)d;
            var acks = shark.WriteMacro(slot + 1, macro, mode, loops);
            if (acks[^1] != true) throw new PeripheralException(Txt.SharkMacroNotAcknowledged);
            return shark.SetButton(slot, ButtonAction.RunMacro(slot + 1));
        }, warning: Txt.SharkMacroNote);
    }

    // ---------- gamemode: angle snap, ripple, key response (the light and the DPI use the shared rows) ----------
    string _gmAngle = "", _gmRipple = ""; // "" keep, "on", "off"
    string? _gmDebounceText;
    ICommand? _gmAngleCommand, _gmRippleCommand;
    public bool CanGmShark => Driver is AttackSharkDriver && HasShark;
    public IReadOnlyList<OptionItem> GmAngles => Choices(_gmAngle);
    public IReadOnlyList<OptionItem> GmRipples => Choices(_gmRipple);
    public ICommand GmAngleCommand => _gmAngleCommand ??= new RelayCommand(p => { if (p is string k) { _gmAngle = k; Raise(nameof(GmAngles)); } });
    public ICommand GmRippleCommand => _gmRippleCommand ??= new RelayCommand(p => { if (p is string k) { _gmRipple = k; Raise(nameof(GmRipples)); } });
    /// <summary>Key response in gamemode, in ms; empty keeps it.</summary>
    public string? GmDebounceText { get => _gmDebounceText; set => Set(ref _gmDebounceText, value); }

    static IReadOnlyList<OptionItem> Choices(string now) => new[] { ("", Txt.PeripheralGamemodeKeep), ("on", Txt.PeripheralTurnOn), ("off", Txt.PeripheralTurnOff) }
        .Select(o => new OptionItem(o.Item1, o.Item2) { IsSelected = o.Item1 == now }).ToList();

    void LoadSharkGamemode()
    {
        _gmAngle = _gamemode?.AngleSnap switch { true => "on", false => "off", _ => "" };
        _gmRipple = _gamemode?.Ripple switch { true => "on", false => "off", _ => "" };
        _gmDebounceText = _gamemode?.DebounceMs?.ToString();
    }

    void SaveSharkGamemode(GamemodePeripheralEntry entry)
    {
        if (!CanGmShark) return;
        if (entry.Dpi is { } dpi && !AttackSharkCodec.ValidDpi(dpi)) throw new InvalidOperationException(Txt.SharkStagesNote);
        if (_gmAngle != "") entry.AngleSnap = _gmAngle == "on";
        if (_gmRipple != "") entry.Ripple = _gmRipple == "on";
        if (!string.IsNullOrWhiteSpace(_gmDebounceText))
            entry.DebounceMs = Int(_gmDebounceText) is { } ms and >= 4 and <= 50 && ms % 2 == 0 ? ms : throw new InvalidOperationException(Txt.SharkGmDebounceInvalid);
    }

    static int? HexColor(string? text) =>
        text?.Trim().TrimStart('#') is { Length: 6 } h && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : null;
}

/// <summary>One DPI stage of the X11: its value and the colour the mouse shows when it's chosen.</summary>
public sealed class SharkStageViewModel : ObservableObject
{
    string? _dpiText, _colorText;

    public SharkStageViewModel(int stage, int dpi, int color, Func<SharkStageViewModel, Task> apply)
    {
        Stage = stage;
        _dpiText = dpi.ToString();
        _colorText = $"#{color:X6}";
        ApplyCommand = new AsyncCommand(() => apply(this));
    }

    public int Stage { get; }
    public string Label => Txt.SharkStageLabel(Stage);
    public string? DpiText { get => _dpiText; set => Set(ref _dpiText, value); }
    public string? ColorText { get => _colorText; set => Set(ref _colorText, value); }
    public ICommand ApplyCommand { get; }
}

/// <summary>One of the X11's five buttons: a function from the list, a key (with modifiers) or a macro (set in the macro editor).</summary>
public sealed class SharkButtonViewModel : ObservableObject
{
    SharkFunction? _function;
    UsageOption? _key;
    int _modifiers;

    public SharkButtonViewModel(int slot, string label, ButtonAction now, Func<SharkButtonViewModel, Task> apply)
    {
        Slot = slot;
        Label = label;
        Current = now;
        _function = AttackSharkButtons.Functions.FirstOrDefault(f => f.Action == now.Action)
            ?? (now.Action == 0x11 ? AttackSharkButtons.KeyFunction : now.Action == 0x12 ? AttackSharkButtons.MacroFunction : null);
        if (now.Action == 0x11)
        {
            _key = PeripheralRowViewModel.Usages.FirstOrDefault(u => u.Code == now.Value && u.Code != 0);
            _modifiers = Array.IndexOf(AttackSharkButtons.ModifierValues, now.Modifiers) is var m and >= 0 ? m : 0;
        }
        ApplyCommand = new AsyncCommand(() => apply(this));
    }

    public int Slot { get; }
    public string Label { get; }
    public ButtonAction Current { get; }
    public IReadOnlyList<SharkFunction> Functions => AttackSharkButtons.Choices;
    public SharkFunction? Function { get => _function; set { if (Set(ref _function, value)) Raise(nameof(IsKey)); } }
    public bool IsKey => _function == AttackSharkButtons.KeyFunction;
    public UsageOption? Key { get => _key; set => Set(ref _key, value); }
    public IReadOnlyList<string> Modifiers => AttackSharkButtons.ModifierLabels;
    public int ModifierIndex { get => _modifiers; set => Set(ref _modifiers, value); }
    public ICommand ApplyCommand { get; }

    /// <summary>What Apply writes; null when Key is chosen without a key. Macro keeps the bound macro (it's written from the editor).</summary>
    public ButtonAction? Action() => _function switch
    {
        null => null,
        var f when f == AttackSharkButtons.MacroFunction => Current.Action == 0x12 ? Current : null,
        var f when f == AttackSharkButtons.KeyFunction => _key is { Code: > 0 } k ? ButtonAction.Key(k.Code, AttackSharkButtons.ModifierValues[_modifiers]) : null,
        var f => new ButtonAction(f.Action, 0, 0),
    };

    public override string ToString() => Label;
}

public sealed record SharkFunction(byte Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The X11's buttons and the functions the vendor app offers for them (MiguelSanzBr's decompile, table 0x4ec3b8).</summary>
public static class AttackSharkButtons
{
    public static readonly (int Slot, string Label)[] Physical =
    {
        (0, Txt.SharkButtonLeft), (1, Txt.SharkButtonRight), (2, Txt.SharkButtonMiddle), (6, Txt.SharkButtonFront), (7, Txt.SharkButtonRear),
    };

    public static readonly SharkFunction KeyFunction = new(0x11, Txt.SharkFnKey), MacroFunction = new(0x12, Txt.SharkFnMacro);

    public static readonly SharkFunction[] Functions =
    {
        new(0x02, Txt.SharkFnLeft), new(0x03, Txt.SharkFnRight), new(0x04, Txt.SharkFnMiddle), new(0x06, Txt.SharkFnForward), new(0x05, Txt.SharkFnBack),
        new(0x07, Txt.SharkFnDouble), new(0x08, Txt.SharkFnFire), new(0x09, Txt.SharkFnScrollUp), new(0x0A, Txt.SharkFnScrollDown),
        new(0x0B, Txt.SharkFnTiltLeft), new(0x0C, Txt.SharkFnTiltRight), new(0x0D, Txt.SharkFnDpiCycle), new(0x0E, Txt.SharkFnDpiUp), new(0x0F, Txt.SharkFnDpiDown),
        new(0x18, Txt.SharkFnPlayPause), new(0x17, Txt.SharkFnNext), new(0x16, Txt.SharkFnPrevious), new(0x1B, Txt.SharkFnVolumeUp), new(0x1C, Txt.SharkFnVolumeDown),
        new(0x1A, Txt.SharkFnMute), new(0x01, Txt.SharkFnOff),
    };

    public static readonly IReadOnlyList<SharkFunction> Choices = Functions.Append(KeyFunction).Append(MacroFunction).ToList();

    /// <summary>Modifier bits: 0 Ctrl, 1 Shift, 2 Alt, 3 Win.</summary>
    public static readonly byte[] ModifierValues = { 0x00, 0x01, 0x02, 0x04, 0x08, 0x03, 0x05, 0x06 };
    public static readonly string[] ModifierLabels = { Txt.PeripheralModeNone, "Ctrl", "Shift", "Alt", "Win", "Ctrl+Shift", "Ctrl+Alt", "Shift+Alt" };
}
