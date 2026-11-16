using System.Diagnostics;
using System.Text;

namespace KosToolbox.Core.Execution;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

public static class ProcessRunner
{
    /// <summary>
    /// Runs a process without a window, captures the output and closes standard input
    /// (with optional <paramref name="stdin"/>) so script menus and "pause" do not hang. <paramref name="onLine"/> gets each line
    /// as it comes (a line a tool rewrites in place with \r counts as a line), from the reading threads.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory = null,
        string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default, Encoding? encoding = null, Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding,
            WorkingDirectory = workingDirectory ?? Environment.SystemDirectory,
        };
        // cmd does not look for commands in the current folder: a "reg.bat" left next to a script is not run in place of reg.exe.
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        if (environment is not null) foreach (var (k, v) in environment) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Received(string? line)
        {
            if (line is null) return;
            lock (output) { output.AppendLine(line); onLine?.Invoke(line); }
        }
        p.OutputDataReceived += (_, e) => Received(e.Data);
        p.ErrorDataReceived += (_, e) => Received(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
            lock (output) return new ProcessResult(-1, output.ToString(), TimedOut: true);
        }
        p.WaitForExit(); // ensures the output events have finished
        lock (output) return new ProcessResult(p.ExitCode, output.ToString(), false);
    }

    /// <summary>Synchronous version for quick reads (e.g. bcdedit).</summary>
    public static ProcessResult Run(string fileName, string arguments, TimeSpan? timeout = null)
        => RunAsync(fileName, arguments, timeout: timeout ?? TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

    /// <summary>Quoting per the CommandLineToArgvW rules, for paths with spaces.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
