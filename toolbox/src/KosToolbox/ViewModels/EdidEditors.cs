using System.Globalization;
using System.Windows.Input;
using KosToolbox.Core.Display;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>Base of the EDID editor dialogs: OK stays enabled while <see cref="Error"/> is null.</summary>
public abstract class EdidEditorBase(string title, double width = 520) : ObservableObject, IEditorDialog
{
    public string Title { get; } = title;
    public double DialogWidth { get; } = width;
    public virtual string? Error => null;
    public bool IsValid => Error is null;
    protected void Validate() { Raise(nameof(Error)); Raise(nameof(IsValid)); }

    protected static bool Int(string s, out int v) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    /// <summary>An optional number: blank reads as 0.</summary>
    protected static bool IntOrBlank(string s, out int v) { v = 0; return s.Trim().Length == 0 || Int(s, out v); }
}

/// <summary>A checkbox of a bit list (established resolutions, speakers, colorimetry, EOTFs...).</summary>
public sealed class BitItem(string label, int bit, Action<BitItem>? changed = null) : ObservableObject
{
    bool _on;
    public string Label { get; } = label;
    public int Bit { get; } = bit;
    public bool IsOn { get => _on; set { if (Set(ref _on, value)) changed?.Invoke(this); } }
    /// <summary>Shows a value read from the EDID without reporting it as a change.</summary>
    internal void Show(bool on) { _on = on; Raise(nameof(IsOn)); }
}

/// <summary>Standard resolution (2 bytes): a common size or another one, and 60–123 Hz.</summary>
public sealed class StandardEditorViewModel : EdidEditorBase
{
    const int Other = -1;
    int _preset;
    string _width, _height, _rate;

    public StandardEditorViewModel(string title, StandardTiming initial) : base(title, 420)
    {
        Presets = StandardTiming.Presets.Select((p, i) => new Choice(i, new StandardTiming(p.Width, p.Height, 60).ToString().Split(" @")[0]))
            .Append(new Choice(Other, "Other resolution")).ToList();
        _preset = Array.IndexOf(StandardTiming.Presets, (initial.Width, initial.Height)) is var i and >= 0 ? i : Other;
        (_width, _height, _rate) = (initial.Width.ToString(), initial.Height.ToString(), initial.Hz.ToString());
    }

    public IReadOnlyList<Choice> Presets { get; }
    public int Preset
    {
        get => _preset;
        set
        {
            if (!Set(ref _preset, value)) return;
            if (value != Other) (_width, _height) = (StandardTiming.Presets[value].Width.ToString(), StandardTiming.Presets[value].Height.ToString());
            Raise(nameof(WidthText)); Raise(nameof(HeightText)); Raise(nameof(IsOther)); Validate();
        }
    }
    public bool IsOther => _preset == Other;
    public string WidthText { get => _width; set { _width = value ?? ""; Validate(); } }
    public string HeightText { get => _height; set { _height = value ?? ""; Validate(); } }
    public string RateText { get => _rate; set { _rate = value ?? ""; Validate(); } }

    public override string? Error => !Int(_width, out var w) || !Int(_height, out var h) || !Int(_rate, out var hz) ? "Enter whole numbers." : new StandardTiming(w, h, hz).Problem();
    public StandardTiming Build() => new(int.Parse(_width.Trim()), int.Parse(_height.Trim()), int.Parse(_rate.Trim()));
}

// =====================================================================
// Data blocks
// =====================================================================

/// <summary>Editor of one CTA-861 data block: <see cref="Build"/> gives the edited block.</summary>
public abstract class DataBlockEditor(string title, double width = 520) : EdidEditorBase(title, width)
{
    public abstract CtaDataBlock Build();
    public override string? Error
    {
        get
        {
            try { Build(); return null; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return ex.Message; }
        }
    }
    protected void Changed(BitItem _) => Validate();
}

/// <summary>A TV resolution (VIC) of the video data block.</summary>
public sealed class VicItem(Vic vic, Action changed) : ObservableObject
{
    bool _on, _native;
    public Vic Vic { get; } = vic;
    public string Label => Vic.ToString();
    public bool CanBeNative => _on && Vic.Code <= 64;
    public bool IsOn { get => _on; set { if (Set(ref _on, value)) { if (!value) Native = false; Raise(nameof(CanBeNative)); changed(); } } }
    public bool Native { get => _native; set { if (Set(ref _native, value && CanBeNative)) changed(); } }
}

public sealed class VideoBlockEditor : DataBlockEditor
{
    readonly List<int> _order;

    public VideoBlockEditor(CtaDataBlock? existing) : base("TV Resolutions", 520)
    {
        var entries = existing is null ? new List<VideoEntry>() : CtaFormats.ReadVideo(existing);
        _order = entries.Select(e => e.Code).ToList();
        Items = CtaFormats.Vics.Select(v => new VicItem(v, Validate)).ToList();
        foreach (var e in entries)
            if (Items.FirstOrDefault(i => i.Vic.Code == e.Code) is { } item) { item.IsOn = true; item.Native = e.Native; }
    }

    public IReadOnlyList<VicItem> Items { get; }
    public string CountText => $"{Items.Count(i => i.IsOn)} selected (the order of the ones already there is kept)";

    /// <summary>The ones already there keep their order (it is the preference order); new ones go after them.</summary>
    public override CtaDataBlock Build()
    {
        Raise(nameof(CountText));
        var on = Items.Where(i => i.IsOn).ToList();
        if (on.Count == 0) throw new ArgumentException("Choose at least one resolution.");
        var ordered = on.OrderBy(i => _order.IndexOf(i.Vic.Code) is var x and >= 0 ? x : int.MaxValue).ThenBy(i => Items.ToList().IndexOf(i));
        return CtaFormats.BuildVideo(ordered.Select(i => new VideoEntry(i.Vic.Code, i.Native)));
    }
}

public sealed class AudioBlockEditor : DataBlockEditor
{
    static readonly int[] Known = { 1, 2, 7, 10, 11, 12 };
    readonly Dictionary<int, byte[]> _existing;

    public AudioBlockEditor(CtaDataBlock? existing) : base("Audio Formats", 420)
    {
        _existing = (existing is null ? new List<byte[]>() : CtaFormats.ReadAudio(existing)).GroupBy(s => s[0] >> 3 & 0x0F).ToDictionary(g => g.Key, g => g.First());
        Items = Known.Union(_existing.Keys).Select(c => new BitItem(c == 1 ? "LPCM (2 channels)" : CtaFormats.AudioName(c), c, Changed)).ToList();
        foreach (var i in Items) i.Show(_existing.ContainsKey(i.Bit) || existing is null && i.Bit == 1);
    }

    public IReadOnlyList<BitItem> Items { get; }
    public string Note => "Formats already in the EDID keep their channels and sample rates; added ones get the usual ones.";

    public override CtaDataBlock Build()
    {
        var on = Items.Where(i => i.IsOn).ToList();
        if (on.Count == 0) throw new ArgumentException("Choose at least one format.");
        return CtaFormats.BuildAudio(on.Select(i => _existing.GetValueOrDefault(i.Bit) ?? CtaFormats.DefaultSad(i.Bit)!));
    }
}

public sealed class SpeakerEditor : DataBlockEditor
{
    public SpeakerEditor(CtaDataBlock? existing) : base("Speaker Setup", 560)
    {
        Items = CtaFormats.Speakers.Select((n, i) => new BitItem(n, i, Changed)).ToList();
        Load(existing is null ? CtaFormats.Stereo : CtaFormats.ReadSpeakers(existing));
        StereoCommand = new RelayCommand(() => Load(CtaFormats.Stereo));
        Surround51Command = new RelayCommand(() => Load(CtaFormats.Surround51));
        Surround71Command = new RelayCommand(() => Load(CtaFormats.Surround71));
    }

    void Load(int bits) { foreach (var i in Items) i.Show((bits >> i.Bit & 1) != 0); Validate(); }

    public IReadOnlyList<BitItem> Items { get; }
    public ICommand StereoCommand { get; }
    public ICommand Surround51Command { get; }
    public ICommand Surround71Command { get; }
    public override CtaDataBlock Build() => CtaFormats.BuildSpeakers(Items.Where(i => i.IsOn).Aggregate(0, (a, i) => a | 1 << i.Bit));
}

public sealed class ColorimetryEditor : DataBlockEditor
{
    public ColorimetryEditor(CtaDataBlock? existing) : base("Colorimetry", 480)
    {
        var bits = existing is null ? 0x80 | 0x40 : CtaFormats.ReadColorimetry(existing); // new: BT.2020 RGB and YCC, as CRU suggests for HDR
        Items = CtaFormats.ColorimetryBits.Where(b => b.Bit < 8 || b.Bit > 11).Select(b => new BitItem(b.Name, b.Bit, Changed)).ToList();
        Metadata = CtaFormats.ColorimetryBits.Where(b => b.Bit is >= 8 and <= 11).Select(b => new BitItem(b.Name, b.Bit, Changed)).ToList();
        foreach (var i in Items.Concat(Metadata)) i.Show((bits >> i.Bit & 1) != 0);
    }

    public IReadOnlyList<BitItem> Items { get; }
    public IReadOnlyList<BitItem> Metadata { get; }
    public override CtaDataBlock Build() => CtaFormats.BuildColorimetry(Items.Concat(Metadata).Where(i => i.IsOn).Aggregate(0, (a, i) => a | 1 << i.Bit));
}

public sealed class HdrEditor : DataBlockEditor
{
    string _max, _avg, _min;
    bool _type1;

    public HdrEditor(CtaDataBlock? existing) : base("HDR Static Metadata", 480)
    {
        var h = existing is null ? new CtaFormats.HdrStatic(0b0101, true, 0, 0, 0) : CtaFormats.ReadHdr(existing);
        Eotfs = CtaFormats.Eotfs.Select((n, i) => new BitItem(n, i, Changed)).ToList();
        foreach (var e in Eotfs) e.Show((h.Eotfs >> e.Bit & 1) != 0);
        _type1 = h.Type1;
        (_max, _avg, _min) = (Code(h.MaxCode), Code(h.AvgCode), Code(h.MinCode));
    }

    static string Code(int c) => c == 0 ? "" : c.ToString();
    public IReadOnlyList<BitItem> Eotfs { get; }
    public bool Type1 { get => _type1; set { Set(ref _type1, value); Validate(); } }
    public string MaxText { get => _max; set { _max = value ?? ""; Lum(); } }
    public string AvgText { get => _avg; set { _avg = value ?? ""; Lum(); } }
    public string MinText { get => _min; set { _min = value ?? ""; Lum(); } }
    void Lum() { Validate(); Raise(nameof(MaxNits)); Raise(nameof(AvgNits)); Raise(nameof(MinNits)); }

    static string Nits(double v) => $"{v.ToString(v < 10 ? "0.####" : "0.#", CultureInfo.InvariantCulture)} cd/m²";
    public string MaxNits => IntOrBlank(_max, out var c) && c is > 0 and <= 255 ? Nits(CtaFormats.HdrStatic.Luminance(c)) : "";
    public string AvgNits => IntOrBlank(_avg, out var c) && c is > 0 and <= 255 ? Nits(CtaFormats.HdrStatic.Luminance(c)) : "";
    public string MinNits => IntOrBlank(_max, out var m) && IntOrBlank(_min, out var c) && c is > 0 and <= 255 && m is > 0 and <= 255 ? Nits(new CtaFormats.HdrStatic(0, false, m, 0, c).MinLuminance) : "";

    public override CtaDataBlock Build()
    {
        if (!IntOrBlank(_max, out var max) || !IntOrBlank(_avg, out var avg) || !IntOrBlank(_min, out var min) || new[] { max, avg, min }.Any(c => c is < 0 or > 255))
            throw new ArgumentException("Luminance values are codes from 1 to 255 (blank = not given).");
        return CtaFormats.BuildHdr(new CtaFormats.HdrStatic(Eotfs.Where(e => e.IsOn).Aggregate(0, (a, e) => a | 1 << e.Bit), _type1, max, avg, min));
    }
}

public sealed class VideoCapabilityEditor : DataBlockEditor
{
    bool _qy, _qs;
    int _pt, _it, _ce;

    public VideoCapabilityEditor(CtaDataBlock? existing) : base("Video Capability", 440)
    {
        var c = existing is null ? new CtaFormats.VideoCapability(true, true, 0, 0, 0) : CtaFormats.ReadVideoCapability(existing);
        (_qy, _qs, _pt, _it, _ce) = (c.Qy, c.Qs, c.Pt, c.It, c.Ce);
    }

    public IReadOnlyList<Choice> ScanModes { get; } = CtaFormats.ScanModes.Select((n, i) => new Choice(i, n)).ToList();
    public IReadOnlyList<Choice> NativeModes { get; } = CtaFormats.ScanModes.Select((n, i) => new Choice(i, i == 0 ? "Use TV/PC information" : n)).ToList();
    public bool Qy { get => _qy; set => Set(ref _qy, value); }
    public bool Qs { get => _qs; set => Set(ref _qs, value); }
    public int Pt { get => _pt; set => Set(ref _pt, value); }
    public int It { get => _it; set => Set(ref _it, value); }
    public int Ce { get => _ce; set => Set(ref _ce, value); }
    public override CtaDataBlock Build() => CtaFormats.BuildVideoCapability(new(_qy, _qs, _pt, _it, _ce));
}

public sealed class FreeSyncEditor : DataBlockEditor
{
    readonly CtaDataBlock? _existing;
    string _min, _max;

    public FreeSyncEditor(CtaDataBlock? existing, int nativeHz) : base("FreeSync Range", 380)
    {
        _existing = existing;
        var r = existing is null ? null : CtaFormats.ReadFreeSync(existing);
        (_min, _max) = ((r?.Min ?? 48).ToString(), (r?.Max ?? Math.Max(48, nativeHz)).ToString());
    }

    public string MinText { get => _min; set { _min = value ?? ""; Validate(); } }
    public string MaxText { get => _max; set { _max = value ?? ""; Validate(); } }
    public override CtaDataBlock Build()
    {
        if (!Int(_min, out var min) || !Int(_max, out var max)) throw new ArgumentException("Enter whole numbers.");
        return CtaFormats.BuildFreeSync(_existing, min, max);
    }
}

public sealed class HdmiEditor : DataBlockEditor
{
    readonly CtaDataBlock? _existing;
    string _address, _tmds;
    bool _ai, _dc30, _dc36, _dc48, _y444;

    public HdmiEditor(CtaDataBlock? existing) : base("HDMI Support", 440)
    {
        _existing = existing;
        var h = existing is null ? new CtaFormats.HdmiSupport("1.0.0.0", false, false, false, false, false, 0) : CtaFormats.ReadHdmi(existing);
        (_address, _tmds, _ai, _dc30, _dc36, _dc48, _y444) = (h.PhysicalAddress, h.MaxTmdsMhz == 0 ? "" : h.MaxTmdsMhz.ToString(), h.Ai, h.Dc30, h.Dc36, h.Dc48, h.DcY444);
    }

    public string Address { get => _address; set { _address = value ?? ""; Validate(); } }
    public string TmdsText { get => _tmds; set { _tmds = value ?? ""; Validate(); } }
    public bool Ai { get => _ai; set => Set(ref _ai, value); }
    public bool Dc30 { get => _dc30; set => Set(ref _dc30, value); }
    public bool Dc36 { get => _dc36; set => Set(ref _dc36, value); }
    public bool Dc48 { get => _dc48; set => Set(ref _dc48, value); }
    public bool Y444 { get => _y444; set => Set(ref _y444, value); }
    public override CtaDataBlock Build()
    {
        if (!IntOrBlank(_tmds, out var tmds)) throw new ArgumentException("Enter the max TMDS clock in MHz (or leave it blank).");
        return CtaFormats.BuildHdmi(_existing, new(_address.Trim(), _ai, _dc48, _dc36, _dc30, _y444, tmds));
    }
}

public sealed class HdmiForumEditor : DataBlockEditor
{
    readonly CtaDataBlock? _existing;
    string _tmds, _vrrMin, _vrrMax;
    bool _scdc, _scramble, _dc30, _dc36, _dc48;

    public HdmiForumEditor(CtaDataBlock? existing) : base("HDMI 2.x Support", 460)
    {
        _existing = existing;
        var h = existing is null ? new CtaFormats.HdmiForum(600, true, false, false, false, false, 0, 0) : CtaFormats.ReadHdmiForum(existing);
        (_tmds, _scdc, _scramble, _dc30, _dc36, _dc48) = (h.MaxTmdsMhz == 0 ? "" : h.MaxTmdsMhz.ToString(), h.Scdc, h.Scramble340, h.Dc420_30, h.Dc420_36, h.Dc420_48);
        (_vrrMin, _vrrMax) = (h.VrrMin == 0 ? "" : h.VrrMin.ToString(), h.VrrMax == 0 ? "" : h.VrrMax.ToString());
    }

    public string TmdsText { get => _tmds; set { _tmds = value ?? ""; Validate(); } }
    public string VrrMinText { get => _vrrMin; set { _vrrMin = value ?? ""; Validate(); } }
    public string VrrMaxText { get => _vrrMax; set { _vrrMax = value ?? ""; Validate(); } }
    public bool Scdc { get => _scdc; set => Set(ref _scdc, value); }
    public bool Scramble { get => _scramble; set => Set(ref _scramble, value); }
    public bool Dc30 { get => _dc30; set => Set(ref _dc30, value); }
    public bool Dc36 { get => _dc36; set => Set(ref _dc36, value); }
    public bool Dc48 { get => _dc48; set => Set(ref _dc48, value); }
    public override CtaDataBlock Build()
    {
        if (!IntOrBlank(_tmds, out var tmds) || !IntOrBlank(_vrrMin, out var min) || !IntOrBlank(_vrrMax, out var max)) throw new ArgumentException("Enter whole numbers (or leave them blank).");
        return CtaFormats.BuildHdmiForum(_existing, new(tmds, _scdc, _scramble, _dc30, _dc36, _dc48, min, max));
    }
}

/// <summary>Any data block as hex: the payload of an existing one (its tag stays), or a whole new block, header byte included.</summary>
public sealed class HexBlockEditor : DataBlockEditor
{
    readonly CtaDataBlock? _existing;
    string _text;

    public HexBlockEditor(CtaDataBlock? existing) : base(existing?.Name ?? "Other Data Block", 520)
    {
        _existing = existing;
        _text = existing is null ? "" : CtaFormats.Hex(existing.Raw.AsSpan(1));
    }

    public string Hint => _existing is null
        ? "Whole block in hex, header byte first (tag in the top 3 bits, length in the low 5), e.g. E3 05 C0 00."
        : $"Bytes after the header (tag {_existing.Tag}), in hex. The header's length follows the bytes typed.";
    public string Text { get => _text; set { _text = value ?? ""; Validate(); } }

    public override CtaDataBlock Build()
    {
        var bytes = CtaFormats.ParseHex(_text) ?? throw new FormatException("Enter bytes in hex, like 0A 1B 2C.");
        if (_existing is not null) return CtaDataBlock.Create(_existing.Tag, bytes);
        if (bytes.Length == 0 || (bytes[0] & 0x1F) != bytes.Length - 1) throw new FormatException("The header's length (low 5 bits of the first byte) must match the bytes after it.");
        if (bytes[0] >> 5 is 0) throw new FormatException("Tag 0 is reserved.");
        return new CtaDataBlock(bytes);
    }
}

// =====================================================================
// Extension block (CTA-861)
// =====================================================================

public sealed record TimingRow(int Index, string Text);
public sealed record DataBlockRow(int Index, string Name, string Size, string Summary);

/// <summary>CRU's Extension Block window for a CTA-861 block: its flags, detailed resolutions and data blocks, edited on a copy.</summary>
public sealed class CtaEditorViewModel : EdidEditorBase
{
    readonly CtaBlock _cta;
    readonly DetailedTiming? _native;
    TimingRow? _timing;
    DataBlockRow? _block;
    string? _message;

    public CtaEditorViewModel(byte[] block, DetailedTiming? native) : base("Extension Block", 660)
    {
        _cta = new CtaBlock(block);
        _native = native;
        AddTimingCommand = new RelayCommand(AddTiming, () => _cta.FreeBytes >= 18);
        EditTimingCommand = new RelayCommand(EditTiming, () => _timing is not null);
        DeleteTimingCommand = new RelayCommand(() => Do(() => _cta.RemoveTiming(_timing!.Index)), () => _timing is not null);
        ClearTimingsCommand = new RelayCommand(() => Do(_cta.ClearTimings), () => _cta.Timings.Count > 0);
        TimingUpCommand = new RelayCommand(() => MoveTiming(-1), () => _timing is { Index: > 0 });
        TimingDownCommand = new RelayCommand(() => MoveTiming(1), () => _timing is { } t && t.Index < _cta.Timings.Count - 1);
        AddBlockCommand = new RelayCommand(AddBlock, () => _cta.SupportsDataBlocks && _cta.FreeBytes > 1);
        EditBlockCommand = new RelayCommand(EditBlock, () => _block is not null);
        DeleteBlockCommand = new RelayCommand(() => Do(() => _cta.RemoveDataBlock(_block!.Index)), () => _block is not null);
        ClearBlocksCommand = new RelayCommand(() => Do(_cta.ClearDataBlocks), () => _cta.DataBlocks.Count > 0);
        BlockUpCommand = new RelayCommand(() => MoveBlock(-1), () => _block is { Index: > 0 });
        BlockDownCommand = new RelayCommand(() => MoveBlock(1), () => _block is { } b && b.Index < _cta.DataBlocks.Count - 1);
        Refresh();
    }

    public byte[] Result => _cta.ToBytes();
    public string Header => $"CTA-861 (revision {_cta.Revision})";
    public string FreeText => $"{_cta.FreeBytes} bytes left (a detailed resolution takes 18)";
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool SupportsDataBlocks => _cta.SupportsDataBlocks;

    public bool Underscan { get => _cta.Underscan; set { _cta.Underscan = value; Raise(); } }
    public bool BasicAudio { get => _cta.BasicAudio; set { _cta.BasicAudio = value; Raise(); } }
    public bool YCbCr444 { get => _cta.YCbCr444; set { _cta.YCbCr444 = value; Raise(); } }
    public bool YCbCr422 { get => _cta.YCbCr422; set { _cta.YCbCr422 = value; Raise(); } }

    public IReadOnlyList<TimingRow> Timings { get; private set; } = Array.Empty<TimingRow>();
    public TimingRow? SelectedTiming { get => _timing; set { Set(ref _timing, value); CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<DataBlockRow> Blocks { get; private set; } = Array.Empty<DataBlockRow>();
    public DataBlockRow? SelectedBlock { get => _block; set { Set(ref _block, value); CommandManager.InvalidateRequerySuggested(); } }

    public ICommand AddTimingCommand { get; }
    public ICommand EditTimingCommand { get; }
    public ICommand DeleteTimingCommand { get; }
    public ICommand ClearTimingsCommand { get; }
    public ICommand TimingUpCommand { get; }
    public ICommand TimingDownCommand { get; }
    public ICommand AddBlockCommand { get; }
    public ICommand EditBlockCommand { get; }
    public ICommand DeleteBlockCommand { get; }
    public ICommand ClearBlocksCommand { get; }
    public ICommand BlockUpCommand { get; }
    public ICommand BlockDownCommand { get; }

    void Refresh(int? timing = null, int? block = null)
    {
        Timings = _cta.Timings.Select((t, i) => new TimingRow(i, DisplayPageViewModel.Describe(t))).ToList();
        Blocks = _cta.DataBlocks.Select((b, i) => new DataBlockRow(i, b.Name, $"{b.Raw.Length} bytes", b.Summary)).ToList();
        _timing = timing is { } t && t < Timings.Count ? Timings[t] : null;
        _block = block is { } b2 && b2 < Blocks.Count ? Blocks[b2] : null;
        foreach (var n in new[] { nameof(Timings), nameof(Blocks), nameof(SelectedTiming), nameof(SelectedBlock), nameof(FreeText) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    void Do(Action edit, int? timing = null, int? block = null)
    {
        try { edit(); Message = null; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Message = ex.Message; }
        Refresh(timing, block);
    }

    void MoveTiming(int delta) { var i = _timing!.Index; Do(() => _cta.MoveTiming(i, delta), timing: i + delta); }
    void MoveBlock(int delta) { var i = _block!.Index; Do(() => _cta.MoveDataBlock(i, delta), block: i + delta); }

    void AddTiming()
    {
        var start = DisplayPageViewModel.NewTiming(_native);
        if (TimingDialog.Open(new TimingEditorViewModel("Add resolution", "Add", start, TimingMode.LcdStandard, _native, false)) is { } r)
            Do(() => _cta.AddTiming(r.Timing), timing: _cta.Timings.Count);
    }

    void EditTiming()
    {
        var i = _timing!.Index;
        if (TimingDialog.Open(new TimingEditorViewModel("Edit resolution", "Save", _cta.Timings[i], TimingMode.Manual, _native, false)) is { } r)
            Do(() => _cta.ReplaceTiming(i, r.Timing), timing: i);
    }

    static readonly (CtaKind Kind, string Label)[] AddTypes =
    {
        (CtaKind.Video, "TV resolutions"), (CtaKind.Audio, "Audio formats"), (CtaKind.Speaker, "Speaker setup"), (CtaKind.Hdmi, "HDMI support"),
        (CtaKind.HdmiForum, "HDMI 2.x support"), (CtaKind.FreeSync, "FreeSync range"), (CtaKind.Colorimetry, "Colorimetry"),
        (CtaKind.VideoCapability, "Video capability"), (CtaKind.HdrStatic, "HDR static metadata"), (CtaKind.Other, "Other (hex)"),
    };

    DataBlockEditor Editor(CtaKind kind, CtaDataBlock? existing) => kind switch
    {
        CtaKind.Video => new VideoBlockEditor(existing),
        CtaKind.Audio => new AudioBlockEditor(existing),
        CtaKind.Speaker => new SpeakerEditor(existing),
        CtaKind.Hdmi => new HdmiEditor(existing),
        CtaKind.HdmiForum => new HdmiForumEditor(existing),
        CtaKind.FreeSync => new FreeSyncEditor(existing, (int)Math.Round(_native?.RefreshHz ?? 60)),
        CtaKind.Colorimetry => new ColorimetryEditor(existing),
        CtaKind.VideoCapability => new VideoCapabilityEditor(existing),
        CtaKind.HdrStatic => new HdrEditor(existing),
        _ => new HexBlockEditor(existing),
    };

    void AddBlock()
    {
        var items = AddTypes.Select(t => new ChoiceItem("Type", t.Label, null, null, t.Kind)).ToList();
        items[0].IsChecked = true;
        if (!ChoiceDialog.Choose("Add Data Block", "", items, "Next", single: true)) return;
        var editor = Editor((CtaKind)items.First(i => i.IsChecked).Tag, null);
        if (EditorDialog.Open(editor)) Do(() => _cta.AddDataBlock(editor.Build()), block: _cta.DataBlocks.Count);
    }

    void EditBlock()
    {
        var i = _block!.Index;
        var existing = _cta.DataBlocks[i];
        var editor = Editor(existing.Kind, existing);
        if (EditorDialog.Open(editor)) Do(() => _cta.ReplaceDataBlock(i, editor.Build()), block: i);
    }
}
