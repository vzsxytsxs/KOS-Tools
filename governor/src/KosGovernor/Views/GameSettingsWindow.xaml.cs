using System.IO;
using System.Windows;
using KosGovernor.Core;
using KosGovernor.ViewModels;
using KosToolbox.Core.Settings;

namespace KosGovernor.Views;

/// <summary>One program in the list; changing a box applies it and shows what Windows reads back.</summary>
public sealed class GameSettingsRow : ObservableObject
{
    readonly Func<GameSettingsRow, GameSetting, bool, GameWindowsSettings?> _apply;
    bool _dscp, _fse, _gpu;

    public GameSettingsRow(GameWindowsSettings s, Func<GameSettingsRow, GameSetting, bool, GameWindowsSettings?> apply)
    {
        _apply = apply;
        FullPath = s.Path;
        HasFullPath = s.HasFullPath;
        (_dscp, _fse, _gpu) = (s.Dscp, s.FullscreenOptimizationsOff, s.HighPerformanceGpu);
    }

    public string FullPath { get; }
    public bool HasFullPath { get; }
    public string Program => Path.GetFileName(FullPath);
    public string Folder => HasFullPath ? Path.GetDirectoryName(FullPath) ?? "" : "Any folder (network priority only)";
    public bool Dscp { get => _dscp; set => Change(GameSetting.Dscp, value); }
    public bool Fse { get => _fse; set => Change(GameSetting.FullscreenOptimizationsOff, value); }
    public bool Gpu { get => _gpu; set => Change(GameSetting.HighPerformanceGpu, value); }

    void Change(GameSetting what, bool on)
    {
        if (_apply(this, what, on) is { } now) Update(now);
        else { Raise(nameof(Dscp)); Raise(nameof(Fse)); Raise(nameof(Gpu)); } // the box goes back to what it was
    }

    internal void Update(GameWindowsSettings s)
    {
        Set(ref _dscp, s.Dscp, nameof(Dscp));
        Set(ref _fse, s.FullscreenOptimizationsOff, nameof(Fse));
        Set(ref _gpu, s.HighPerformanceGpu, nameof(Gpu));
    }
}

public enum GameSetting { Dscp, FullscreenOptimizationsOff, HighPerformanceGpu }

/// <summary>
/// Per-program Windows settings for games (what the KOS "Auto DSCP &amp; FSE" script set): network priority, fullscreen optimizations
/// and GPU choice, listed from the registry, changed one box at a time and read back.
/// </summary>
public partial class GameSettingsWindow : Window
{
    static readonly ColumnDef[] Defs =
    {
        new("Program", "Program", 190, "Program", Center: false),
        new("Folder", "Folder", 330, "Folder", Center: false, Fill: true),
        new("Dscp", "Network priority", 120, "Dscp", TemplateKey: "DscpCell"),
        new("Fse", "Fullscreen opt. off", 130, "Fse", TemplateKey: "FseCell"),
        new("Gpu", "High-perf. GPU", 110, "Gpu", TemplateKey: "GpuCell"),
        new("Remove", "", 90, "", TemplateKey: "RemoveCell"),
    };

    static GameSettingsWindow? _open;
    readonly MainViewModel _main;
    List<GameSettingsRow> _rows = new();

    GameSettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        new GridColumns(List, Defs, _ => { }).Show(Array.Empty<string>(), "", true);
        Load();
    }

    /// <summary>One window; asking again brings it to the front with the list read again.</summary>
    public static void ShowSingle(MainViewModel main)
    {
        if (_open is { IsLoaded: true }) { _open.Load(); _open.Activate(); return; }
        _open = new GameSettingsWindow(main) { Owner = Application.Current.MainWindow };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    void Load()
    {
        try { _rows = GameSettings.List().Select(s => new GameSettingsRow(s, Change)).ToList(); }
        catch (Exception ex) { Status.Text = $"The settings couldn't be read: {ex.Message}"; return; }
        List.ItemsSource = _rows;
        Status.Text = _rows.Count == 0 ? "No program has any of these settings. Add one, or right-click a process › Windows Settings." : $"{_rows.Count} program(s).";
    }

    GameWindowsSettings? Change(GameSettingsRow row, GameSetting what, bool on)
    {
        var (now, error) = Apply(row.FullPath, what, on);
        Status.Text = error ?? Done(row.FullPath, what, on);
        return now;
    }

    /// <summary>Writes one setting and reads the program back: (what Windows has now, null) or (null, why it failed or wasn't kept).</summary>
    internal static (GameWindowsSettings? Now, string? Error) Apply(string path, GameSetting what, bool on)
    {
        try
        {
            switch (what)
            {
                case GameSetting.Dscp: GameSettings.SetDscp(path, on); break;
                case GameSetting.FullscreenOptimizationsOff: GameSettings.SetFullscreenOptimizationsOff(path, on); break;
                default: GameSettings.SetHighPerformanceGpu(path, on); break;
            }
            var now = GameSettings.Read(path);
            var kept = what switch { GameSetting.Dscp => now.Dscp, GameSetting.FullscreenOptimizationsOff => now.FullscreenOptimizationsOff, _ => now.HighPerformanceGpu } == on;
            Log.Info($"Game settings: {Name(what)} {(on ? "on" : "off")} for {path}{(kept ? "" : " (not confirmed by reading back)")}.");
            return kept ? (now, null) : (null, $"{Path.GetFileName(path)}: Windows reads {Name(what)} back as {(on ? "off" : "on")}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Game settings: {Name(what)} for {path} failed: {ex.Message}");
            return (null, $"{Path.GetFileName(path)}: {Name(what)} wasn't changed: {ex.Message}");
        }
    }

    internal static string Name(GameSetting what) => what switch
    {
        GameSetting.Dscp => "network priority (DSCP 46)",
        GameSetting.FullscreenOptimizationsOff => "fullscreen optimizations off",
        _ => "high-performance GPU",
    };

    internal static string Done(string path, GameSetting what, bool on) =>
        $"{Path.GetFileName(path)}: {Name(what)} {(on ? "on" : "off")}" + (what == GameSetting.Dscp ? "" : " (takes effect the next time it starts)") + ".";

    void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the game's program", Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        if (_rows.FirstOrDefault(r => r.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } existing) { List.SelectedItem = existing; List.ScrollIntoView(existing); return; }
        var row = new GameSettingsRow(GameSettings.Read(path), Change);
        _rows.Insert(0, row);
        List.ItemsSource = null;
        List.ItemsSource = _rows;
        List.SelectedItem = row;
        Status.Text = $"{row.Program}: tick what it should have.";
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameSettingsRow row) return;
        var errors = new List<string>();
        foreach (var what in Enum.GetValues<GameSetting>())
        {
            var on = what switch { GameSetting.Dscp => row.Dscp, GameSetting.FullscreenOptimizationsOff => row.Fse, _ => row.Gpu };
            if (!on) continue;
            if (Apply(row.FullPath, what, false).Error is { } error) errors.Add(error);
        }
        Load();
        Status.Text = errors.Count == 0 ? $"{row.Program}: all three off." : string.Join(" ", errors);
    }
}
