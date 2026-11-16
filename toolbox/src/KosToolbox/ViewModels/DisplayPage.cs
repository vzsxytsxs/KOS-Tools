using System.Globalization;
using System.Windows;
using System.Windows.Input;
using KosToolbox.Core.Display;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>A descriptor of the base block in CRU's "Detailed resolutions" list.</summary>
public sealed record DescriptorRow(EdidDescriptor Descriptor, string Text)
{
    public bool IsTiming => Descriptor.Kind == DescriptorKind.Timing;
}

public sealed record StandardRow(int Index, StandardTiming Timing)
{
    public string Text => Timing.ToString();
}

/// <summary>Established resolutions of one size (CRU groups them: 640x480 (4:3) with 60/72/75 Hz...).</summary>
public sealed record EstablishedGroup(string Title, IReadOnlyList<BitItem> Items);

/// <summary>Monitor in the top list; the label gets "(unsaved)" when there are pending edits.</summary>
public sealed class MonitorChoice(string key, string label) : ObservableObject
{
    string _label = label;
    public string Key { get; } = key;
    public string BaseLabel { get; } = label;
    public string Label { get => _label; set => Set(ref _label, value); }
    public override string ToString() => Label;
}

/// <summary>Audio format (bitstream) of the EDID audio block.</summary>
public sealed class AudioFormatItem(int code, string label, DisplayPageViewModel owner) : ObservableObject
{
    bool _on;
    public int Code { get; } = code;
    public string Label { get; } = label;
    public bool IsOn
    {
        get => _on;
        set { if (_on != value) owner.SetAudioFormat(this, value); }
    }
    internal void Show(bool on) { _on = on; Raise(nameof(IsOn)); }
}

/// <summary>Rectangle of a monitor in the layout (drawing coordinates); draggable.</summary>
public sealed class ArrangeItem(WinDisplay display, DisplayPageViewModel owner) : ObservableObject, IChartPoint
{
    double _left, _top, _width, _height;
    bool _isSelected;
    Point? _grab;
    public WinDisplay Display { get; } = display;
    public string Number => Display.Number.ToString();
    public string Name => Display.Name + (Display.Primary ? " - Primary" : "");
    public string Mode => $"{Display.Width} x {Display.Height} - {Display.Hz} Hz";
    public double Left { get => _left; set => Set(ref _left, value); }
    public double Top { get => _top; set => Set(ref _top, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public void DragStart() { _grab = null; owner.SelectedArrange = this; }
    public void DragTo(double x, double y)
    {
        _grab ??= new Point(x - Left, y - Top);
        Left = x - _grab.Value.X;
        Top = y - _grab.Value.Y;
    }
    public void DragEnd() { if (_grab is not null) owner.DropArrange(this); _grab = null; }
}

/// <summary>
/// Display tab of the GPU page, like CRU: the EDID of each monitor (detailed resolutions, VRR range, HDMI audio) in a
/// pending copy that Save writes as an override (the driver only uses it after a restart); and the Windows display
/// settings (layout, scale, resolution, rotation, refresh rate, HDR), which take effect immediately.
/// </summary>
public sealed partial class DisplayPageViewModel : PageViewModel
{
    List<MonitorInfo> _monitors = new();
    IReadOnlyList<MonitorChoice> _monitorItems = Array.Empty<MonitorChoice>();
    MonitorChoice? _selected;
    Edid? _work;
    readonly List<(string Key, string Text)> _changes = new();
    IReadOnlyList<DescriptorRow> _descriptors = Array.Empty<DescriptorRow>();
    IReadOnlyList<StandardRow> _standard = Array.Empty<StandardRow>();
    IReadOnlyList<ExtensionBlockInfo> _extensions = Array.Empty<ExtensionBlockInfo>();
    DescriptorRow? _selectedDescriptor;
    StandardRow? _selectedStandard;
    ExtensionBlockInfo? _selectedExtension;
    string _minHz = "", _maxHz = "", _maxClock = "";
    string? _message, _loadError;
    bool _isError, _busy;

    public DisplayPageViewModel() : base("display", Txt.PageDisplay, "", Txt.PageDisplaySubtitle)
    {
        AudioFormats = new[]
        {
            new AudioFormatItem(Edid.AudioAc3, "AC-3", this), new AudioFormatItem(Edid.AudioDts, "DTS", this), new AudioFormatItem(Edid.AudioEac3, "E-AC-3 (DD+)", this),
            new AudioFormatItem(Edid.AudioDtsHd, "DTS-HD", this), new AudioFormatItem(Edid.AudioTrueHd, "TrueHD", this),
        };
        Established = Edid.EstablishedModes.GroupBy(m => (m.Width, m.Height))
            .Select(g => new EstablishedGroup($"{g.Key.Width}x{g.Key.Height} ({StandardRatio(g.Key.Width, g.Key.Height)})",
                g.Select(m => new BitItem($"{m.Width}x{m.Height} @ {m.Hz} Hz", m.Bit, SetEstablished)).ToList())).ToList();
        AllEstablishedCommand = new RelayCommand(() => SetAllEstablished(true), () => _work is not null && !_busy);
        NoEstablishedCommand = new RelayCommand(() => SetAllEstablished(false), () => _work is not null && !_busy);

        AddDetailedCommand = new RelayCommand(AddDetailed, () => _work is { FreeDescriptors: > 0 } && !_busy);
        EditDetailedCommand = new RelayCommand(EditDetailed, () => _selectedDescriptor is { IsTiming: true } && !_busy);
        DeleteDetailedCommand = new RelayCommand(DeleteDetailed, () => _selectedDescriptor is { Descriptor.Preferred: false } && !_busy);
        DetailedUpCommand = new RelayCommand(() => MoveDetailed(-1), () => _selectedDescriptor is { Descriptor.Offset: > 72 } && !_busy);
        DetailedDownCommand = new RelayCommand(() => MoveDetailed(1), () => _selectedDescriptor is { Descriptor.Offset: > 54 and < 108 } && !_busy);

        AddStandardCommand = new RelayCommand(AddStandard, () => _work is not null && _standard.Count < Edid.StandardSlots && !_busy);
        EditStandardCommand = new RelayCommand(EditStandard, () => _selectedStandard is not null && !_busy);
        DeleteStandardCommand = new RelayCommand(() => SetStandard(l => l.RemoveAt(_selectedStandard!.Index), "Delete standard resolution"), () => _selectedStandard is not null && !_busy);
        ClearStandardCommand = new RelayCommand(() => SetStandard(l => l.Clear(), "Delete all standard resolutions"), () => _standard.Count > 0 && !_busy);
        StandardUpCommand = new RelayCommand(() => MoveStandard(-1), () => _selectedStandard is { Index: > 0 } && !_busy);
        StandardDownCommand = new RelayCommand(() => MoveStandard(1), () => _selectedStandard is { } s && s.Index < _standard.Count - 1 && !_busy);

        AddExtensionCommand = new RelayCommand(() => Edit($"ext:add:{_changes.Count}", "Add CTA-861 extension block", e => e.AddCtaBlock()), () => _work is { Extensions: < Edid.MaxBlocks - 1 } && !_busy);
        EditExtensionCommand = new RelayCommand(EditExtension, () => _selectedExtension is { IsCta: true } && !_busy);
        DeleteExtensionCommand = new RelayCommand(DeleteExtension, () => _selectedExtension is not null && !_busy);
        ExtensionUpCommand = new RelayCommand(() => MoveExtension(-1), () => _selectedExtension is { Index: > 1 } && !_busy);
        ExtensionDownCommand = new RelayCommand(() => MoveExtension(1), () => _selectedExtension is { } x && x.Index < _extensions.Count && !_busy);
        DiscardCommand = new RelayCommand(Load, () => !_busy && _changes.Count > 0);
        SaveCommand = new AsyncCommand(SaveAsync, () => !_busy && _changes.Count > 0);
        RestoreCommand = new AsyncCommand(RestoreAsync, () => !_busy && Monitor?.Override is not null);
        RestartCommand = new AsyncCommand(RestartAsync, () => !_busy);
        RescanCommand = new AsyncCommand(RescanAsync, () => !_busy);
        MaxRefreshCommand = new RelayCommand(() => { if (RefreshRates.Count > 0) SelectedRefresh = RefreshRates[0]; }, () => RefreshRates.Count > 0);
        MakePrimaryCommand = new RelayCommand(MakePrimary, () => _win is { Primary: false });
    }

    public override bool ScrollsItself => false;
    public IReadOnlyList<AudioFormatItem> AudioFormats { get; }
    public IReadOnlyList<EstablishedGroup> Established { get; }
    public ICommand AllEstablishedCommand { get; }
    public ICommand NoEstablishedCommand { get; }
    public ICommand AddDetailedCommand { get; }
    public ICommand EditDetailedCommand { get; }
    public ICommand DeleteDetailedCommand { get; }
    public ICommand DetailedUpCommand { get; }
    public ICommand DetailedDownCommand { get; }
    public ICommand AddStandardCommand { get; }
    public ICommand EditStandardCommand { get; }
    public ICommand DeleteStandardCommand { get; }
    public ICommand ClearStandardCommand { get; }
    public ICommand StandardUpCommand { get; }
    public ICommand StandardDownCommand { get; }
    public ICommand AddExtensionCommand { get; }
    public ICommand EditExtensionCommand { get; }
    public ICommand DeleteExtensionCommand { get; }
    public ICommand ExtensionUpCommand { get; }
    public ICommand ExtensionDownCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand MaxRefreshCommand { get; }
    public ICommand MakePrimaryCommand { get; }

    // ---------- monitor (EDID) ----------
    MonitorInfo? Monitor => _monitors.FirstOrDefault(m => m.InstanceId == _selected?.Key);
    public IReadOnlyList<MonitorChoice> MonitorItems { get => _monitorItems; private set { Set(ref _monitorItems, value); Raise(nameof(HasMonitors)); } }
    public bool HasMonitors => _monitorItems.Count > 0;
    public bool HasEdid => _work is not null;
    public string? MonitorName => _work is null ? null : _work.Name ?? _selected?.BaseLabel;
    public bool IsActive => Monitor?.Active == true;
    public string ProductId => _work?.ProductId ?? "";
    public string ExtensionBlocks => _work?.Extensions.ToString() ?? "";
    public string RegistryKey => Monitor?.InstanceId ?? "";
    public string DeviceName => Monitor?.Description ?? "";
    public string NativeResolution => _work?.NativeTiming is { } n ? $"{n.HActive} x {n.VActive} @ {n.RefreshHz.ToString("0.000", CultureInfo.InvariantCulture)} Hz" : "—";
    public string SourceText => Monitor?.Override is not null ? Txt.EdidSourceOverride : Txt.EdidSourceMonitor;
    public bool HasOverride => Monitor?.Override is not null;
    public string? NoCtaText => _work is { HasCta: false } ? Txt.EdidNoCta : null;
    public bool HasCta => _work is { HasCta: true };
    public bool HasPending => _changes.Count > 0;
    public string PendingText => Txt.EdidPending(_changes.Count);
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public MonitorChoice? SelectedMonitor
    {
        get => _selected;
        set
        {
            if (value is null || value == _selected) return;
            if (HasPending) Toasts.Show(Txt.PendingDiscarded(_changes.Count), ToastKind.Info);
            _selected = value;
            Raise();
            Load();
        }
    }

    // ---------- HDMI audio ----------
    public bool AudioEnabled
    {
        get => _work is { BasicAudio: true };
        set
        {
            if (value == AudioEnabled) return;
            Edit("audio", value ? Txt.EnableHdmiAudio : "Disable HDMI audio", e => { if (value) e.EnableBasicAudio(); else e.DisableAudio(); });
        }
    }
    public string AudioStatus => _work is not { HasCta: true } ? Txt.EdidNoCta
        : (AudioEnabled ? "HDMI audio enabled." : "HDMI audio disabled.") + " Applied on the next driver restart.";

    internal void SetAudioFormat(AudioFormatItem item, bool on) =>
        Edit($"audio:{item.Code}", $"{(on ? "Add" : "Remove")} {item.Label} audio", e => e.SetAudioFormat(item.Code, on));

    // ---------- range (VRR) ----------
    public bool RangeEnabled
    {
        get => _work?.Range is not null;
        set
        {
            if (value == RangeEnabled) return;
            if (!value) { Edit("range", "Disable refresh rate range", e => e.RemoveRange()); return; }
            var native = _work?.NativeTiming;
            var max = (int)Math.Round(native?.RefreshHz ?? 60);
            var clock = (int)Math.Ceiling((native?.PixelClockKhz ?? 150_000) / 10_000.0) * 10;
            Edit("range", Txt.EdidChangeRange(48, max), e => e.SetRange(Math.Min(48, max), max, clock));
        }
    }
    public string MinHz { get => _minHz; set { _minHz = value ?? ""; RangeEdited(); } }
    public string MaxHz { get => _maxHz; set { _maxHz = value ?? ""; RangeEdited(); } }
    public string MaxPixelClock { get => _maxClock; set { _maxClock = value ?? ""; RangeEdited(); } }
    public string RangeNote => _work?.Range is { } r ? $"Horizontal {r.MinHKhz}-{r.MaxHKhz} kHz (auto). Applied on the next driver restart." : "No range: the driver uses the display's fixed refresh rates.";

    void RangeEdited()
    {
        if (_work?.Range is null || !int.TryParse(_minHz, out var min) || !int.TryParse(_maxHz, out var max) || !int.TryParse(_maxClock, out var clock)) return;
        if (min is < 1 or > 510 || max is < 1 or > 510 || min > max || clock is < 10 or > 2550) return; // still typing
        var r = _work.Range;
        if (r.MinVHz == min && r.MaxVHz == max && (int)Math.Ceiling(clock / 10.0) * 10 == r.MaxPixelClockMhz) return;
        Edit("range", Txt.EdidChangeRange(min, max), e => e.SetRange(min, max, clock), keepFields: true);
    }

    internal static string Describe(DetailedTiming t) =>
        $"{t.HActive} x {t.VActive}{(t.Interlaced ? "i" : "")} @ {t.RefreshHz.ToString("0.000", CultureInfo.InvariantCulture)} Hz ({(t.PixelClockKhz / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} MHz) [{(t.HPositive ? "+" : "-")}/{(t.VPositive ? "+" : "-")}]";

    internal static DetailedTimingInfo Info(DetailedTiming t) => new(t.HActive, t.VActive, t.RefreshHz, t.PixelClockKhz / 1000.0, t.HTotal, t.VTotal);

    /// <summary>Starting point of "Add resolution": the native size at 60 Hz (LCD standard), with the native image size in mm.</summary>
    internal static DetailedTiming NewTiming(DetailedTiming? native) =>
        DetailedTiming.Generate(TimingMode.LcdStandard, native?.HActive ?? 1920, native?.VActive ?? 1080, 60, native) with { WidthMm = native?.WidthMm ?? 0, HeightMm = native?.HeightMm ?? 0 };

    static string StandardRatio(int w, int h) => new StandardTiming(w, h, 60).Ratio;

    // ---------- established resolutions ----------
    void SetEstablished(BitItem item) => Edit("established", "Established resolutions", e => e.SetEstablished(item.Bit, item.IsOn));

    void SetAllEstablished(bool on) => Edit("established", "Established resolutions", e =>
    {
        foreach (var m in Edid.EstablishedModes) e.SetEstablished(m.Bit, on);
    });

    // ---------- detailed resolutions (base block descriptors) ----------
    public IReadOnlyList<DescriptorRow> Detailed { get => _descriptors; private set => Set(ref _descriptors, value); }
    public DescriptorRow? SelectedDetailed { get => _selectedDescriptor; set { Set(ref _selectedDescriptor, value); CommandManager.InvalidateRequerySuggested(); } }
    public string DetailedSlotsText => _work is null ? "" : $"Detailed resolutions ({Txt.Count(_work.FreeDescriptors, "slot")} left)";

    string DescribeDescriptor(EdidDescriptor d) => d.Kind switch
    {
        DescriptorKind.Timing => Describe(d.Timing!) + (d.Preferred ? "  · preferred" : ""),
        DescriptorKind.Name => $"Name: {d.Text}",
        DescriptorKind.Serial => $"Serial number: {d.Text}",
        DescriptorKind.Text => $"Text: {d.Text}",
        DescriptorKind.Range => _work?.Range is { } r ? $"Range limits: {r.MinVHz}-{r.MaxVHz} Hz, {r.MaxPixelClockMhz} MHz" : "Range limits",
        DescriptorKind.StandardTimings => "More standard resolutions",
        DescriptorKind.ColorPoint => "Color point",
        _ => $"Descriptor {d.Text}",
    };

    void AddDetailed()
    {
        if (_work is null) return;
        var native = _work.NativeTiming;
        if (TimingDialog.Open(new TimingEditorViewModel("Add resolution", "Add", NewTiming(native), TimingMode.LcdStandard, native, false)) is not { } r) return;
        Edit($"add:{Describe(r.Timing)}", Txt.EdidChangeAdded(Describe(r.Timing)), e =>
        {
            e.AddBaseTiming(r.Timing);
            if (r.Preferred && e.Find(r.Timing) is { } added) e.MakePreferred(added);
        });
    }

    void EditDetailed()
    {
        if (_work is null || _selectedDescriptor is not { Descriptor: { Timing: { } timing } d }) return;
        if (TimingDialog.Open(new TimingEditorViewModel("Edit resolution", "Save", timing, TimingMode.Manual, _work.NativeTiming, d.Preferred)) is not { } r) return;
        var at = new EdidTiming(0, d.Offset, timing, d.Preferred);
        Edit($"edit:0:{d.Offset}", $"Edit resolution → {Describe(r.Timing)}", e =>
        {
            e.ReplaceTiming(at, r.Timing);
            if (r.Preferred) e.MakePreferred(at with { Timing = r.Timing });
        });
    }

    void DeleteDetailed()
    {
        if (_selectedDescriptor is not { } row) return;
        Edit($"clear:{row.Descriptor.Offset}", $"Delete {row.Text}", e => e.ClearDescriptor(row.Descriptor.Offset));
    }

    void MoveDetailed(int delta)
    {
        if (_selectedDescriptor is not { } row) return;
        var offset = row.Descriptor.Offset;
        Edit($"move:{_changes.Count}", "Reorder detailed resolutions", e => e.MoveDescriptor(offset, delta));
        _selectedDescriptor = _descriptors.FirstOrDefault(x => x.Descriptor.Offset == offset + delta * 18);
        Raise(nameof(SelectedDetailed));
    }

    // ---------- standard resolutions ----------
    public IReadOnlyList<StandardRow> Standard { get => _standard; private set => Set(ref _standard, value); }
    public StandardRow? SelectedStandard { get => _selectedStandard; set { Set(ref _selectedStandard, value); CommandManager.InvalidateRequerySuggested(); } }
    public string StandardSlotsText => _work is null ? "" : $"Standard resolutions ({Txt.Count(Edid.StandardSlots - _standard.Count, "slot")} left)";

    void SetStandard(Action<List<StandardTiming>> change, string description, int? select = null)
    {
        Edit($"standard:{_changes.Count}", description, e =>
        {
            var list = e.StandardTimings.ToList();
            change(list);
            e.SetStandardTimings(list);
        });
        _selectedStandard = select is { } i && i < _standard.Count ? _standard[i] : null;
        Raise(nameof(SelectedStandard));
    }

    void AddStandard()
    {
        var vm = new StandardEditorViewModel("Standard Resolution", new StandardTiming(1920, 1080, 60));
        if (EditorDialog.Open(vm)) { var t = vm.Build(); SetStandard(l => l.Add(t), $"Add {t}", _standard.Count); }
    }

    void EditStandard()
    {
        if (_selectedStandard is not { } row) return;
        var vm = new StandardEditorViewModel("Standard Resolution", row.Timing);
        if (EditorDialog.Open(vm)) { var t = vm.Build(); SetStandard(l => l[row.Index] = t, $"Edit {row.Timing} → {t}", row.Index); }
    }

    void MoveStandard(int delta)
    {
        if (_selectedStandard is not { } row) return;
        var j = row.Index + delta;
        SetStandard(l => (l[row.Index], l[j]) = (l[j], l[row.Index]), "Reorder standard resolutions", j);
    }

    // ---------- extension blocks ----------
    public IReadOnlyList<ExtensionBlockInfo> Extensions { get => _extensions; private set => Set(ref _extensions, value); }
    public ExtensionBlockInfo? SelectedExtension { get => _selectedExtension; set { Set(ref _selectedExtension, value); CommandManager.InvalidateRequerySuggested(); } }
    public string ExtensionSlotsText => _work is null ? "" : $"Extension blocks ({Txt.Count(Edid.MaxBlocks - 1 - _work.Extensions, "slot")} left)";

    void EditExtension()
    {
        if (_work is null || _selectedExtension is not { IsCta: true } x) return;
        var vm = new CtaEditorViewModel(_work.Block(x.Index), _work.NativeTiming);
        if (!EditorDialog.Open(vm)) return;
        var bytes = vm.Result;
        if (bytes.AsSpan(0, 127).SequenceEqual(_work.Block(x.Index).AsSpan(0, 127))) return;
        Edit($"ext:{x.Index}:{_changes.Count}", $"Edit extension block {x.Index}", e => e.SetBlock(x.Index, bytes));
        _selectedExtension = _extensions.FirstOrDefault(e => e.Index == x.Index);
        Raise(nameof(SelectedExtension));
    }

    void DeleteExtension()
    {
        if (_selectedExtension is not { } x) return;
        Edit($"ext:del:{_changes.Count}", $"Delete extension block {x.Index} ({x.Text})", e => e.RemoveBlock(x.Index));
    }

    void MoveExtension(int delta)
    {
        if (_selectedExtension is not { } x) return;
        Edit($"ext:move:{_changes.Count}", "Reorder extension blocks", e => e.MoveBlock(x.Index, delta));
        _selectedExtension = _extensions.FirstOrDefault(e => e.Index == x.Index + delta);
        Raise(nameof(SelectedExtension));
    }

    // ---------- reading / editing ----------
    public override async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            _monitors = await Task.Run(Monitors.List);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading monitors failed", ex);
            _monitors = new();
            LoadError = Txt.DisplayReadFailed(ex.Message);
        }
        var keep = _selected?.Key;
        MonitorItems = _monitors.Select(m => new MonitorChoice(m.InstanceId, Label(m))).ToList();
        _selected = _monitorItems.FirstOrDefault(i => i.Key == keep) ?? _monitorItems.FirstOrDefault();
        Raise(nameof(SelectedMonitor));
        Load();
        await LoadWindowsAsync();
    }

    async Task RescanAsync()
    {
        if (HasPending) Toasts.Show(Txt.PendingDiscarded(_changes.Count), ToastKind.Info);
        _changes.Clear();
        await RefreshAsync();
    }

    static string Label(MonitorInfo m)
    {
        string? name = null, product = null;
        try { var e = Edid.Parse(m.Edid); name = e.Name; product = e.ProductId; } catch (FormatException) { }
        return $"{product ?? m.InstanceId.Split('\\')[1]} - {name ?? "Monitor"}{(m.Active ? $" ({Txt.ActiveMonitor.ToLowerInvariant()})" : "")}{(m.Override is not null ? $" · {Txt.OverridePill}" : "")}";
    }

    /// <summary>Working copy built from what the driver will use (the override, if any; otherwise the monitor's EDID).</summary>
    void Load()
    {
        _changes.Clear();
        try { _work = Monitor is { } m ? Edid.Parse(m.Effective) : null; }
        catch (FormatException ex)
        {
            _work = null;
            LoadError = ex.Message;
        }
        Changed(keepFields: false);
    }

    void Changed(bool keepFields)
    {
        if (!keepFields)
        {
            _minHz = _work?.Range?.MinVHz.ToString() ?? "";
            _maxHz = _work?.Range?.MaxVHz.ToString() ?? "";
            _maxClock = _work?.Range?.MaxPixelClockMhz.ToString() ?? "";
        }
        // CRU's lists; the selection stays on the same slot.
        var keepDetailed = _selectedDescriptor?.Descriptor.Offset;
        Detailed = _work?.Descriptors.Where(d => d.Kind != DescriptorKind.Dummy).Select(d => new DescriptorRow(d, DescribeDescriptor(d))).ToList() ?? new List<DescriptorRow>();
        _selectedDescriptor = _descriptors.FirstOrDefault(d => d.Descriptor.Offset == keepDetailed);
        var keepStandard = _selectedStandard?.Index;
        Standard = _work?.StandardTimings.Select((t, i) => new StandardRow(i, t)).ToList() ?? new List<StandardRow>();
        _selectedStandard = keepStandard is { } s && s < _standard.Count ? _standard[s] : null;
        var keepExtension = _selectedExtension?.Index;
        Extensions = _work?.ExtensionList ?? Array.Empty<ExtensionBlockInfo>();
        _selectedExtension = _extensions.FirstOrDefault(x => x.Index == keepExtension);
        foreach (var item in Established.SelectMany(g => g.Items)) item.Show(_work?.Established(item.Bit) == true);
        var formats = _work?.AudioFormats ?? Array.Empty<int>();
        foreach (var f in AudioFormats) f.Show(formats.Contains(f.Code));
        foreach (var item in _monitorItems) item.Label = item.BaseLabel + (item == _selected && _changes.Count > 0 ? " (unsaved)" : "");
        // The range fields are not re-read while they are being typed in.
        foreach (var name in new[] { nameof(HasEdid), nameof(MonitorName), nameof(IsActive), nameof(ProductId), nameof(ExtensionBlocks), nameof(RegistryKey),
            nameof(DeviceName), nameof(NativeResolution), nameof(SourceText), nameof(HasOverride), nameof(NoCtaText), nameof(HasCta), nameof(HasPending), nameof(PendingText),
            nameof(AudioEnabled), nameof(AudioStatus), nameof(RangeEnabled), nameof(RangeNote), nameof(SelectedDetailed), nameof(DetailedSlotsText),
            nameof(SelectedStandard), nameof(StandardSlotsText), nameof(SelectedExtension), nameof(ExtensionSlotsText) })
            Raise(name);
        if (!keepFields) { Raise(nameof(MinHz)); Raise(nameof(MaxHz)); Raise(nameof(MaxPixelClock)); }
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Edits the working copy; edits with the same key (e.g. the range) count as a single pending change.</summary>
    void Edit(string key, string description, Action<Edid> edit, bool keepFields = false)
    {
        if (_work is null) return;
        try
        {
            edit(_work);
            _changes.RemoveAll(c => c.Key == key);
            _changes.Add((key, description));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Report(ex.Message, true); }
        Changed(keepFields);
    }

    async Task SaveAsync()
    {
        if (Monitor is not { } m || _work is null) return;
        await RunAsync(async () => { await Monitors.SaveAsync(m.InstanceId, _work.ToBytes()); return Txt.OverrideSaved; });
    }

    async Task RestoreAsync()
    {
        if (Monitor is not { } m) return;
        await RunAsync(async () => { await Monitors.SaveAsync(m.InstanceId, null); return Txt.OverrideRemoved; });
    }

    async Task RestartAsync()
    {
        await RunAsync(async () => Txt.DisplayRestarted(await Monitors.RestartAdaptersAsync()));
    }

    async Task RunAsync(Func<Task<string>> op)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        string message;
        var error = false;
        try { message = await op(); }
        catch (ElevationCancelledException ex) { message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("Display change failed", ex);
            message = ex.Message;
            error = true;
        }
        finally { _busy = false; }
        await RefreshAsync(); // the state shown always comes from a fresh read
        Report(message, error);
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }

    // =====================================================================
    // Windows display settings (take effect immediately)
    // =====================================================================
    List<WinDisplay> _wins = new();
    List<DisplayTarget> _targets = new();
    List<WinMode> _modes = new();
    WinDisplay? _win;
    ArrangeItem? _selectedArrange;
    IReadOnlyList<ArrangeItem> _arrange = Array.Empty<ArrangeItem>();
    Size _arrangeSize = new(900, 290);
    (int Current, int Recommended, IReadOnlyList<int> Options)? _scale;
    bool _hdrSupported, _hdr, _applyingWin;

    public IReadOnlyList<ArrangeItem> Arrangement { get => _arrange; private set => Set(ref _arrange, value); }
    public Size ArrangeSize
    {
        get => _arrangeSize;
        set { if (!value.IsEmpty && value.Width > 1 && value != _arrangeSize) { _arrangeSize = value; LayoutArrangement(); } }
    }
    public ArrangeItem? SelectedArrange
    {
        get => _selectedArrange;
        set
        {
            if (value is null || !Set(ref _selectedArrange, value)) return;
            foreach (var a in _arrange) a.IsSelected = a == value;
            _win = value.Display;
            MainViewModel.Fire(LoadSelectedAsync());
        }
    }
    public bool HasWindowsDisplays => _wins.Count > 0;
    public bool CanMakePrimary => _win is { Primary: false } && _wins.Count > 1;
    public string WinName => _win is { } w ? $"{w.Number}. {w.Name}{(w.Primary ? " (Primary)" : "")}" : "";

    public IReadOnlyList<Choice> Scales => _scale is { } s ? s.Options.Select(p => new Choice(p, p == s.Recommended ? $"{p}% (Recommended)" : $"{p}%")).ToList() : Array.Empty<Choice>();
    public bool ScaleAvailable => _scale is not null;
    public int SelectedScale
    {
        get => _scale?.Current ?? 100;
        set { if (_scale is { } s && value != s.Current && Target is { } t) ApplyWin(() => DisplaySettings.SetScale(t, value), $"Display scale set to {value}%.", confirm: false); }
    }

    static readonly string[] OrientationNames = { "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)" };
    public IReadOnlyList<Choice> Rotations { get; } = OrientationNames.Select((n, i) => new Choice(i, n)).ToList();
    public int SelectedRotation
    {
        get => _win?.Orientation ?? 0;
        set
        {
            if (_win is not { } w || value == w.Orientation) return;
            var (lw, lh) = Landscape(w);
            ApplyMode(lw, lh, w.Hz, value, $"Rotation set to {OrientationNames[value]}.");
        }
    }

    /// <summary>Resolutions always in landscape (rotation swaps width and height at apply time).</summary>
    public IReadOnlyList<Choice> Resolutions => _modes.Select(m => (m.Width, m.Height)).Distinct()
        .Select((r, i) => new Choice(r.Width * 100_000 + r.Height, $"{r.Width} × {r.Height}")).ToList();
    public int SelectedResolution
    {
        get => _win is { } w ? Landscape(w) is var (lw, lh) ? lw * 100_000 + lh : 0 : 0;
        set
        {
            if (_win is not { } w || value == SelectedResolution) return;
            int width = value / 100_000, height = value % 100_000;
            var rates = _modes.Where(m => m.Width == width && m.Height == height).Select(m => m.Hz).ToList();
            var hz = rates.Contains(w.Hz) ? w.Hz : rates.DefaultIfEmpty(60).Max();
            ApplyMode(width, height, hz, w.Orientation, $"Resolution set to {width} × {height}.");
        }
    }
    public IReadOnlyList<int> RefreshRates => _win is { } w && Landscape(w) is var (lw, lh)
        ? _modes.Where(m => m.Width == lw && m.Height == lh).Select(m => m.Hz).Distinct().OrderDescending().ToList() : new List<int>();
    public IReadOnlyList<Choice> RefreshChoices => RefreshRates.Select(r => new Choice(r, $"{r} Hz")).ToList();
    public int SelectedRefresh
    {
        get => _win?.Hz ?? 0;
        set
        {
            if (_win is not { } w || value == w.Hz || value <= 0) return;
            var (lw, lh) = Landscape(w);
            ApplyMode(lw, lh, value, w.Orientation, $"Refresh rate set to {value} Hz.");
        }
    }
    public bool HdrSupported => _hdrSupported && !_applyingWin;
    public bool Hdr
    {
        get => _hdr;
        set { if (value != _hdr && Target is { } t) ApplyWin(() => DisplaySettings.SetHdr(t, value), value ? "HDR on." : "HDR off.", confirm: false); }
    }

    DisplayTarget? Target => _targets.FirstOrDefault(t => string.Equals(t.GdiName, _win?.GdiName, StringComparison.OrdinalIgnoreCase));
    static (int W, int H) Landscape(WinDisplay w) => w.Orientation % 2 == 1 ? (w.Height, w.Width) : (w.Width, w.Height);

    async Task LoadWindowsAsync()
    {
        try
        {
            (_wins, _targets) = await Task.Run(() => (WindowsDisplays.List(), DisplaySettings.Targets()));
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading Windows displays failed: {ex.Message}");
            _wins = new();
        }
        var keep = _win?.GdiName;
        Arrangement = _wins.Select(w => new ArrangeItem(w, this)).ToList();
        LayoutArrangement();
        var selected = _arrange.FirstOrDefault(a => a.Display.GdiName == keep) ?? _arrange.FirstOrDefault(a => a.Display.Primary) ?? _arrange.FirstOrDefault();
        Raise(nameof(HasWindowsDisplays));
        if (selected is null) return;
        _selectedArrange = selected;
        foreach (var a in _arrange) a.IsSelected = a == selected;
        _win = selected.Display;
        Raise(nameof(SelectedArrange));
        await LoadSelectedAsync();
    }

    async Task LoadSelectedAsync()
    {
        if (_win is not { } w) return;
        var target = Target;
        var (modes, scale, hdr) = await Task.Run(() =>
        {
            var m = WindowsDisplays.Modes(w.GdiName);
            // Some drivers list the modes already rotated when the screen is in portrait: the list is always kept in landscape.
            if (w.Orientation % 2 == 1 && m.Any(x => x.Width == w.Width && x.Height == w.Height)) m = m.Select(x => x with { Width = x.Height, Height = x.Width }).ToList();
            (int, int, IReadOnlyList<int>)? s = null;
            (bool, bool) h = (false, false);
            if (target is not null)
            {
                try { s = DisplaySettings.ReadScale(target); } catch { /* no per-monitor scale */ }
                h = DisplaySettings.ReadHdr(target);
            }
            return (m, s, h);
        });
        if (_win != w) return;
        _modes = modes;
        _scale = scale;
        (_hdrSupported, _hdr) = hdr;
        RaiseWin();
        CommandManager.InvalidateRequerySuggested();
        await LoadDdcAsync(w);
    }

    bool _loadingWin;

    /// <summary>Re-reads the monitor's values into the view; whatever the lists send back in the meantime is ignored.</summary>
    void RaiseWin()
    {
        _loadingWin = true;
        try
        {
            foreach (var name in new[] { nameof(WinName), nameof(CanMakePrimary), nameof(Scales), nameof(ScaleAvailable), nameof(SelectedScale), nameof(SelectedRotation),
                nameof(Resolutions), nameof(SelectedResolution), nameof(RefreshRates), nameof(RefreshChoices), nameof(SelectedRefresh), nameof(HdrSupported), nameof(Hdr) })
                Raise(name);
        }
        finally { _loadingWin = false; }
    }

    /// <summary>Draws the monitors proportionally, centered in the layout area.</summary>
    void LayoutArrangement()
    {
        if (_arrange.Count == 0) return;
        int minX = _wins.Min(w => w.X), minY = _wins.Min(w => w.Y);
        int maxX = _wins.Max(w => w.X + w.Width), maxY = _wins.Max(w => w.Y + w.Height);
        var scale = Math.Min((_arrangeSize.Width - 60) / Math.Max(1, maxX - minX), (_arrangeSize.Height - 60) / Math.Max(1, maxY - minY));
        scale = Math.Min(scale, 0.19);
        var offsetX = (_arrangeSize.Width - (maxX - minX) * scale) / 2;
        var offsetY = (_arrangeSize.Height - (maxY - minY) * scale) / 2;
        foreach (var a in _arrange)
        {
            a.Left = offsetX + (a.Display.X - minX) * scale;
            a.Top = offsetY + (a.Display.Y - minY) * scale;
            a.Width = a.Display.Width * scale;
            a.Height = a.Display.Height * scale;
        }
        _layoutScale = scale;
        _layoutOrigin = (minX, minY, offsetX, offsetY);
    }

    double _layoutScale = 1;
    (int MinX, int MinY, double OffsetX, double OffsetY) _layoutOrigin;

    /// <summary>A monitor was dropped in the layout: snaps to the edge of the nearest neighbor and applies.</summary>
    internal void DropArrange(ArrangeItem item)
    {
        if (_wins.Count < 2) { LayoutArrangement(); return; }
        var w = item.Display;
        var x = (int)Math.Round((item.Left - _layoutOrigin.OffsetX) / _layoutScale) + _layoutOrigin.MinX;
        var y = (int)Math.Round((item.Top - _layoutOrigin.OffsetY) / _layoutScale) + _layoutOrigin.MinY;
        // Candidates: attached to the left/right/top/bottom of each other monitor, the one closest to where it was dropped.
        var best = (X: w.X, Y: w.Y, Distance: double.MaxValue);
        foreach (var o in _wins.Where(o => o.GdiName != w.GdiName))
        {
            var cy = Math.Clamp(y, o.Y - w.Height + 1, o.Y + o.Height - 1);
            var cx = Math.Clamp(x, o.X - w.Width + 1, o.X + o.Width - 1);
            foreach (var (px, py) in new[] { (o.X - w.Width, cy), (o.X + o.Width, cy), (cx, o.Y - w.Height), (cx, o.Y + o.Height) })
            {
                var overlaps = _wins.Any(z => z.GdiName != w.GdiName && px < z.X + z.Width && px + w.Width > z.X && py < z.Y + z.Height && py + w.Height > z.Y);
                var d = Math.Pow(px - x, 2) + Math.Pow(py - y, 2);
                if (!overlaps && d < best.Distance) best = (px, py, d);
            }
        }
        if (best.X == w.X && best.Y == w.Y) { LayoutArrangement(); return; }
        var positions = _wins.ToDictionary(z => z.GdiName, z => z.GdiName == w.GdiName ? (best.X, best.Y) : (z.X, z.Y));
        ApplyWin(() => WindowsDisplays.SetLayout(positions), "Display arrangement applied.", confirm: false);
    }

    void MakePrimary()
    {
        if (_win is not { } w) return;
        ApplyWin(() => WindowsDisplays.SetLayout(_wins.ToDictionary(z => z.GdiName, z => (z.X, z.Y)), w.GdiName), $"{w.Name} is now the main display.", confirm: false);
    }

    void ApplyMode(int landscapeW, int landscapeH, int hz, int orientation, string done)
    {
        if (_win is not { } w) return;
        var (lw, lh) = Landscape(w);
        var old = (lw, lh, w.Hz, w.Orientation);
        var (width, height) = orientation % 2 == 1 ? (landscapeH, landscapeW) : (landscapeW, landscapeH);
        ApplyWin(() => WindowsDisplays.SetMode(w.GdiName, width, height, hz, orientation), done, confirm: true,
            revert: () => WindowsDisplays.SetMode(w.GdiName, old.Item4 % 2 == 1 ? old.lh : old.lw, old.Item4 % 2 == 1 ? old.lw : old.lh, old.Hz, old.Item4));
    }

    /// <summary>
    /// Applies a Windows setting and re-reads. Mode changes ask for confirmation within 15 s, like Windows: with no
    /// answer (black screen), they revert to the previous mode by themselves.
    /// </summary>
    void ApplyWin(Action action, string done, bool confirm, Action? revert = null)
    {
        // One change at a time: while one is being applied (or "Keep changes?" is open) and while the view re-reads,
        // the lists may resend the selected value; that must not turn into a second change (and a second dialog).
        if (_loadingWin) return;
        if (_applyingWin) { Application.Current.Dispatcher.BeginInvoke(RaiseWin); return; } // the list goes back to the real value
        _applyingWin = true;
        Raise(nameof(HdrSupported));
        MainViewModel.Fire(Run());

        async Task Run()
        {
            try
            {
                await Task.Run(action);
                if (confirm && revert is not null && !KeepChanges())
                {
                    await Task.Run(revert);
                    Report("Display settings reverted.", false);
                }
                else Report(done, false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Windows display change failed: {ex.Message}");
                Report(ex.Message, true);
            }
            try { await LoadWindowsAsync(); }
            finally
            {
                _applyingWin = false;
                Raise(nameof(HdrSupported));
            }
        }
    }

    static bool KeepChanges()
    {
        const int Seconds = 15;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(Seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var d in Application.Current.Windows.OfType<Dialog>().ToList()) d.Close();
        };
        timer.Start();
        try { return Dialog.Confirm("Keep these display settings?", $"If you don't answer, the previous settings come back in {Seconds} seconds.", "Keep changes"); }
        finally { timer.Stop(); }
    }
}
