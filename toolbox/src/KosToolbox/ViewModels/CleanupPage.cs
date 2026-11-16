using System.Windows.Input;

namespace KosToolbox.ViewModels;

/// <summary>
/// Cleanup page: everything that removes things from the system, in tabs — programs and Store apps, disk usage and junk files,
/// driver packages and devices that are no longer connected. Each tab is the page that already existed.
/// </summary>
public sealed class CleanupPageViewModel : PageViewModel
{
    readonly Dictionary<string, PageViewModel> _pages;
    PageViewModel _current;

    public CleanupPageViewModel() : base("cleanup", "Cleanup", "", "Remove apps, junk files, old driver packages and devices that aren't connected anymore.")
    {
        _pages = new()
        {
            ["apps"] = new AppsPageViewModel(),
            ["storage"] = new StoragePageViewModel(),
            ["drivers"] = new DriversPageViewModel(),
            ["ghosts"] = new DevicesPageViewModel(ghostsOnly: true),
        };
        Tabs = new[]
        {
            new GpuTab("apps", "Apps", "") { IsSelected = true }, new GpuTab("storage", "Storage", ""),
            new GpuTab("drivers", "Drivers", ""), new GpuTab("ghosts", "Ghost devices", ""),
        };
        _current = _pages["apps"];
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
    }

    public IReadOnlyList<GpuTab> Tabs { get; }
    public ICommand SelectTabCommand { get; }
    /// <summary>The open tab (the corresponding former page).</summary>
    public PageViewModel Current { get => _current; private set => Set(ref _current, value); }
    // All tabs have their own lists that scroll by themselves.
    public override bool ScrollsItself => true;

    void SelectTab(string key)
    {
        if (!_pages.TryGetValue(key, out var page) || page == _current) return;
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        _current.OnShown(false);
        Current = page;
        page.OnShown(true);
        MainViewModel.Fire(page.RefreshAsync()); // real state on every visit, as the pages used to do
    }

    public override Task RefreshAsync() => _current.RefreshAsync();
    public override void OnShown(bool shown) => _current.OnShown(shown);
}
