using System.Globalization;
using System.Windows.Input;
using KosToolbox.Core.Display;

namespace KosToolbox.ViewModels;

/// <summary>
/// Resolution editor like CRU's: automatic modes (size and refresh rate only) or manual (porches, sync, blanking,
/// polarity). One of the three (back porch / blanking / total) and one of the three frequencies (refresh / horizontal / pixel
/// clock) stay fixed; the others are calculated. The pixel clock is stored in 10 kHz steps, as in the EDID.
/// </summary>
public sealed class TimingEditorViewModel : ObservableObject
{
    static DetailedTiming? _clipboard;
    readonly DetailedTiming _initial;
    readonly TimingMode _initialMode;
    readonly DetailedTiming? _native;
    TimingMode _mode;
    int _ha, _hf, _hs, _hb, _va, _vf, _vs, _vb, _clock;
    bool _hp, _vp, _interlaced, _preferred;
    double _hz, _khz;
    string _anchor = Back, _freq = Refresh;
    string? _editing;
    readonly Dictionary<string, string> _text = new();

    public const string Back = "back", Blank = "blank", Total = "total", Refresh = "refresh", Horizontal = "horizontal", Clock = "clock";

    public TimingEditorViewModel(string title, string okText, DetailedTiming initial, TimingMode mode, DetailedTiming? native, bool preferred)
    {
        Title = title;
        OkText = okText;
        _initial = initial;
        _initialMode = mode;
        _native = native;
        _preferred = preferred;
        Modes = new[]
        {
            new Choice((int)TimingMode.Manual, "Manual"), new Choice((int)TimingMode.LcdStandard, "Automatic - LCD standard"),
            new Choice((int)TimingMode.LcdNative, "Automatic - LCD native"), new Choice((int)TimingMode.LcdReduced, "Automatic - LCD reduced"),
            new Choice((int)TimingMode.Exact, "Exact"), new Choice((int)TimingMode.ExactReduced, "Exact reduced"),
        };
        CopyCommand = new RelayCommand(() => { _clipboard = Build(); Raise(nameof(CanPaste)); try { System.Windows.Clipboard.SetText(Summary); } catch { /* clipboard busy */ } });
        PasteCommand = new RelayCommand(() => { if (_clipboard is { } t) { _mode = TimingMode.Manual; Load(t); } }, () => _clipboard is not null);
        ResetCommand = new RelayCommand(() => { _mode = _initialMode; Load(_initial); });
        _mode = mode;
        Load(initial);
    }

    public string Title { get; }
    public string OkText { get; }
    public IReadOnlyList<Choice> Modes { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand ResetCommand { get; }
    public bool CanPaste => _clipboard is not null;
    public IReadOnlyList<string> Polarities { get; } = new[] { "+", "-" };

    public int Mode
    {
        get => (int)_mode;
        set
        {
            if ((int)_mode == value) return;
            _mode = (TimingMode)value;
            if (!IsManual) { _freq = Refresh; Regenerate(); } else Sync();
            Raise("");
        }
    }
    public bool IsManual => _mode == TimingMode.Manual;
    public bool CanBack => IsManual && _anchor == Back;
    public bool CanBlank => IsManual && _anchor == Blank;
    public bool CanTotal => IsManual && _anchor == Total;
    public bool CanRefresh => !IsManual || _freq == Refresh;
    public bool CanHorizontal => IsManual && _freq == Horizontal;
    public bool CanClock => IsManual && _freq == Clock;

    // Radio buttons: which value stays fixed.
    public bool AnchorBack { get => _anchor == Back; set { if (value) SetAnchor(Back); } }
    public bool AnchorBlank { get => _anchor == Blank; set { if (value) SetAnchor(Blank); } }
    public bool AnchorTotal { get => _anchor == Total; set { if (value) SetAnchor(Total); } }
    public bool FreqRefresh { get => _freq == Refresh; set { if (value) SetFreq(Refresh); } }
    public bool FreqHorizontal { get => _freq == Horizontal; set { if (value) SetFreq(Horizontal); } }
    public bool FreqClock { get => _freq == Clock; set { if (value) SetFreq(Clock); } }

    void SetAnchor(string a) { _anchor = a; Raise(""); }
    void SetFreq(string f)
    {
        _freq = f;
        _hz = ActualHz;
        _khz = ActualKhz;
        Raise("");
    }

    // ---------- fields (text; each edit recalculates the rest) ----------
    public string HActive { get => T(nameof(HActive)); set => Edit(nameof(HActive), value); }
    public string VActive { get => T(nameof(VActive)); set => Edit(nameof(VActive), value); }
    public string HFront { get => T(nameof(HFront)); set => Edit(nameof(HFront), value); }
    public string VFront { get => T(nameof(VFront)); set => Edit(nameof(VFront), value); }
    public string HSync { get => T(nameof(HSync)); set => Edit(nameof(HSync), value); }
    public string VSync { get => T(nameof(VSync)); set => Edit(nameof(VSync), value); }
    public string HBack { get => T(nameof(HBack)); set => Edit(nameof(HBack), value); }
    public string VBack { get => T(nameof(VBack)); set => Edit(nameof(VBack), value); }
    public string HBlank { get => T(nameof(HBlank)); set => Edit(nameof(HBlank), value); }
    public string VBlank { get => T(nameof(VBlank)); set => Edit(nameof(VBlank), value); }
    public string HTotal { get => T(nameof(HTotal)); set => Edit(nameof(HTotal), value); }
    public string VTotal { get => T(nameof(VTotal)); set => Edit(nameof(VTotal), value); }
    public string RefreshText { get => T(nameof(RefreshText)); set => Edit(nameof(RefreshText), value); }
    public string HorizontalText { get => T(nameof(HorizontalText)); set => Edit(nameof(HorizontalText), value); }
    public string ClockText { get => T(nameof(ClockText)); set => Edit(nameof(ClockText), value); }
    public string HPolarity { get => _hp ? "+" : "-"; set { _hp = value == "+"; Changed(); } }
    public string VPolarity { get => _vp ? "+" : "-"; set { _vp = value == "+"; Changed(); } }
    public bool Interlaced { get => _interlaced; set { _interlaced = value; Changed(); } }
    public bool Preferred { get => _preferred; set => Set(ref _preferred, value); }

    int HTot => _ha + _hb;
    int VTot => _va + _vb;
    public double ActualHz => HTot == 0 || VTot == 0 ? 0 : _clock * 1000.0 / ((double)HTot * VTot);
    public double ActualKhz => HTot == 0 ? 0 : _clock / (double)HTot;
    public string ActualRefresh => $"Actual: {ActualHz.ToString("0.000", CultureInfo.InvariantCulture)} Hz";
    public string ActualHorizontal => $"Actual: {ActualKhz.ToString("0.000", CultureInfo.InvariantCulture)} kHz";
    public string Summary => $"{_ha} x {_va}{(_interlaced ? "i" : "")} @ {ActualHz.ToString("0.000", CultureInfo.InvariantCulture)} Hz ({(_clock / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} MHz) [{HPolarity}/{VPolarity}]";
    public string? Error => Build().Problem() is { } p ? $"This timing can't be stored in an EDID ({p})." : null;
    public bool IsValid => Error is null;

    string T(string name) => _text.GetValueOrDefault(name, "");

    static bool Int(string s, out int v) => int.TryParse(s.Trim(), out v);
    static bool Num(string s, out double v) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    void Edit(string name, string value)
    {
        _text[name] = value ?? "";
        // Incomplete or invalid text (e.g. an empty field while typing): the timing stays as it was.
        var isFrequency = name is nameof(RefreshText) or nameof(HorizontalText) or nameof(ClockText);
        int n = 0;
        double f = 0;
        if (isFrequency ? !Num(_text[name], out f) || f <= 0 : !Int(_text[name], out n) || n < 0) { Raise(nameof(Error)); Raise(nameof(IsValid)); return; }
        var hBack = _hb - _hf - _hs;
        var vBack = _vb - _vf - _vs;
        var hTotal = HTot;
        var vTotal = VTot;
        switch (name)
        {
            case nameof(HActive): _ha = n; break;
            case nameof(VActive): _va = n; break;
            case nameof(HFront): _hf = n; break;
            case nameof(VFront): _vf = n; break;
            case nameof(HSync): _hs = n; break;
            case nameof(VSync): _vs = n; break;
            case nameof(HBack): hBack = n; break;
            case nameof(VBack): vBack = n; break;
            case nameof(HBlank): _hb = n; break;
            case nameof(VBlank): _vb = n; break;
            case nameof(HTotal): hTotal = n; break;
            case nameof(VTotal): vTotal = n; break;
            case nameof(RefreshText): _hz = f; break;
            case nameof(HorizontalText): _khz = f; break;
            case nameof(ClockText): _clock = (int)Math.Round(f * 100) * 10; break;
        }
        _editing = name;
        try
        {
            if (!IsManual) { Regenerate(); return; }
            // The fixed value (back porch, blanking or total) stays the same when another field changes.
            if (name is not (nameof(HBlank) or nameof(VBlank)))
            {
                _hb = name == nameof(HTotal) || _anchor == Total ? hTotal - _ha : name == nameof(HBack) || _anchor == Back ? _hf + _hs + hBack : _hb;
                _vb = name == nameof(VTotal) || _anchor == Total ? vTotal - _va : name == nameof(VBack) || _anchor == Back ? _vf + _vs + vBack : _vb;
            }
            Changed();
        }
        finally { _editing = null; }
    }

    /// <summary>After the timing changes: the fixed frequency stays the same, the pixel clock follows.</summary>
    void Changed()
    {
        if (HTot > 0 && VTot > 0)
        {
            if (_freq == Refresh && _hz > 0) _clock = (int)Math.Round(HTot * (double)VTot * _hz / 10_000.0, MidpointRounding.AwayFromZero) * 10;
            else if (_freq == Horizontal && _khz > 0) _clock = (int)Math.Round(HTot * _khz / 10.0, MidpointRounding.AwayFromZero) * 10;
        }
        Sync();
    }

    void Regenerate()
    {
        if (_ha is < 64 or > 8192 || _va is < 64 or > 8192 || _hz is <= 1 or > 1000) { Sync(); return; }
        try { Fill(DetailedTiming.Generate(_mode, _ha, _va, _hz, _native)); }
        catch (ArgumentException) { /* size outside the formula: keeps the previous one */ }
        Sync();
    }

    void Load(DetailedTiming t)
    {
        Fill(t);
        _hz = Math.Round(t.RefreshHz, 3);
        _khz = ActualKhz;
        if (!IsManual) Regenerate(); else Sync();
        Raise("");
    }

    void Fill(DetailedTiming t)
    {
        _ha = t.HActive; _hf = t.HFront; _hs = t.HSync; _hb = t.HBlank;
        _va = t.VActive; _vf = t.VFront; _vs = t.VSync; _vb = t.VBlank;
        _clock = t.PixelClockKhz;
        _hp = t.HPositive; _vp = t.VPositive; _interlaced = t.Interlaced;
    }

    /// <summary>Rewrites the calculated fields (except the one being typed in).</summary>
    void Sync()
    {
        void Put(string name, string value)
        {
            if (name == _editing) return;
            _text[name] = value;
            Raise(name);
        }
        var inv = CultureInfo.InvariantCulture;
        Put(nameof(HActive), _ha.ToString()); Put(nameof(VActive), _va.ToString());
        Put(nameof(HFront), _hf.ToString()); Put(nameof(VFront), _vf.ToString());
        Put(nameof(HSync), _hs.ToString()); Put(nameof(VSync), _vs.ToString());
        Put(nameof(HBack), (_hb - _hf - _hs).ToString()); Put(nameof(VBack), (_vb - _vf - _vs).ToString());
        Put(nameof(HBlank), _hb.ToString()); Put(nameof(VBlank), _vb.ToString());
        Put(nameof(HTotal), HTot.ToString()); Put(nameof(VTotal), VTot.ToString());
        Put(nameof(RefreshText), (_freq == Refresh && _hz > 0 ? _hz : ActualHz).ToString("0.000", inv));
        Put(nameof(HorizontalText), ActualKhz.ToString("0.000", inv));
        Put(nameof(ClockText), (_clock / 1000.0).ToString("0.000", inv));
        foreach (var n in new[] { nameof(HPolarity), nameof(VPolarity), nameof(Interlaced), nameof(ActualRefresh), nameof(ActualHorizontal), nameof(Summary), nameof(Error), nameof(IsValid) }) Raise(n);
    }

    /// <summary>The timing as it would appear in the EDID (keeps the original's physical image size).</summary>
    public DetailedTiming Build() => new(_clock, _ha, _hb, _hf, _hs, _va, _vb, _vf, _vs, _interlaced, _hp, _vp, _initial.WidthMm, _initial.HeightMm);
}
