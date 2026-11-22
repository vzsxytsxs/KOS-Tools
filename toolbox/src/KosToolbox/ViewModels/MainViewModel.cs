using System.Globalization;
using System.Windows.Input;
using KosToolbox.Core.Catalog;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// A sidebar group: its header and its pages. Each group marks the current page only when it holds it: a ListBox given an item from
/// another list keeps its old selection marked, so sharing one selected page would leave two rows lit.
/// </summary>
public sealed class NavListViewModel(Action<PageViewModel> select, string title, params PageViewModel[] pages) : ObservableObject
{
    PageViewModel? _selected;

    /// <summary>Empty for the last group (Settings), which has no header.</summary>
    public string Title { get; } = title;
    public string Header { get; } = title.ToUpperInvariant();
    public bool HasHeader => Title.Length > 0;
    public IReadOnlyList<PageViewModel> Pages { get; } = pages;
    /// <summary>The KOS Apps group: buttons that open the other apps, shown without a selection.</summary>
    public bool IsLinks { get; } = pages.All(p => p is AppLinkViewModel);
    public PageViewModel? Selected { get => _selected; set { if (value is not null) select(value); } }

    public void Show(PageViewModel current) => Set(ref _selected, Pages.Contains(current) ? current : null, nameof(Selected));
}

public sealed class MainViewModel : ObservableObject
{
    readonly IReadOnlyList<ICatalogItem> _all;
    PageViewModel _selected;
    PageViewModel _current;
    string _search = "";
    bool _navCollapsed = AppSettings.Current.NavCollapsed;

    public MainViewModel()
    {
        // One view model per item, shared between the page and the search (so the state stays consistent).
        _all = KosCatalog.Tweaks.Select(t => (ICatalogItem)new TweakViewModel(t))
            .Concat(KosCatalog.Actions.Select(a => new ActionViewModel(a))).ToList();

        Action<PageViewModel> select = p => SelectedPage = p;
        var gpu = new GpuPageViewModel(ItemsOf(Core.Catalog.Pages.Gpu), _all.OfType<TweakViewModel>().FirstOrDefault(t => t.Tweak.Id == "wu-drivers"));
        DriversPageViewModel.OpenGpuDriver = () => { gpu.SelectTab("driver"); SelectedPage = gpu; };
        var kosState = new KosStatePageViewModel(_all.OfType<TweakViewModel>());
        KosStatePageViewModel.Open = () => SelectedPage = kosState;
        var setup = new SetupPageViewModel();
        ToolsPageViewModel.OpenSetup = () => SelectedPage = setup;
        NavGroups = new[]
        {
            new NavListViewModel(select, Txt.NavOverview, new HomeViewModel(),
                kosState), // what Windows Update, sfc or DISM put back
            new NavListViewModel(select, Txt.NavTune,
                new CatalogPageViewModel(Core.Catalog.Pages.Windows, Txt.PageWindows, "", Txt.PageWindowsSubtitle, ItemsOf(Core.Catalog.Pages.Windows)),
                new PowerPageViewModel(ItemsOf(Core.Catalog.Pages.Power)),
                gpu, // includes the former Display page as a tab, the NVIDIA tool tweaks and the Driver tab
                new NetworkPageViewModel(),
                new PeripheralsPageViewModel(), // HID mice, keyboards and other devices: the list and each one's collections
                new AudioPageViewModel()), // playback and recording devices: default, volume, format, exclusive mode, enhancements
            new NavListViewModel(select, Txt.NavSystem, new StartupPageViewModel(), new ServicesPageViewModel(ItemsOf(Core.Catalog.Pages.Services)), new DevicesPageViewModel(),
                new BiosPageViewModel()), // BIOS settings through SCEWIN, with a backup on every write
            new NavListViewModel(select, Txt.NavSoftware, setup,
                new WindowsUpdatePageViewModel(), // search, install and hide updates while Windows Update stays off
                new DebloatPageViewModel(),
                new ToolsPageViewModel(ItemsOf(Core.Catalog.Pages.Tools)),
                new CleanupPageViewModel()), // Apps, Storage, Drivers and Ghost devices, as tabs
            new NavListViewModel(select, Txt.NavDiagnose,
                new LatencyPageViewModel(), // DPC/ISR per driver, mouse report rate and Sleep(1) precision
                new StabilityPageViewModel()), // crashes, driver resets and hardware errors from the System log
            // The other KOS apps: each entry opens the app (or brings it to the front); the page that's open stays.
            new NavListViewModel(select, Txt.NavKosApps,
                new AppLinkViewModel("app-governor", Txt.AppGovernorLink, "", Txt.AppGovernorSubtitle, "governor", "KosGovernor.exe",
                    "M2,9 V14 M5,5 V14 M8,7 V14 M11,2 V14 M14,6 V14"), // its logo's bars
                new AppLinkViewModel("app-firewall", Txt.AppFirewallLink, "", Txt.AppFirewallSubtitle, "firewall", "KosFirewall.exe"),
                new AppLinkViewModel("app-equalizer", Txt.AppEqualizerLink, "", Txt.AppEqualizerSubtitle, "equalizer", "KosEqualizer.exe")),
            new NavListViewModel(select, "", new SettingsViewModel()),
        };
        Pages = NavGroups.SelectMany(g => g.Pages).Where(p => p is not AppLinkViewModel).ToList();
        _selected = _current = Pages[0]; // always opens on Home (system dashboard)
        NavGroups[0].Show(_selected);
        RefreshCommand = new AsyncCommand(() => Current.RefreshAsync());
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleNavCommand = new RelayCommand(() => NavCollapsed = !NavCollapsed);
        _current.OnShown(true);
        Fire(_current.RefreshAsync());
    }

    IEnumerable<ICatalogItem> ItemsOf(string page) => _all.Where(i => i.Page == page);

    /// <summary>Every page, in sidebar order; the sidebar itself shows them through the lists below.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }
    public IReadOnlyList<NavListViewModel> NavGroups { get; }
    public string Title => Elevation.IsAdmin ? Txt.WindowTitleAdmin : Txt.WindowTitle;
    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleNavCommand { get; }

    /// <summary>Icons only, with the names in tooltips; the choice is kept across runs.</summary>
    public bool NavCollapsed
    {
        get => _navCollapsed;
        private set
        {
            if (!Set(ref _navCollapsed, value)) return;
            Raise(nameof(NavWidth));
            Raise(nameof(NavToggleTooltip));
            AppSettings.Current.NavCollapsed = value;
            AppSettings.Current.Save();
        }
    }
    public double NavWidth => _navCollapsed ? 60 : 208;
    public string NavToggleTooltip => _navCollapsed ? Txt.NavExpandTooltip : Txt.NavCollapseTooltip;

    public PageViewModel SelectedPage
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            foreach (var group in NavGroups) group.Show(value);
            if (_search.Length > 0) { _search = ""; Raise(nameof(SearchText)); }
            Current = value;
            Fire(value.RefreshAsync()); // real state on every visit; nothing is applied automatically
        }
    }

    public PageViewModel Current
    {
        get => _current;
        private set
        {
            var old = _current;
            if (!Set(ref _current, value)) return;
            old.OnShown(false);
            value.OnShown(true);
        }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            var q = _search.Trim();
            if (q.Length < 2) { Current = _selected; return; }
            var results = new CatalogPageViewModel("search", Txt.SearchResults(q), "", Txt.SearchSubtitle,
                _all.Where(i => Matches(i.SearchText, q)), i => PageTitle(i.Page) + (i.Tab is null ? "" : " › " + i.Tab), singleTab: true);
            Current = results;
            Fire(results.EnsureLoadedAsync());
        }
    }

    /// <summary>Background reads: an unexpected failure goes to the log instead of vanishing.</summary>
    internal static async void Fire(Task task)
    {
        try { await task; }
        catch (Exception ex) { Log.Error("Page refresh failed", ex); }
    }

    // Ignores case and accents.
    internal static bool Matches(string text, string query) => CultureInfo.InvariantCulture.CompareInfo
        .IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    string PageTitle(string key) => Pages.FirstOrDefault(p => p.Key == key)?.Title ?? key;
}
