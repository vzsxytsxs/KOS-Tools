using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

public sealed class ToolProgramViewModel(ToolProgram program) : ObservableObject
{
    ImageSource? _icon;
    bool _iconRequested;

    public ToolProgram Program { get; } = program;
    public string Name => Program.Name;
    public string? Description => Program.Description;
    public string? Problem => Program.Problem;
    public bool HasProblem => Program.Problem is not null;
    public string Path => Program.Path;
    public string SearchText => $"{Name} {Description} {System.IO.Path.GetFileName(Path)}";

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested) { _iconRequested = true; LoadIcon(); }
            return _icon;
        }
    }

    async void LoadIcon()
    {
        _icon = await IconCache.GetAsync(Program.IconPath, large: true);
        if (_icon is not null) Raise(nameof(Icon));
    }
}

/// <summary>
/// Tools page: the programs in the KOS tools folder (they only open on a user click) and the catalog maintenance tab.
/// </summary>
public sealed class ToolsPageViewModel : PageViewModel
{
    const string ProgramsTab = "programs", MaintenanceTab = "maintenance";
    List<ToolProgramViewModel> _all = new();
    IReadOnlyList<ToolProgramViewModel> _programs = Array.Empty<ToolProgramViewModel>();
    string _search = "";
    string? _error;
    bool _loading, _loaded;

    public ToolsPageViewModel(IEnumerable<ICatalogItem> maintenance) : base("tools", Txt.PageTools, "", Txt.PageToolsSubtitle)
    {
        Maintenance = maintenance.GroupBy(i => i.Group ?? "").Select(g => new GroupViewModel(g.Key, g.ToList())).ToList();
        Tabs = new[] { new OptionItem(ProgramsTab, Txt.TabPrograms) { IsSelected = true }, new OptionItem(MaintenanceTab, Txt.TabMaintenance) };
        SelectTabCommand = new RelayCommand(p =>
        {
            foreach (var t in Tabs) t.IsSelected = t.Key == (string)p!;
            Raise(nameof(ShowPrograms));
            Raise(nameof(ShowMaintenance));
        });
        OpenCommand = new RelayCommand(p => Open((ToolProgramViewModel)p!), p => p is ToolProgramViewModel { HasProblem: false });
        RescanCommand = new AsyncCommand(ScanAsync, () => !_loading);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        OpenSetupCommand = new RelayCommand(() => OpenSetup?.Invoke());
    }

    /// <summary>Opens Setup, where the tools KOS doesn't ship (frame-time capture and others) are downloaded and installed.</summary>
    public static Action? OpenSetup { get; set; }
    public ICommand OpenSetupCommand { get; }

    public IReadOnlyList<OptionItem> Tabs { get; }
    public IReadOnlyList<GroupViewModel> Maintenance { get; }
    public bool ShowPrograms => Tabs[0].IsSelected;
    public bool ShowMaintenance => Tabs[1].IsSelected;
    public ICommand SelectTabCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public IReadOnlyList<ToolProgramViewModel> Programs { get => _programs; private set { Set(ref _programs, value); Raise(nameof(CountText)); Raise(nameof(IsEmpty)); } }
    public string CountText => Txt.ProgramsCount(_programs.Count, _all.Count);
    public bool IsEmpty => _loaded && _error is null && _programs.Count == 0;
    public string EmptyText => _all.Count == 0 ? Txt.NoPrograms(ToolDiscovery.Root) : Txt.NoProgramsMatch;
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Folder => ToolDiscovery.Root;
    public bool ShowElevatedNote => Elevation.IsAdmin;

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Filter(); }
    }

    public override async Task RefreshAsync()
    {
        foreach (var item in Maintenance.SelectMany(g => g.Items)) await item.RefreshAsync();
        if (!_loaded) await ScanAsync();
    }

    /// <summary>Lists the folder (metadata and shortcuts), without running anything.</summary>
    async Task ScanAsync()
    {
        IsLoading = true;
        try
        {
            var list = await Task.Run(ToolDiscovery.Scan);
            _all = list.Select(p => new ToolProgramViewModel(p)).ToList();
            Error = null;
        }
        catch (Exception ex)
        {
            Log.Error("Listing the KOS tools failed", ex);
            _all = new();
            Error = Txt.ToolsScanFailed(ToolDiscovery.Root, ex.Message);
        }
        finally
        {
            _loaded = true;
            IsLoading = false;
        }
        Raise(nameof(EmptyText));
        Filter();
    }

    void Filter()
    {
        var q = _search.Trim();
        Programs = q.Length == 0 ? _all : _all.Where(p => MainViewModel.Matches(p.SearchText, q)).ToList();
    }

    static void Open(ToolProgramViewModel vm)
    {
        var p = vm.Program;
        // When elevated, the toolbox would open the program as administrator: a file (or shortcut target) that a standard user
        // may have swapped is not opened.
        if (Elevation.IsAdmin && (ToolDiscovery.OwnerProblem(p.Path) ?? (p.Target is { } t && System.IO.File.Exists(t) ? ToolDiscovery.OwnerProblem(t) : null)) is { } problem)
        {
            Toasts.Show(Txt.NotOpened(problem), ToastKind.Warning);
            return;
        }
        try { ToolDiscovery.Open(p); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* the program asked for UAC and the user cancelled */ }
        catch (Exception ex)
        {
            Log.Error($"Opening {p.Path} failed", ex);
            Toasts.Show($"{p.Name}: {Txt.OpenFailed(ex.Message)}", ToastKind.Error);
        }
    }

    static void OpenFolder()
    {
        try { ToolDiscovery.OpenFolder(); }
        catch (Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }
}
