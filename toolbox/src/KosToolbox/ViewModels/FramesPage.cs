using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Latency;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Views;

namespace KosToolbox.ViewModels;

/// <summary>Latency › Frames: a timed PresentMon capture of one program, with its FPS, lows, spikes and present mode.</summary>
public sealed class FramesViewModel : ObservableObject
{
    string _program = "";
    Choice<int> _duration;
    bool _running;
    string? _status;
    bool _isError;
    IReadOnlyList<string> _programs = Array.Empty<string>();
    IReadOnlyList<InfoRow> _results = Array.Empty<InfoRow>();
    IReadOnlyList<IReadOnlyList<Point>>? _series;

    public FramesViewModel()
    {
        Durations = FrameCapture.Durations.Select(s => new Choice<int>($"{s} s", s)).ToList();
        _duration = Durations[1];
        CaptureCommand = new AsyncCommand(CaptureAsync, () => !_running && Available && Elevation.IsAdmin && FrameCapture.ValidProgram(_program.Trim()));
        RefreshProgramsCommand = new RelayCommand(RefreshPrograms);
    }

    bool _available = FrameCapture.Exe is not null;
    public bool Available { get => _available; private set { if (Set(ref _available, value)) Raise(nameof(MissingNote)); } }
    public string? MissingNote => Available ? null : "PresentMon isn't in the KOS folder (bin\\3\\PresentMon), so frames can't be captured.";
    public bool NeedsAdmin => !Elevation.IsAdmin;
    public IReadOnlyList<Choice<int>> Durations { get; }
    public Choice<int> Duration { get => _duration; set => Set(ref _duration, value ?? _duration); }
    /// <summary>Program file name, typed or picked from the programs with a window.</summary>
    public string Program { get => _program; set { if (Set(ref _program, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<string> Programs { get => _programs; private set => Set(ref _programs, value); }
    string? _picked;
    /// <summary>A program picked from the open ones: it becomes <see cref="Program"/>.</summary>
    public string? PickedProgram { get => _picked; set { if (Set(ref _picked, value) && value is not null) Program = value; } }
    public ICommand CaptureCommand { get; }
    public ICommand RefreshProgramsCommand { get; }
    public ICommand RestartAsAdminCommand { get; } = new RelayCommand(SettingsViewModel.RestartAsAdmin);
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public IReadOnlyList<InfoRow> Results { get => _results; private set => Set(ref _results, value); }
    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => _series; private set => Set(ref _series, value); }
    public bool HasResults => _results.Count > 0;

    /// <summary>Programs that have a window in this session, by name.</summary>
    public void RefreshPrograms()
    {
        Available = FrameCapture.Exe is not null;
        var names = new List<string>();
        foreach (var p in Process.GetProcesses())
            using (p)
                try { if (p.MainWindowHandle != IntPtr.Zero && p.SessionId != 0) names.Add(p.ProcessName + ".exe"); }
                catch (InvalidOperationException) { }
        Programs = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    async Task CaptureAsync()
    {
        var program = _program.Trim();
        var seconds = _duration.Value;
        _running = true;
        IsError = false;
        CommandManager.InvalidateRequerySuggested();
        var started = DateTime.Now;
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => Status = $"Capturing {program}: {Math.Max(0, seconds - (int)(DateTime.Now - started).TotalSeconds)} s left. Keep the game in front and playing.";
        tick.Start();
        Status = $"Capturing {program}: {seconds} s left. Keep the game in front and playing.";
        try
        {
            Show(await FrameCapture.CaptureAsync(program, seconds));
        }
        catch (Exception ex)
        {
            Log.Warn($"Frames: capture of {program} failed: {ex.Message}");
            Status = ex.Message;
            IsError = true;
        }
        finally
        {
            tick.Stop();
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void Show(FrameReport r)
    {
        Results = Format(r);
        Raise(nameof(HasResults));
        Series = new[] { (IReadOnlyList<Point>)r.FrameTimesMs.Select((ms, i) => new Point(i + 1, ms)).ToList() };
        Status = r.IndependentFlip
            ? $"{r.Program} presents with independent flip: its frames skip the desktop compositor."
            : $"{r.Program} presents through the desktop compositor ({r.PresentModes.FirstOrDefault()?.Mode}). Exclusive fullscreen, fullscreen optimizations or borderless with MPO usually gives independent flip.";
    }

    internal static IReadOnlyList<InfoRow> Format(FrameReport r) => new[]
    {
        new InfoRow("Average", $"{r.AverageFps:0.0} FPS ({r.Frames:N0} frames in {r.Seconds:0.0} s)"),
        new InfoRow("1 % low", $"{r.Low1Fps:0.0} FPS"),
        new InfoRow("0.1 % low", $"{r.Low01Fps:0.0} FPS"),
        new InfoRow("Frame time", $"median {r.MedianMs:0.00} ms · worst {r.MaxMs:0.0} ms"),
        new InfoRow("Spikes", $"{r.Spikes:N0} frame(s) over twice the median"),
        new InfoRow("Present mode", string.Join(" · ", r.PresentModes.Take(3).Select(m => $"{m.Mode} {m.Share:P0}"))),
        new InfoRow("Tearing allowed", $"{r.TearingShare:P0} of frames"),
    };
}
