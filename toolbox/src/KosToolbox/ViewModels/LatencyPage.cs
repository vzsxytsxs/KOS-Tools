using System.Windows.Input;
using System.Windows.Threading;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Latency;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

public sealed class LatencyRowViewModel(LatencyRow row)
{
    public string Name => row.Name;
    public string Isrs => row.Isr.Count.ToString("N0");
    public string IsrMax => row.Isr.Count == 0 ? "" : Txt.Microseconds(row.Isr.MaxUs);
    public string IsrTotal => row.Isr.Count == 0 ? "" : Txt.Milliseconds(row.Isr.TotalMs);
    public string Dpcs => row.Dpc.Count.ToString("N0");
    public string DpcMax => row.Dpc.Count == 0 ? "" : Txt.Microseconds(row.Dpc.MaxUs);
    public string DpcTotal => row.Dpc.Count == 0 ? "" : Txt.Milliseconds(row.Dpc.TotalMs);
    public bool IsSlow => row.MaxUs >= LatencyPageViewModel.SlowUs;
}

/// <summary>
/// Latency, like LatencyMon: a kernel trace (<see cref="LatencyTrace"/>) started and stopped here, with its totals read
/// every second while the page is shown. The measurement goes on while other pages are open.
/// </summary>
public sealed class LatencyPageViewModel : PageViewModel
{
    /// <summary>From 1 ms a DPC, an ISR or a thread's wait after its interrupt can cause audio dropouts and stutter.</summary>
    public const double SlowUs = 1000;
    static readonly TimeSpan Settled = TimeSpan.FromSeconds(30);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    LatencyTrace? _trace;
    LatencyReport? _report;
    bool _shown, _namesHidden;

    public LatencyPageViewModel() : base("latency", Txt.PageLatency, "", Txt.PageLatencySubtitle) // Stopwatch
    {
        Tabs = new[] { new GpuTab("dpc", "DPC / ISR", "") { IsSelected = true }, new GpuTab("mouse", "Mouse", ""), new GpuTab("timer", "Timer", ""), new GpuTab("frames", "Frames", ""), new GpuTab("ping", "Ping", "") };
        SelectTabCommand = new RelayCommand(p => SelectTab((string)p!));
        ToggleCommand = new AsyncCommand(ToggleAsync, () => Elevation.IsAdmin);
        RestartAsAdminCommand = new RelayCommand(SettingsViewModel.RestartAsAdmin);
        _timer.Tick += (_, _) => Update();
    }

    public ICommand ToggleCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public IReadOnlyList<GpuTab> Tabs { get; }
    public ICommand SelectTabCommand { get; }
    public bool IsDpcTab => Tabs[0].IsSelected;
    public bool IsMouseTab => Tabs[1].IsSelected;
    public bool IsTimerTab => Tabs[2].IsSelected;
    public bool IsFramesTab => Tabs[3].IsSelected;
    public bool IsPingTab => Tabs[4].IsSelected;
    public FramesViewModel Frames { get; } = new();
    public MouseTestViewModel Mouse { get; } = new();
    public TimerBenchViewModel Timer { get; } = new();
    public PingTestViewModel Ping { get; } = new();

    void SelectTab(string key)
    {
        foreach (var t in Tabs) t.IsSelected = t.Key == key;
        if (!IsMouseTab) Mouse.Stop();
        if (!IsPingTab) Ping.Stop();
        Raise(nameof(IsDpcTab));
        Raise(nameof(IsMouseTab));
        Raise(nameof(IsTimerTab));
        Raise(nameof(IsFramesTab));
        Raise(nameof(IsPingTab));
        if (IsTimerTab) Timer.Refresh();
        if (IsFramesTab) { Frames.RefreshPrograms(); Frames.ReloadCaptures(); }
        if (IsPingTab) Ping.RefreshHosts();
    }
    public bool NeedsAdmin => !Elevation.IsAdmin;
    public bool IsRunning => _trace is not null;
    public string ToggleText => IsRunning ? Txt.LatencyStop : Txt.LatencyStart;
    public bool HasReport => _report is not null;

    public string Elapsed => _report is null ? "" : Txt.LatencyElapsed(_report.Elapsed, IsRunning);
    public string TimerValue => _report is { TimerSamples: > 0 } r ? Txt.Microseconds(r.TimerMaxUs) : "–";
    public string TimerDetail => _report is { TimerSamples: > 0 } r ? Txt.LatencyAverage(r.TimerAverageUs) : "";
    LatencyRow? TopDpc => _report?.Drivers.Where(d => d.Dpc.Count > 0).MaxBy(d => d.Dpc.MaxUs);
    LatencyRow? TopIsr => _report?.Drivers.Where(d => d.Isr.Count > 0).MaxBy(d => d.Isr.MaxUs);
    public string DpcValue => TopDpc is { } d ? Txt.Microseconds(d.Dpc.MaxUs) : "–";
    public string DpcDetail => TopDpc?.Name ?? "";
    public string IsrValue => TopIsr is { } d ? Txt.Microseconds(d.Isr.MaxUs) : "–";
    public string IsrDetail => TopIsr?.Name ?? "";
    public string FaultsValue => (_report?.HardFaults ?? 0).ToString("N0");
    public string FaultsDetail => _report?.Faults.FirstOrDefault() is { } f ? Txt.LatencyFaultsTop(f.Process) : "";

    public IReadOnlyList<LatencyRowViewModel> Drivers => _report?.Drivers.Select(d => new LatencyRowViewModel(d)).ToList() ?? new();
    public IReadOnlyList<LatencyRowViewModel> Cpus => _report?.Cpus.Select(c => new LatencyRowViewModel(c)).ToList() ?? new();
    public IReadOnlyList<FaultRow> Faults => _report?.Faults.Take(15).ToList() ?? new();
    public bool NoFaults => _report is { HardFaults: 0 };
    public string? NoDriverNames => _namesHidden ? Txt.LatencyNoDriverNames : null;

    /// <summary>The worst driver from 1 ms, else a slow interrupt to thread, else all clear (after enough time to mean something).</summary>
    public string Verdict => _report is not { } r ? ""
        : r.Drivers.FirstOrDefault() is { } worst && worst.MaxUs >= SlowUs ? Txt.LatencySlowDriver(worst.Name, worst.MaxUs)
        : r.TimerMaxUs >= SlowUs ? Txt.LatencySlowTimer(r.TimerMaxUs)
        : r.Elapsed < Settled ? Txt.LatencyTooShort : Txt.LatencyGood;
    public bool VerdictIsWarning => _report is { } r && (r.Drivers.FirstOrDefault()?.MaxUs >= SlowUs || r.TimerMaxUs >= SlowUs);

    public override void OnShown(bool shown)
    {
        _shown = shown;
        if (shown && _trace is not null) { _timer.Start(); Update(); }
        else _timer.Stop();
        if (!shown) { Mouse.Stop(); Ping.Stop(); } // raw input and pings only while the page is open
    }

    async Task ToggleAsync()
    {
        if (_trace is { } running)
        {
            _timer.Stop();
            Update();
            _trace = null;
            await Task.Run(running.Dispose);
            Raise(nameof(Elapsed));
        }
        else
        {
            try
            {
                _trace = await Task.Run(LatencyTrace.Start);
                _namesHidden = _trace.Modules == 0;
                Raise(nameof(NoDriverNames));
                Update();
                if (_shown) _timer.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Latency: the measurement couldn't start", ex);
                Toasts.Show(Txt.LatencyStartFailed(ex.Message), ToastKind.Error);
            }
        }
        Raise(nameof(IsRunning));
        Raise(nameof(ToggleText));
    }

    void Update()
    {
        if (_trace is null) return;
        _report = _trace.Report();
        Raise(null); // every figure on the page comes from the report
    }
}
