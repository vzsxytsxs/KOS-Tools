using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using KosToolbox.Core.Drivers;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Setup;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>A checkbox: an NVIDIA component, a post-install tweak or a DDU option.</summary>
public sealed class CheckItem(string key, string name, string? toolTip, bool isChecked, bool isEnabled = true) : ObservableObject
{
    bool _checked = isChecked;
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string? ToolTip { get; } = toolTip;
    public bool IsEnabled { get; } = isEnabled;
    public bool IsChecked { get => _checked; set { if (IsEnabled) Set(ref _checked, value); } }
}

/// <summary>
/// GPU › Driver: the installed display driver (remove through pnputil, install from an extracted folder), the NVIDIA driver
/// (list, download, install with only the chosen components and tweaks) and Display Driver Uninstaller.
/// </summary>
public sealed class GpuDriverViewModel : ObservableObject
{
    List<DriverPackage> _packages = new();
    bool _busy, _isNvidia, _alsoAudio = true, _studio, _clean = true;
    string? _status, _package, _packageText, _lookupNote;
    OptionItem? _vendor, _release, _dduVendor;
    IReadOnlyList<OptionItem> _vendors = Array.Empty<OptionItem>(), _releases = Array.Empty<OptionItem>();
    IReadOnlyList<CheckItem> _components = Array.Empty<CheckItem>(), _dduOptions = Array.Empty<CheckItem>();
    List<NvidiaRelease> _found = new();

    public GpuDriverViewModel(TweakViewModel? windowsUpdateDrivers)
    {
        WindowsUpdateDrivers = windowsUpdateDrivers;
        RemoveDisplayCommand = new AsyncCommand(RemoveDisplayAsync, () => !_busy && _vendor is not null);
        InstallFromFolderCommand = new AsyncCommand(InstallFromFolderAsync, () => !_busy);
        OpenUrlCommand = new RelayCommand(p =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo((string)p!) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
        });
        FindCommand = new AsyncCommand(FindAsync, () => !_busy);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !_busy && _release is not null);
        UseFileCommand = new AsyncCommand(UseFileAsync, () => !_busy);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_busy && _package is not null && _components.Count > 0);
        DduNowCommand = new AsyncCommand(() => RunDduAsync(false), () => !_busy && _dduVendor is not null && Ddu.Available);
        DduSafeModeCommand = new AsyncCommand(() => RunDduAsync(true), () => !_busy && _dduVendor is not null && Ddu.Available);
        PostInstallTweaks = NvidiaDriver.PostInstallTweaks.Select(t => new CheckItem(t.Id, t.Name, t.Description, t.Id != "nv-hdcp")).ToList();
        DduVendors = Ddu.Vendors.Select(v => new OptionItem(v, v)).ToList();
        _dduVendor = DduVendors[0];
        UpdateDduOptions();
    }

    // ---------- installed driver ----------
    public ICommand RemoveDisplayCommand { get; }
    public ICommand InstallFromFolderCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public IReadOnlyList<OptionItem> DownloadLinks { get; } = DisplayDrivers.DownloadPages.Select(d => new OptionItem(d.Url, Txt.DownloadFrom(d.Vendor))).ToList();
    public IReadOnlyList<OptionItem> DisplayVendors { get => _vendors; private set { Set(ref _vendors, value); Raise(nameof(HasDisplayDriver)); } }
    public OptionItem? SelectedVendor { get => _vendor; set { if (value is not null) Set(ref _vendor, value); } }
    public bool HasDisplayDriver => _vendors.Count > 0;
    public bool AlsoRemoveAudio { get => _alsoAudio; set => Set(ref _alsoAudio, value); }
    public string DisplayDriverText => DisplayDrivers.Installed(_packages) is { Count: > 0 } list
        ? string.Join("\n", list.Select(p =>
        {
            var version = p.Version?.ToString() ?? "";
            var friendly = DisplayDrivers.Vendor(p.Provider) == "NVIDIA" ? NvidiaDriver.FriendlyVersion(version) : null;
            return Txt.DisplayDriverInfo(p.Provider, friendly is null ? version : $"{friendly} ({version})", p.OriginalName, p.Devices.Count);
        }))
        : Txt.NoDisplayDriver;

    /// <summary>The driver store and the adapters; the NVIDIA lookup only runs with Find drivers.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            (_packages, IsNvidia) = await Task.Run(() => (DriverStore.List(), NvidiaDriver.Adapters().Count > 0));
        }
        catch (Exception ex)
        {
            Log.Error("GPU › Driver: reading the driver store failed", ex);
            _packages = new();
        }
        var keep = _vendor?.Key;
        DisplayVendors = DisplayDrivers.Installed(_packages).Select(p => DisplayDrivers.Vendor(p.Provider)).Distinct().Select(v => new OptionItem(v, v)).ToList();
        _vendor = _vendors.FirstOrDefault(v => v.Key == keep) ?? _vendors.FirstOrDefault();
        Raise(nameof(SelectedVendor));
        Raise(nameof(DisplayDriverText));
        // DDU starts on the vendor of the installed driver (or NVIDIA when the card has none).
        var installed = _vendor?.Key ?? (IsNvidia ? "NVIDIA" : null);
        if (installed is not null && DduVendors.FirstOrDefault(v => v.Key == installed) is { } d && d != _dduVendor) SelectedDduVendor = d;
        if (WindowsUpdateDrivers is not null) await WindowsUpdateDrivers.RefreshAsync();
    }

    async Task RemoveDisplayAsync()
    {
        if (_vendor is null) return;
        var set = DisplayDrivers.RemovalSet(_packages, _vendor.Key, _alsoAudio);
        if (set.Count == 0) return;
        await RunAsync(async () =>
        {
            await DriverStore.DeleteAsync(set.Select(p => p.PublishedName).ToList(), uninstall: true, force: true);
            var removed = set.Count(p => !DriverStore.Exists(p.PublishedName));
            return (Txt.DisplayRemoved(removed, set.Count), removed < set.Count);
        });
    }

    async Task InstallFromFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Txt.ChooseDriverFolder };
        if (dialog.ShowDialog() != true) return;
        var inf = DisplayDrivers.FindDisplayInfs(dialog.FolderName).FirstOrDefault();
        if (inf is null) { Toasts.Show(Txt.DisplayInfInvalid, ToastKind.Error); return; }
        await RunAsync(async () =>
        {
            await DriverStore.InstallAsync(inf);
            // Verified against the driver store: the original INF now shows up as a published video package.
            var ok = DisplayDrivers.Installed(await Task.Run(DriverStore.List))
                .Any(p => p.OriginalName.Equals(Path.GetFileName(inf), StringComparison.OrdinalIgnoreCase));
            return ok ? (Txt.DisplayInstalled(inf), false) : (Txt.DisplayInstallNotConfirmed, true);
        });
    }

    // ---------- NVIDIA driver ----------
    public bool IsNvidia { get => _isNvidia; private set => Set(ref _isNvidia, value); }
    public IReadOnlyList<OptionItem> DriverTypes { get; } = new[] { new OptionItem("grd", Txt.GameReady), new OptionItem("studio", Txt.Studio) };
    public OptionItem SelectedDriverType
    {
        get => DriverTypes[_studio ? 1 : 0];
        set
        {
            if (value is null || (value.Key == "studio") == _studio) return;
            _studio = value.Key == "studio";
            Raise();
            Releases = Array.Empty<OptionItem>();
            LookupNote = null;
        }
    }
    public IReadOnlyList<OptionItem> Releases { get => _releases; private set { Set(ref _releases, value); SelectedRelease = value.FirstOrDefault(); Raise(nameof(HasReleases)); } }
    public OptionItem? SelectedRelease { get => _release; set => Set(ref _release, value); }
    public bool HasReleases => _releases.Count > 0;
    public string? LookupNote { get => _lookupNote; private set => Set(ref _lookupNote, value); }
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public string? PackageText { get => _packageText; private set => Set(ref _packageText, value); }
    public IReadOnlyList<CheckItem> Components { get => _components; private set { Set(ref _components, value); Raise(nameof(HasPackage)); } }
    public bool HasPackage => _components.Count > 0;
    public IReadOnlyList<CheckItem> PostInstallTweaks { get; }
    public bool CleanInstall { get => _clean; set => Set(ref _clean, value); }
    /// <summary>Windows Update's driver updates (Windows › System › Updates), here too: they can replace the driver installed here.</summary>
    public TweakViewModel? WindowsUpdateDrivers { get; }
    public ICommand FindCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand UseFileCommand { get; }
    public ICommand InstallCommand { get; }

    bool _installing, _installIndeterminate = true;
    double _installPercent;
    string? _installStep;
    public bool IsInstalling { get => _installing; private set => Set(ref _installing, value); }
    public bool InstallIndeterminate { get => _installIndeterminate; private set => Set(ref _installIndeterminate, value); }
    public double InstallPercent { get => _installPercent; private set => Set(ref _installPercent, value); }
    public string? InstallStep { get => _installStep; private set => Set(ref _installStep, value); }

    async Task FindAsync()
    {
        await RunAsync(async () =>
        {
            Status = Txt.Reading;
            var lookup = await NvidiaDriver.ListAsync(_studio, CancellationToken.None);
            _found = lookup.Releases;
            Releases = _found.Select(r => new OptionItem(r.Url, Txt.NvidiaReleaseLabel(r.Version, r.Date, r.Size))).ToList();
            LookupNote = lookup.ByDeviceId ? Txt.NvidiaByDeviceNote : null;
            return _found.Count == 0 ? (Txt.NvidiaNoReleases, true) : (Txt.NvidiaReleasesFound(_found.Count, lookup.Gpu), false);
        }, refresh: false);
    }

    async Task DownloadAsync()
    {
        if (_found.FirstOrDefault(r => r.Url == _release?.Key) is not { } release) return;
        await RunAsync(async () =>
        {
            var progress = new Progress<DownloadProgress>(p => Status = Txt.NvidiaDownloading(p.Received, p.Total));
            var path = await NvidiaDriver.DownloadAsync(release, progress, CancellationToken.None);
            return await LoadPackageAsync(path);
        }, refresh: false);
    }

    async Task UseFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Txt.ChooseNvidiaPackage, Filter = "NVIDIA driver (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        var file = dialog.FileName;
        await RunAsync(async () =>
        {
            Status = Txt.Reading;
            if (await Task.Run(() => Installer.Signer(file)) != NvidiaDriver.Publisher) return (Txt.NvidiaNotSigned(Path.GetFileName(file)), true);
            return await LoadPackageAsync(file);
        }, refresh: false);
    }

    /// <summary>Lists the package's components: Display.Driver and NVI2 checked and locked, the rest unchecked.</summary>
    async Task<(string, bool)> LoadPackageAsync(string path)
    {
        Status = Txt.Reading;
        var components = await NvidiaDriver.ComponentsAsync(path, CancellationToken.None);
        _package = path;
        Components = components.Select(c => new CheckItem(c.Folder, c.Name, c.Folder, c.Required, !c.Required)).ToList();
        PackageText = Txt.NvidiaPackageReady(Path.GetFileName(path), components.Count);
        return (PackageText, false);
    }

    async Task InstallAsync()
    {
        if (_package is not { } package) return;
        var job = new NvidiaInstallJob
        {
            Package = package,
            Components = _components.Where(c => c.IsEnabled && c.IsChecked).Select(c => c.Key).ToList(),
            Clean = _clean,
            Tweaks = PostInstallTweaks.Where(t => t.IsChecked).Select(t => t.Key).ToList(),
        };
        // The version from the file name (621.14-desktop-…exe); confirmed against the driver store afterwards.
        var expected = Regex.Match(Path.GetFileName(package), @"^\d{3}\.\d{2}").Value;
        await RunAsync(async () =>
        {
            Status = Txt.NvidiaInstallNote;
            var install = NvidiaDriver.InstallAsync(job);
            // The install runs in the elevated process: its step comes from the progress file it writes.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            IsInstalling = true;
            try
            {
                while (await Task.WhenAny(install, Task.Delay(500)) != install)
                {
                    var p = await Task.Run(NvidiaDriver.ReadProgress);
                    InstallPercent = p?.Percent ?? 0;
                    InstallIndeterminate = p?.Percent is null;
                    InstallStep = Txt.NvidiaInstallStep(p?.Step, p?.Percent, clock.Elapsed);
                }
            }
            finally { IsInstalling = false; }
            var code = await install;
            Log.Info($"GPU › Driver: NVIDIA install finished (code {code}).");
            var installed = DisplayDrivers.Installed(await Task.Run(DriverStore.List))
                .Where(p => DisplayDrivers.Vendor(p.Provider) == "NVIDIA")
                .Select(p => NvidiaDriver.FriendlyVersion(p.Version?.ToString())).Where(v => v is not null).ToList();
            var ok = expected.Length > 0 ? installed.Contains(expected) : code == 0 && installed.Count > 0;
            // Like the Setup installers: the download goes once the install is confirmed; a file the user picked stays.
            if (ok && string.Equals(Path.GetDirectoryName(Path.GetFullPath(package)), Path.GetFullPath(Downloader.Folder), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(package); _package = null; Components = Array.Empty<CheckItem>(); PackageText = null; Log.Info($"GPU › Driver: {package} deleted after the install."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"GPU › Driver: couldn't delete {package}: {ex.Message}"); }
            }
            return ok ? (Txt.NvidiaInstalled(expected.Length > 0 ? expected : installed[0]!), false)
                : (Txt.NvidiaInstallNotConfirmed(expected.Length > 0 ? expected : "?", installed.FirstOrDefault()), true);
        });
    }

    // ---------- Display Driver Uninstaller ----------
    public bool DduAvailable => Ddu.Available;
    public string? DduMissing => Ddu.Available ? null : Txt.DduMissing(Ddu.Exe);
    public IReadOnlyList<OptionItem> DduVendors { get; }
    public OptionItem? SelectedDduVendor
    {
        get => _dduVendor;
        set { if (value is not null && Set(ref _dduVendor, value)) UpdateDduOptions(); }
    }
    public IReadOnlyList<CheckItem> DduOptions { get => _dduOptions; private set => Set(ref _dduOptions, value); }
    public ICommand DduNowCommand { get; }
    public ICommand DduSafeModeCommand { get; }

    void UpdateDduOptions() => DduOptions = Ddu.Options.Where(o => o.Vendor is null || o.Vendor == _dduVendor?.Key)
        .Select(o => new CheckItem(o.Switch, o.Name, o.Description, o.Default)).ToList();

    async Task RunDduAsync(bool safeMode)
    {
        if (_dduVendor is null) return;
        var job = new DduJob { Vendor = _dduVendor.Key, Options = _dduOptions.Where(o => o.IsChecked).Select(o => o.Key).ToList(), SafeMode = safeMode };
        await RunAsync(async () =>
        {
            await Ddu.RunAsync(job);
            return (safeMode ? Txt.DduScheduled : Txt.DduStarted, false);
        }, refresh: false);
    }

    // ---------- common ----------
    async Task RunAsync(Func<Task<(string Message, bool Error)>> op, bool refresh = true)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        string message;
        var error = false;
        try { (message, error) = await op(); }
        catch (ElevationCancelledException ex) { message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("GPU › Driver: action failed", ex);
            message = ex.Message;
            error = true;
        }
        finally
        {
            _busy = false;
            Status = null;
            CommandManager.InvalidateRequerySuggested();
        }
        if (refresh) await RefreshAsync();
        Toasts.Show(message, error ? ToastKind.Error : ToastKind.Success);
    }
}
