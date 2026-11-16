namespace KosToolbox.Core.Execution;

/// <summary>Single point to start elevated tasks; replaceable in tests.</summary>
public static class ElevatedRunner
{
    public static Func<ElevatedJob, CancellationToken, Task<int>> Handler { get; set; } = (job, ct) => job.RunElevatedAsync(ct);

    public static Task<int> RunAsync(ElevatedJob job, CancellationToken ct = default) => Handler(job, ct);
}
