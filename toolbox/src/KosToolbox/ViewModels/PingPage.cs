using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using KosToolbox.Core.Latency;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>Latency › Ping: round trip, jitter and loss to one host, graphed as the replies come in.</summary>
public sealed class PingTestViewModel : ObservableObject
{
    string _host = "1.1.1.1";
    Choice<int> _interval, _duration;
    CancellationTokenSource? _cts;
    string? _status;
    bool _isError;
    IReadOnlyList<Choice<string>> _hosts = Array.Empty<Choice<string>>();
    IReadOnlyList<InfoRow> _stats = Array.Empty<InfoRow>();
    IReadOnlyList<IReadOnlyList<Point>>? _series;
    IReadOnlyList<double>? _marks;

    public PingTestViewModel()
    {
        Intervals = new[] { new Choice<int>("250 ms", 250), new Choice<int>("500 ms", 500), new Choice<int>("1 s", 1000) };
        Durations = new[] { new Choice<int>("30 s", 30), new Choice<int>("1 min", 60), new Choice<int>("5 min", 300), new Choice<int>("15 min", 900) };
        _interval = Intervals[1];
        _duration = Durations[1];
        ToggleCommand = new AsyncCommand(ToggleAsync, () => IsRunning || PingTest.ValidHost(_host.Trim()));
        RefreshHosts();
        if (_hosts.Count > 0) _host = _hosts[0].Value;
    }

    public IReadOnlyList<Choice<int>> Intervals { get; }
    public IReadOnlyList<Choice<int>> Durations { get; }
    public Choice<int> Interval { get => _interval; set => Set(ref _interval, value ?? _interval); }
    public Choice<int> Duration { get => _duration; set => Set(ref _duration, value ?? _duration); }
    public string Host
    {
        get => _host;
        set
        {
            if (!Set(ref _host, value ?? "")) return;
            Raise(nameof(HostError));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string? HostError => _host.Trim().Length == 0 || PingTest.ValidHost(_host.Trim()) ? null : Txt.PingInvalidHost;
    /// <summary>The router(s) and a few public servers; picking one fills in <see cref="Host"/>.</summary>
    public IReadOnlyList<Choice<string>> Hosts { get => _hosts; private set => Set(ref _hosts, value); }
    Choice<string>? _picked;
    public Choice<string>? PickedHost { get => _picked; set { if (Set(ref _picked, value) && value is not null) Host = value.Value; } }
    public ICommand ToggleCommand { get; }
    public bool IsRunning => _cts is not null;
    public bool IsIdle => _cts is null;
    public string ToggleText => IsRunning ? Txt.PingStop : Txt.PingStart;
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public IReadOnlyList<InfoRow> Stats { get => _stats; private set { Set(ref _stats, value); Raise(nameof(HasStats)); } }
    public bool HasStats => _stats.Count > 0;
    public IReadOnlyList<IReadOnlyList<Point>>? Series { get => _series; private set => Set(ref _series, value); }
    public IReadOnlyList<double>? Marks { get => _marks; private set => Set(ref _marks, value); }

    public void RefreshHosts()
    {
        var list = PingTest.Gateways().Select(g => new Choice<string>($"{Txt.PingRouter} · {g}", g.ToString())).ToList();
        list.Add(new Choice<string>("Cloudflare · 1.1.1.1", "1.1.1.1"));
        list.Add(new Choice<string>("Google · 8.8.8.8", "8.8.8.8"));
        list.Add(new Choice<string>("Quad9 · 9.9.9.9", "9.9.9.9"));
        Hosts = list;
    }

    /// <summary>Stops a run (tab or page left).</summary>
    public void Stop() => _cts?.Cancel();

    async Task ToggleAsync()
    {
        if (_cts is { } running) { running.Cancel(); return; }
        var host = _host.Trim();
        var interval = _interval.Value;
        var total = _duration.Value * 1000 / interval;
        var rtts = new List<double?>();
        var sent = new List<double>(); // seconds since the start: a lost reply waits for the timeout, so the pace can slip
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsError = false;
        Show(rtts, sent);
        RaiseRunning();
        try
        {
            IPAddress address;
            try { address = await PingTest.ResolveAsync(host, cts.Token); }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
            {
                Status = Txt.PingResolveFailed(host, ex.Message);
                IsError = true;
                return;
            }
            using var ping = new Ping();
            await PingTest.ProbeAsync(ping, address); // not counted: the first send loads the ICMP code and reads several ms slower
            var clock = Stopwatch.StartNew();
            var next = TimeSpan.Zero;
            for (var i = 0; i < total && !cts.IsCancellationRequested; i++)
            {
                // One interval after the previous probe was sent; after a timeout the next goes right away, never in a burst.
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                next = clock.Elapsed + TimeSpan.FromMilliseconds(interval);
                sent.Add(clock.Elapsed.TotalSeconds);
                rtts.Add(await PingTest.ProbeAsync(ping, address));
                Show(rtts, sent);
                Status = Txt.PingRunning(host, address.ToString(), rtts.Count, total);
            }
            Status = cts.IsCancellationRequested ? Txt.PingStopped(rtts.Count) : Txt.PingDone(host, rtts.Count);
        }
        catch (OperationCanceledException) { Status = Txt.PingStopped(rtts.Count); }
        finally
        {
            _cts = null;
            RaiseRunning();
            if (rtts.Count > 0 && PingTest.Compute(rtts) is var s)
                Log.Info($"Ping: {host}, {s.Sent} sent, {s.LossPercent:0.#}% lost, average {s.AvgMs:0.0} ms, jitter {s.JitterMs:0.0} ms.");
        }
    }

    void RaiseRunning()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsIdle));
        Raise(nameof(ToggleText));
        CommandManager.InvalidateRequerySuggested();
    }

    void Show(List<double?> rtts, List<double> sent)
    {
        double X(int i) => sent[i];
        var rtt = new List<Point>();
        var jitter = new List<Point>();
        var lost = new List<double>();
        var i = 0;
        foreach (var d in PingTest.Jitter(rtts))
        {
            if (rtts[i] is { } r) rtt.Add(new Point(X(i), r)); else lost.Add(X(i));
            if (d is { } j) jitter.Add(new Point(X(i), j));
            i++;
        }
        Series = new IReadOnlyList<Point>[] { rtt, jitter };
        Marks = lost;
        var s = PingTest.Compute(rtts);
        var rows = new List<InfoRow>();
        if (s.Sent > 0) rows.Add(new InfoRow(Txt.PingRepliesLabel, Txt.PingReplies(s.Received, s.Sent, s.LossPercent)));
        if (s.AvgMs is { } avg) rows.Add(new InfoRow(Txt.PingRoundTripLabel, Txt.PingRoundTrip(s.MinMs!.Value, avg, s.MaxMs!.Value)));
        if (s.JitterMs is { } jit) rows.Add(new InfoRow(Txt.PingJitterLabel, Txt.PingJitter(jit, s.MaxJitterMs!.Value)));
        Stats = rows;
    }
}
