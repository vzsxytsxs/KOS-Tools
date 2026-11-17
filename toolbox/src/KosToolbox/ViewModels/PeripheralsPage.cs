using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KosToolbox.Core.Peripherals;
using KosToolbox.Core.Peripherals.Hid;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>A collection of the selected device, as the HID collections table shows it (usage page:usage, and its name when known).</summary>
public sealed record HidCollectionRow(string Location, string Usage, string? UsageName, string Input, string Output, string Feature);

/// <summary>A device of the list; selected, its Info shows beside the list.</summary>
public sealed class PeripheralRowViewModel : ObservableObject
{
    bool _selected;

    public PeripheralRowViewModel(HidDevice device)
    {
        Device = device;
        Name = device.Product ?? Txt.PeripheralUnnamed(device.VendorId, device.ProductId);
        var ids = Txt.HidIds(device.VendorId, device.ProductId);
        Sub = device.Bluetooth ? $"{Txt.Bluetooth} · {ids}" : ids;
        var rows = new List<InfoRow>
        {
            new(Txt.PeripheralVidPid, ids), new(Txt.PeripheralProduct, device.Product ?? "—"),
            new(Txt.PeripheralKindLabel, KindText), new(Txt.PeripheralRevision, Txt.HidRevision(device.Version)),
        };
        if (device.Bluetooth) rows.Add(new(Txt.PeripheralConnection, Txt.Bluetooth)); // only when the bus proves it: a USB receiver isn't "wired"
        Info = new InfoSection(Txt.PeripheralInfo, columns: 1) { Rows = rows };
        static string Length(int n) => n == 0 ? "—" : n.ToString();
        Collections = device.Collections.Select(c => new HidCollectionRow(Txt.HidLocation(c.Interface, c.Collection), $"{c.UsagePage:X4}:{c.Usage:X4}",
            Txt.HidUsageName(c.UsagePage, c.Usage), Length(c.InputLength), Length(c.OutputLength), Length(c.FeatureLength))).ToList();
        CopyReportCommand = new RelayCommand(CopyReport);
    }

    public HidDevice Device { get; }
    public string Name { get; }
    /// <summary>VID:PID, after "Bluetooth" when it is connected that way.</summary>
    public string Sub { get; }
    public string KindText => Txt.PeripheralKindName(Device.Kind);
    public string Glyph => Device.Kind switch
    {
        PeripheralKind.Mouse => "", PeripheralKind.Keyboard => "", PeripheralKind.Headset => "",
        PeripheralKind.Controller => "", _ => "", // mouse, keyboard, headphones, gamepad, a USB plug
    };
    public InfoSection Info { get; }
    public IReadOnlyList<HidCollectionRow> Collections { get; }
    public ICommand CopyReportCommand { get; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    void CopyReport()
    {
        try
        {
            Clipboard.SetText(Txt.PeripheralReport(Device));
            Toasts.Show(Txt.DeviceReportCopied, ToastKind.Success);
        }
        catch (ExternalException) { Toasts.Show(Txt.ClipboardBusy, ToastKind.Error); }
    }
}

/// <summary>
/// Peripherals: the HID devices connected now, one row per physical device, and the selected one's Info and collections. There are no
/// device drivers yet, so every device is "Not supported" and nothing is sent to any: enumeration opens collections with access 0 to
/// read what Windows reports. Read on every visit and, while the page is shown, 2.5 s after Windows reports a device change.
/// </summary>
public sealed class PeripheralsPageViewModel : PageViewModel
{
    const int WmDeviceChange = 0x0219, DbtDevNodesChanged = 0x0007;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(2.5) };
    IReadOnlyList<PeripheralRowViewModel> _devices = Array.Empty<PeripheralRowViewModel>();
    PeripheralRowViewModel? _selected;
    HwndSource? _window;
    string? _loadError;
    bool _loading, _loaded;

    /// <summary>Where the devices come from; the ui-shot probe swaps in a sample list.</summary>
    public static Func<List<HidDevice>> Source { get; set; } = HidDevices.List;

    public PeripheralsPageViewModel() : base("peripherals", Txt.PagePeripherals, "", Txt.PagePeripheralsSubtitle)
    {
        SelectCommand = new RelayCommand(p => { if (p is PeripheralRowViewModel row) Selected = row; });
        // A read still running when the timer fires: try again later rather than miss the change.
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_loading) _debounce.Start();
            else MainViewModel.Fire(RefreshAsync());
        };
    }

    /// <summary>The list and the selected device scroll on their own, side by side.</summary>
    public override bool ScrollsItself => true;
    public ICommand SelectCommand { get; }
    public IReadOnlyList<PeripheralRowViewModel> Devices { get => _devices; private set { Set(ref _devices, value); Raise(nameof(HasDevices)); } }
    public bool HasDevices => _devices.Count > 0;
    public bool IsEmpty => _loaded && _devices.Count == 0 && _loadError is null;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }

    public PeripheralRowViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var d in _devices) d.IsSelected = ReferenceEquals(d, value);
        }
    }

    public override async Task RefreshAsync()
    {
        if (_loading) return;
        IsLoading = true;
        var keep = _selected?.Device.Key;
        try
        {
            var devices = await Task.Run(Source);
            Devices = devices.Select(d => new PeripheralRowViewModel(d))
                .OrderBy(r => r.Device.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            LoadError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading HID devices failed", ex);
            Devices = Array.Empty<PeripheralRowViewModel>();
            LoadError = Txt.PeripheralsReadFailed(ex.Message);
        }
        finally
        {
            _loaded = true;
            IsLoading = false;
            Raise(nameof(IsEmpty));
        }
        Selected = _devices.FirstOrDefault(r => r.Device.Key == keep) ?? _devices.FirstOrDefault();
    }

    /// <summary>While shown, device changes re-read the list (DBT_DEVNODES_CHANGED reaches top-level windows without registering).</summary>
    public override void OnShown(bool shown)
    {
        if (shown && _window is null && Application.Current?.MainWindow is { } w && PresentationSource.FromVisual(w) is HwndSource source)
        {
            _window = source;
            source.AddHook(OnMessage);
        }
        else if (!shown)
        {
            _window?.RemoveHook(OnMessage);
            _window = null;
            _debounce.Stop();
        }
    }

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && wParam == DbtDevNodesChanged)
        {
            _debounce.Stop(); // a burst of changes (a receiver's interfaces arriving one by one) gives one read
            _debounce.Start();
        }
        return IntPtr.Zero;
    }
}
