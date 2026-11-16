using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Network;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>An advanced property with a pending change (nothing is written before Apply) and the recommended value.</summary>
public sealed class NetPropertyViewModel : ObservableObject
{
    const string NotAdded = "\u0000"; // "Not added" option for keywords the driver does not list (deletes the value)
    readonly Action _changed;
    OptionItem? _option;
    string _text;
    string? _error, _pendingValue;
    bool _pending;
    public bool IsPending => _pending;

    public NetPropertyViewModel(NetProperty property, string? recommended, Action changed)
    {
        Property = property;
        Recommended = recommended;
        _changed = changed;
        var options = property.Options.Select(o => new OptionItem(o.Key, o.Value)).ToList();
        if (property.Unlisted && property.IsEnum) options.Insert(0, new OptionItem(NotAdded, "Not added"));
        Options = options;
        _option = CurrentOption();
        _text = property.Effective;
        UseDefaultCommand = new RelayCommand(() => Stage(null), () => property.Current is not null && !(_pending && _pendingValue is null));
        DiscardCommand = new RelayCommand(Discard, () => _pending || _error is not null);
        UseRecommendedCommand = new RelayCommand(() => SetValue(Recommended!), () => Recommended is not null && !IsRecommended);
    }

    public NetProperty Property { get; }
    public string Name => Property.Name;
    public string Keyword => Property.Keyword;
    public bool IsEnum => Property.IsEnum;
    public bool IsText => !Property.IsEnum;
    public bool IsDefault => Property.Current is null;
    /// <summary>The driver does not list this keyword: the value goes straight to the registry and may be ignored.</summary>
    public bool IsUnlisted => Property.Unlisted;
    public string UnlistedTip => $"{Keyword} isn't listed by this driver. The value is written to the registry; the driver only uses it if it supports it.";
    public IReadOnlyList<OptionItem> Options { get; }
    public string? Hint => Property.IsEnum ? null : Property.Min is not null || Property.Max is not null ? Txt.NicRange(Property.Min, Property.Max, Property.Step) : null;
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool IsModified => _pending;
    public ICommand UseDefaultCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand UseRecommendedCommand { get; }

    /// <summary>Value recommended by the KOS Network Tweaker (registry), if any.</summary>
    public string? Recommended { get; }
    public bool HasRecommended => Recommended is not null;
    public string RecommendedLabel => Recommended is null ? "" : "★ " + Describe(Recommended);
    public bool IsRecommended => Recommended is not null && (_pending ? _pendingValue : Property.Current ?? (Property.Unlisted ? null : Property.Default)) == Recommended;

    public string Describe(string? value) => value is null ? Txt.ResetToDefault : Options.FirstOrDefault(o => o.Key == value)?.Label ?? value;
    public NetPropertyWrite Write => new() { Keyword = Keyword, Value = _pendingValue };

    OptionItem? CurrentOption() => Property.Unlisted && Property.Current is null
        ? Options.FirstOrDefault(o => o.Key == NotAdded)
        : Options.FirstOrDefault(o => o.Key == Property.Effective);

    public OptionItem? SelectedOption
    {
        get => _option;
        set { if (value is not null && Set(ref _option, value)) Stage(value.Key == NotAdded ? null : value.Key); }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            var t = _text.Trim();
            if (t.Length == 0 && Property.Unlisted) { Error = null; Stage(null); return; } // empty = do not add
            Error = NetAdapters.Problem(Property, t);
            if (Error is null) Stage(t);
            else Unstage();
        }
    }

    /// <summary>Marks a value (recommended ones).</summary>
    public void SetValue(string value)
    {
        if (IsEnum) { _option = Options.FirstOrDefault(o => o.Key == value) ?? _option; Raise(nameof(SelectedOption)); }
        else { _text = value; Error = null; Raise(nameof(Text)); }
        Stage(value);
    }

    void Stage(string? value)
    {
        // Choosing the value in effect (stored or driver default) is not a change.
        if (value is not null && value == Property.Current || value is null && Property.Current is null) { Unstage(); return; }
        if (value is not null && Property.Current is null && !Property.Unlisted && value == Property.Default) { Unstage(); return; }
        _pending = true;
        _pendingValue = value;
        if (value is null && !Property.Unlisted) { _option = Options.FirstOrDefault(o => o.Key == Property.Default); _text = Property.Default ?? ""; Raise(nameof(SelectedOption)); Raise(nameof(Text)); }
        RaiseState();
    }

    void Unstage()
    {
        if (!_pending) { Raise(nameof(IsRecommended)); return; }
        _pending = false;
        _pendingValue = null;
        RaiseState();
    }

    void RaiseState()
    {
        Raise(nameof(IsModified));
        Raise(nameof(IsRecommended));
        _changed();
    }

    public void Discard()
    {
        _option = CurrentOption();
        _text = Property.Effective;
        Error = null;
        Raise(nameof(SelectedOption));
        Raise(nameof(Text));
        Unstage();
    }
}

/// <summary>Global TCP/UDP parameter (netsh) with a pending change.</summary>
public sealed class NetGlobalViewModel : ObservableObject
{
    readonly Action _changed;
    OptionItem? _option;
    string _text;
    string? _pending, _error;

    public NetGlobalViewModel(NetGlobal global, string? recommended, Action changed)
    {
        Global = global;
        Recommended = recommended;
        _changed = changed;
        Options = global.Options.Select(o => new OptionItem(o, Pretty(o))).ToList();
        _option = Options.FirstOrDefault(o => o.Key == global.Value);
        _text = global.Value ?? "";
    }

    static string Pretty(string v) => v.Length == 0 ? v : char.ToUpperInvariant(v[0]) + v[1..];

    public NetGlobal Global { get; }
    public string Name => Global.Label;
    public string Key => Global.Key;
    public bool IsEnum => !Global.IsNumber;
    public bool IsText => Global.IsNumber;
    public IReadOnlyList<OptionItem> Options { get; }
    public string? Recommended { get; }
    public bool HasRecommended => Recommended is not null;
    public string RecommendedLabel => Recommended is null ? "" : "★ " + Pretty(Recommended);
    public bool IsRecommended => Recommended is not null && (_pending ?? Global.Value) == Recommended;
    public bool IsModified => _pending is not null;
    public string? PendingValue => _pending;
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public ICommand UseRecommendedCommand => new RelayCommand(() => SetValue(Recommended!), () => Recommended is not null && !IsRecommended);

    public OptionItem? SelectedOption
    {
        get => _option;
        set { if (value is not null && Set(ref _option, value)) Stage(value.Key); }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            Error = NetGlobals.Problem(Key, _text.Trim());
            Stage(Error is null ? _text.Trim() : Global.Value);
        }
    }

    public void SetValue(string value)
    {
        if (IsEnum) { _option = Options.FirstOrDefault(o => o.Key == value) ?? _option; Raise(nameof(SelectedOption)); }
        else { _text = value; Error = null; Raise(nameof(Text)); }
        Stage(value);
    }

    void Stage(string? value)
    {
        _pending = value == Global.Value ? null : value;
        Raise(nameof(IsModified));
        Raise(nameof(IsRecommended));
        _changed();
    }

    public void Discard()
    {
        _option = Options.FirstOrDefault(o => o.Key == Global.Value);
        _text = Global.Value ?? "";
        Error = null;
        Raise(nameof(SelectedOption));
        Raise(nameof(Text));
        Stage(null);
    }
}

/// <summary>Adapter binding (protocol/service) with a pending change.</summary>
public sealed class NetBindingViewModel(NetBinding binding, bool? recommended, Action changed) : ObservableObject
{
    bool? _pending;
    public NetBinding Binding { get; } = binding;
    public string Name => Binding.DisplayName;
    public string ComponentId => Binding.ComponentId;
    public bool? Recommended { get; } = recommended;
    public bool HasRecommended => Recommended is not null;
    public string RecommendedLabel => Recommended is null ? "" : Recommended.Value ? "★ On" : "★ Off";
    public bool IsModified => _pending is not null;
    public bool? PendingValue => _pending;
    public bool IsEnabled
    {
        get => _pending ?? Binding.Enabled;
        set
        {
            _pending = value == Binding.Enabled ? null : value;
            Raise();
            Raise(nameof(IsModified));
            changed();
        }
    }
    public void SetValue(bool value) => IsEnabled = value;
    public void Discard() { _pending = null; Raise(nameof(IsEnabled)); Raise(nameof(IsModified)); changed(); }
}

/// <summary>
/// Network: one tab per adapter (NIC 1, NIC 2…), with the driver's advanced properties (including keywords it
/// does not list), interrupt moderation, RSS, global TCP/UDP parameters and bindings. Next to each value, the
/// value recommended by the KOS Network Tweaker. Nothing is written before Apply; the adapter restarts for changes to take effect.
/// </summary>
public sealed class NetworkPageViewModel : PageViewModel
{
    List<NetAdapter> _adapters = new();
    NetAdapter? _adapter;
    NetAdapterInfo? _info;
    List<NetPropertyViewModel> _all = new();
    List<NetGlobalViewModel> _globals = new();
    List<NetBindingViewModel> _bindings = new();
    IReadOnlyList<GpuTab> _tabs = Array.Empty<GpuTab>();
    string? _message, _loadError;
    bool _isError, _busy, _showVirtual, _showUnlisted, _advancedOpen = true, _interruptOpen = true, _rssOpen = true, _globalsOpen = true, _bindingsOpen = true;

    public NetworkPageViewModel() : base("network", Txt.PageNetwork, "", Txt.PageNetworkSubtitle)
    {
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !_busy && PendingCount > 0 && !HasErrors);
        DiscardCommand = new RelayCommand(Discard, () => !_busy && (PendingCount > 0 || HasErrors));
        RestoreCommand = new AsyncCommand(RestoreAsync, () => !_busy && _adapter is { } a && NetAdapters.Original(a.InstanceId).Count > 0);
        RecommendedCommand = new RelayCommand(StageRecommended, () => !_busy && _adapter is not null);
        RestartCommand = new AsyncCommand(RestartAsync, () => !_busy && _adapter is not null);
        SelectTabCommand = new RelayCommand(p => SelectAdapter((string)p!));
        ToggleCommand = new RelayCommand(p =>
        {
            switch ((string)p!)
            {
                case "advanced": AdvancedOpen = !AdvancedOpen; break;
                case "interrupt": InterruptOpen = !InterruptOpen; break;
                case "rss": RssOpen = !RssOpen; break;
                case "globals": GlobalsOpen = !GlobalsOpen; break;
                case "bindings": BindingsOpen = !BindingsOpen; break;
                case "dns": DnsOpen = !DnsOpen; break;
            }
        });
        DnsChoices = NetDns.Providers.Select(p => new OptionItem(p.Key, p.Name)).ToArray();
        SelectDnsCommand = new AsyncCommand(p => ApplyDnsAsync((string)p!, _dns is { } d && (d.V4Encrypted || d.V6Encrypted)), _ => !_busy && _dns is not null);
        ToggleDnsEncryptionCommand = new AsyncCommand(() => ApplyDnsAsync(NetDns.Match(_dns!)!.Key, !DnsEncrypted), () => !_busy && _dns is { } d && NetDns.Match(d) is { Key: not NetDns.Automatic });
    }

    // ---------- DNS ----------
    DnsState? _dns;
    bool _dnsOpen = true;
    public IReadOnlyList<OptionItem> DnsChoices { get; }
    public ICommand SelectDnsCommand { get; }
    public ICommand ToggleDnsEncryptionCommand { get; }
    public bool DnsOpen { get => _dnsOpen; set => Set(ref _dnsOpen, value); }
    public bool HasDns => _dns is not null;
    public bool DnsEncrypted => _dns is { } d && (d.V4Encrypted || d.V6Encrypted);
    public string DnsStatus => _dns is not { } d ? "" :
        (NetDns.Match(d) is null ? $"Other servers set on the adapter: {string.Join(", ", d.V4.Concat(d.V6))}. " : "")
        + (d.InUse.Count > 0 ? $"In use: {string.Join(", ", d.InUse)}" : "No DNS server in use")
        + (d.V4Encrypted || d.V6Encrypted ? $" · encrypted (DNS over HTTPS{(d.V6.Count > 0 && !d.V6Encrypted ? ", IPv4 only" : "")})" : "");

    async Task LoadDnsAsync(NetAdapter a)
    {
        DnsState? state = null;
        if (a.InterfaceId is { } id)
            try { state = await Task.Run(() => NetDns.Read(id)); }
            catch (Exception ex) { Log.Warn($"Reading DNS of {a.Name} failed: {ex.Message}"); }
        if (_adapter != a) return;
        _dns = state;
        var key = state is null ? null : NetDns.Match(state)?.Key;
        foreach (var c in DnsChoices) c.IsSelected = c.Key == key;
        foreach (var n in new[] { nameof(HasDns), nameof(DnsEncrypted), nameof(DnsStatus) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    async Task ApplyDnsAsync(string provider, bool encrypted)
    {
        if (_adapter is not { } a) return;
        _busy = true;
        IsError = false;
        Message = Txt.Applying;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await NetDns.ApplyAsync(a, provider, encrypted);
            var p = NetDns.Find(provider);
            Message = $"{a.Name}: DNS set to {p.Name}{(encrypted && p.Key != NetDns.Automatic ? ", encrypted with DNS over HTTPS" : "")}.";
            if (encrypted && p.Key != NetDns.Automatic)
                Toasts.Show("Encrypted DNS has no fallback: if the provider can't be reached over HTTPS, names don't resolve until encryption is turned off.", ToastKind.Warning);
        }
        catch (ElevationCancelledException ex) { Message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error($"Network: setting DNS of {a.Name} failed", ex);
            Message = ex.Message;
            IsError = true;
        }
        finally { _busy = false; }
        await LoadDnsAsync(a);
    }

    public override bool ScrollsItself => false;
    public ICommand ApplyCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RecommendedCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand ToggleCommand { get; }

    public IReadOnlyList<GpuTab> Tabs { get => _tabs; private set => Set(ref _tabs, value); }
    public bool HasAdapters => _adapters.Count > 0;
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public bool ShowVirtual { get => _showVirtual; set { if (Set(ref _showVirtual, value)) MainViewModel.Fire(RefreshAsync()); } }

    /// <summary>
    /// Shows the known keywords that this driver does not list and that do not even exist in the registry (⚠); with this on,
    /// Recommended also adds those.
    /// </summary>
    public bool ShowUnlisted { get => _showUnlisted; set { if (Set(ref _showUnlisted, value)) RaiseAll(); } }

    // ---------- header ----------
    public string AdapterName => _adapter?.Name ?? "";
    public string AdapterStatus => _adapter?.Status ?? "";
    // The chip vendor (from the PCI VEN); the driver provider (e.g. Microsoft, for the Windows driver) goes in the tooltip.
    public string Manufacturer => VendorLabel is "Generic" or "" ? _info?.Manufacturer ?? "" : VendorLabel;
    public string DriverProvider => _info is null ? "" : $"Driver provider: {_info.Manufacturer}";
    public string HardwareId => _info?.HardwareId ?? "";
    public string DriverVersion => _info?.DriverVersion ?? "";
    public string DriverFile => _info?.DriverFile ?? "";
    public string RegistryKey => _info?.RegistryKey ?? "";
    public string VendorLabel => _adapter is null ? "" : NetTweaks.Vendor(_adapter.InstanceId) switch
    {
        "realtek" => "Realtek", "intel" => "Intel", "killer" => "Killer / Atheros", "broadcom" => "Broadcom", "marvell" => "Marvell", "aquantia" => "Aquantia", _ => "Generic",
    };

    // ---------- sections ----------
    public IReadOnlyList<NetPropertyViewModel> AdvancedLeft => Half(Section(NetTweaks.SectionAdvanced), 0);
    public IReadOnlyList<NetPropertyViewModel> AdvancedRight => Half(Section(NetTweaks.SectionAdvanced), 1);
    public IReadOnlyList<NetPropertyViewModel> InterruptRows => Section(NetTweaks.SectionInterrupt);
    public IReadOnlyList<NetPropertyViewModel> RssRows => Section(NetTweaks.SectionRss);
    public IReadOnlyList<NetGlobalViewModel> GlobalsLeft => Half(_globals, 0);
    public IReadOnlyList<NetGlobalViewModel> GlobalsRight => Half(_globals, 1);
    public IReadOnlyList<NetBindingViewModel> BindingsLeft => Half(_bindings, 0);
    public IReadOnlyList<NetBindingViewModel> BindingsRight => Half(_bindings, 1);
    public bool HasInterrupt => InterruptRows.Count > 0;
    public bool HasRss => RssRows.Count > 0;
    public bool HasBindings => _bindings.Count > 0 || _slowLoading;
    public string InterruptNote => NetTweaks.InterruptNote;

    public bool AdvancedOpen { get => _advancedOpen; set => Set(ref _advancedOpen, value); }
    public bool InterruptOpen { get => _interruptOpen; set => Set(ref _interruptOpen, value); }
    public bool RssOpen { get => _rssOpen; set => Set(ref _rssOpen, value); }
    public bool GlobalsOpen { get => _globalsOpen; set => Set(ref _globalsOpen, value); }
    public bool BindingsOpen { get => _bindingsOpen; set => Set(ref _bindingsOpen, value); }

    /// <summary>How many known keywords this driver does not list (all appear, with ⚠).</summary>
    public string UnlistedNote =>
        !_showUnlisted && _all.Count(IsMissing) is var hidden and > 0
            ? $"{hidden} known setting(s) this driver doesn't list are hidden. Turn on \"{Txt.ShowUnlistedSettings}\" (⚠ button) to see them; Recommended then adds them too."
        : _all.Count(p => p.IsUnlisted) is var n and > 0
            ? $"⚠ = not listed by this driver by default ({n}). The value goes straight to the registry; the driver only uses it if it supports it."
        : "";

    // Order: the driver's, then those that exist only in the registry, then those that exist nowhere; each group by name.
    List<NetPropertyViewModel> Section(string section) => _all
        .Where(p => NetTweaks.SectionOf(p.Keyword) == section && (_showUnlisted || !IsMissing(p) || p.IsPending))
        .OrderBy(p => p.IsUnlisted ? p.Property.Current is null ? 2 : 1 : 0).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    static IReadOnlyList<T> Half<T>(IReadOnlyList<T> list, int half)
    {
        var mid = (list.Count + 1) / 2;
        return half == 0 ? list.Take(mid).ToList() : list.Skip(mid).ToList();
    }

    int PendingCount => _all.Count(p => p.IsModified) + _globals.Count(g => g.IsModified) + _bindings.Count(b => b.IsModified);
    bool HasErrors => _all.Any(p => p.HasError) || _globals.Any(g => g.HasError);
    public string PendingText => Txt.NicPending(PendingCount);
    public bool HasPending => PendingCount > 0;

    // ---------- reading ----------
    public override async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            var virt = _showVirtual;
            _adapters = await Task.Run(() => NetAdapters.List(virt));
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading network adapters failed", ex);
            _adapters = new();
            LoadError = Txt.NetworkReadFailed(ex.Message);
        }
        var keep = _adapter?.InstanceId;
        Tabs = _adapters.Select((a, i) => new GpuTab(a.InstanceId, a.Physical ? $"NIC {i + 1}" : $"Virtual {i + 1}", "")).ToList();
        _adapter = _adapters.FirstOrDefault(a => a.InstanceId == keep) ?? _adapters.FirstOrDefault();
        foreach (var t in _tabs) t.IsSelected = t.Key == _adapter?.InstanceId;
        Raise(nameof(HasAdapters));
        await LoadAsync();
    }

    void SelectAdapter(string instanceId)
    {
        if (_adapter?.InstanceId == instanceId || _adapters.FirstOrDefault(a => a.InstanceId == instanceId) is not { } next) return;
        if (HasPending) Toasts.Show(Txt.PendingDiscarded(PendingCount), ToastKind.Info);
        _adapter = next;
        foreach (var t in _tabs) t.IsSelected = t.Key == instanceId;
        MainViewModel.Fire(LoadAsync());
    }

    async Task LoadAsync()
    {
        if (_adapter is not { } a) { _all = new(); _globals = new(); _bindings = new(); RaiseAll(); return; }
        var vendor = NetTweaks.Vendor(a.InstanceId);
        // 1) What comes from the registry (fast) appears right away.
        try
        {
            var (info, props) = await Task.Run(() => (NetAdapters.Info(a), NetAdapters.Properties(a)));
            if (_adapter != a) return;
            _info = info;
            _all = props.Select(p => new NetPropertyViewModel(p, Fit(p, NetTweaks.Recommended(p.Keyword, vendor)), Changed)).ToList();
            await LoadDnsAsync(a);
        }
        catch (Exception ex)
        {
            Log.Error("Reading network adapter failed", ex);
            _all = new();
            LoadError = Txt.NetworkReadFailed(ex.Message);
        }
        _globals = new();
        _bindings = new();
        SlowLoading = true;
        RaiseAll();
        // 2) netsh and PowerShell (slow, in parallel and in a single PowerShell call) arrive later.
        try
        {
            var (globals, bindings) = await NetGlobals.ReadAllAsync(a.InstanceId);
            if (_adapter != a) return;
            _globals = globals.Select(g => new NetGlobalViewModel(g, NetTweaks.GlobalRecommended.GetValueOrDefault(g.Key), Changed)).ToList();
            _bindings = bindings.Select(b => new NetBindingViewModel(b, NetTweaks.BindingRecommended.TryGetValue(b.ComponentId, out var r) ? r : null, Changed)).ToList();
        }
        catch (Exception ex) { Log.Warn($"Reading TCP/UDP settings and bindings failed: {ex.Message}"); }
        finally { if (_adapter == a) SlowLoading = false; }
        RaiseAll();
    }

    bool _slowLoading;
    /// <summary>TCP/UDP and bindings still arriving (the adapter properties already show).</summary>
    public bool SlowLoading { get => _slowLoading; private set => Set(ref _slowLoading, value); }

    /// <summary>
    /// Recommended value this driver accepts: a number out of range becomes the nearest limit (e.g. 1024 buffers on a driver
    /// that goes up to 512); an option the driver does not have is not recommended.
    /// </summary>
    static string? Fit(NetProperty p, string? recommended)
    {
        if (recommended is null) return null;
        if (p.IsEnum) return p.Options.Any(o => o.Key == recommended) ? recommended : null;
        if (!long.TryParse(recommended, out var n)) return recommended;
        if (p.Max is { } max && n > max) n = max;
        if (p.Min is { } min && n < min) n = min;
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Keyword that the driver does not list and that does not exist in the registry.</summary>
    static bool IsMissing(NetPropertyViewModel p) => p.IsUnlisted && p.Property.Current is null;

    void RaiseAll()
    {
        foreach (var n in new[] { nameof(AdapterName), nameof(AdapterStatus), nameof(Manufacturer), nameof(HardwareId), nameof(DriverVersion), nameof(DriverFile),
            nameof(RegistryKey), nameof(VendorLabel), nameof(AdvancedLeft), nameof(AdvancedRight), nameof(InterruptRows), nameof(RssRows), nameof(GlobalsLeft),
            nameof(GlobalsRight), nameof(BindingsLeft), nameof(BindingsRight), nameof(HasInterrupt), nameof(HasRss), nameof(HasBindings), nameof(InterruptNote),
            nameof(UnlistedNote) })
            Raise(n);
        Changed();
    }

    void Changed()
    {
        Raise(nameof(PendingText));
        Raise(nameof(HasPending));
        CommandManager.InvalidateRequerySuggested();
    }

    void Discard()
    {
        foreach (var p in _all) p.Discard();
        foreach (var g in _globals) g.Discard();
        foreach (var b in _bindings) b.Discard();
    }

    /// <summary>Marks the recommended values that are not yet in effect (as pending changes; Apply writes them).</summary>
    void StageRecommended()
    {
        var count = 0;
        // Those this driver does not have are only included with "Show settings this driver doesn't list" on (the user chose to see them).
        foreach (var p in _all.Where(p => p.HasRecommended && !p.IsRecommended && (_showUnlisted || !IsMissing(p) || NetTweaks.AddWhenMissing(p.Keyword))))
        {
            p.SetValue(p.Recommended!);
            count++;
        }
        foreach (var g in _globals.Where(g => g.HasRecommended && !g.IsRecommended)) { g.SetValue(g.Recommended!); count++; }
        foreach (var b in _bindings.Where(b => b.Recommended is { } r && b.IsEnabled != r)) { b.SetValue(b.Recommended!.Value); count++; }
        Report(count == 0 ? "Everything is already at the recommended values." : $"{count} recommended value(s) marked. Review them and press Apply.", false);
    }

    // ---------- writing ----------
    async Task ApplyAsync()
    {
        if (_adapter is not { } a) return;
        var props = _all.Where(p => p.IsModified).ToList();
        var globals = _globals.Where(g => g.IsModified).ToList();
        var bindings = _bindings.Where(b => b.IsModified).ToList();
        await RunAsync(async () =>
        {
            // Each part proceeds even if another fails; at the end, what went in and what did not.
            var errors = new List<string>();
            var reboot = false;
            if (props.Count > 0)
                try { reboot = await NetAdapters.ApplyAsync(a, props.Select(r => r.Write).ToList(), restart: true); }
                catch (ElevationCancelledException) { throw; }
                catch (Exception ex) { errors.Add($"Adapter properties: {ex.Message}"); }
            await Task.Run(() =>
            {
                foreach (var g in globals)
                    try { NetGlobals.Set(g.Key, g.PendingValue!); } catch (Exception ex) { errors.Add(ex.Message); }
                try { errors.AddRange(NetGlobals.SetBindings(a.InstanceId, bindings.Select(b => (b.ComponentId, b.PendingValue!.Value)).ToList()).Select(e => $"Binding {e}")); }
                catch (Exception ex) { errors.Add($"Bindings: {ex.Message}"); }
            });
            var total = props.Count + globals.Count + bindings.Count;
            if (errors.Count > 0)
                throw new InvalidOperationException($"The rest was applied, but not: {string.Join(" · ", errors)}");
            return Txt.NicApplied(total, props.Count > 0, reboot);
        });
    }

    async Task RestoreAsync()
    {
        if (_adapter is not { } a) return;
        var writes = NetAdapters.Original(a.InstanceId)
            .Where(o => _all.FirstOrDefault(p => p.Keyword == o.Key)?.Property.Current != o.Value)
            .Select(o => new NetPropertyWrite { Keyword = o.Key, Value = o.Value }).ToList();
        if (writes.Count == 0) { Report(Txt.AlreadySet(Txt.RestoreOriginal), false); return; }
        await RunAsync(async () => Txt.NicApplied(writes.Count, true, await NetAdapters.ApplyAsync(a, writes, restart: true)));
    }

    async Task RestartAsync()
    {
        if (_adapter is not { } a) return;
        await RunAsync(async () => await NetAdapters.RestartAsync(a) ? "Adapter restarted. Windows asks for a restart to finish." : "Adapter restarted.");
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
            Log.Error("Network change failed", ex);
            message = ex.Message;
            error = true;
        }
        finally { _busy = false; }
        await RefreshAsync(); // the state shown always comes from a fresh read
        Report(message, error);
    }

    void Report(string? message, bool error) { Message = message; IsError = error; }
}
