using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KosToolbox.Core.Latency;

/// <summary>One Sleep(1) run: what was asked (100 ns units, 0 = nothing), the resolution in effect, and how long each Sleep(1) took.</summary>
public sealed record SleepRun(uint Requested, double ResolutionMs, double MeanMs, double MinMs, double MaxMs, double StdDevMs, double[] Samples);

/// <summary>
/// How precise Sleep(1) is, like MeasureSleep: times many Sleep(1) calls with the performance counter. With a requested resolution
/// this process asks for it during the run only (NtSetTimerResolution) and releases it after; without, it shows what a program that
/// asks for nothing gets now (since Windows 10 2004, other programs' requests only count with "Global timer resolution requests").
/// </summary>
public static class TimerBench
{
    public static SleepRun Run(uint requested, int count, CancellationToken ct)
    {
        if (requested > 0 && NtSetTimerResolution(requested, true, out _) < 0) throw new InvalidOperationException($"Windows refused the timer resolution {requested / 10000.0:0.####} ms.");
        var priority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.Sleep(1); // lines the loop up with a timer tick
            var samples = new double[count];
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp();
                Thread.Sleep(1);
                samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            var mean = samples.Average();
            return new SleepRun(requested, Query()?.CurrentMs ?? 0, mean, samples.Min(), samples.Max(),
                Math.Sqrt(samples.Sum(v => (v - mean) * (v - mean)) / count), samples);
        }
        finally
        {
            Thread.CurrentThread.Priority = priority;
            if (requested > 0) NtSetTimerResolution(requested, false, out _);
        }
    }

    /// <summary>The timer's coarsest and finest resolution and the one in effect now, in ms.</summary>
    public static (double CoarsestMs, double FinestMs, double CurrentMs)? Query() =>
        NtQueryTimerResolution(out var coarsest, out var finest, out var current) >= 0 ? (coarsest / 10000.0, finest / 10000.0, current / 10000.0) : null;

    [DllImport("ntdll.dll")] static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
}
