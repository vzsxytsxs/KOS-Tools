using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Tools;

namespace KosToolbox.Core.Setup;

public readonly record struct DownloadProgress(long Received, long? Total, double BytesPerSecond, bool Aria2 = false);

/// <summary>
/// Downloads for the Setup page, into the user's Downloads\KOS Setup folder. With aria2c.exe in the KOS tools folder, downloads
/// with it (8 connections); without it, or if it fails, with HttpClient.
/// </summary>
public static class Downloader
{
    const string UserAgent = "KOS-Toolbox/1.0";
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500), Stall = TimeSpan.FromSeconds(60);

    public static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        // No automatic decompression: Content-Length must be the file size.
        var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>
    /// Installers: %ProgramData%\TheKOS\Toolbox\Downloads (only administrators can write; the installer runs elevated and is
    /// deleted after installing). Without administrator, the user's data folder.
    /// </summary>
    public static string Folder => Elevation.IsAdmin ? Path.Combine(AppPaths.TheKosRoot, "Toolbox", "Downloads") : Path.Combine(AppPaths.DataRoot, "Downloads");

    /// <summary>Portable programs (the file is the program): the user's Downloads folder.</summary>
    public static string PortableFolder => KnownDownloads();

    /// <summary>The downloads folder, created as the downloads create it (admin-only when elevated); for opening it in Explorer.</summary>
    public static string EnsureFolder()
    {
        PrepareFolder(Folder);
        return Folder;
    }

    static void PrepareFolder(string folder)
    {
        if (folder == Folder && Elevation.IsAdmin)
        {
            Gpu.GpuTuner.PrepareDirectory(Path.GetDirectoryName(folder)!); // TheKOS\Toolbox, protected
            Gpu.GpuTuner.PrepareDirectory(folder);
        }
        else Directory.CreateDirectory(folder);
    }

    /// <summary>The portable program already downloaded (the newest with the catalog's name, e.g. OnboardMemoryManager*.exe); null if none.</summary>
    public static string? ExistingPortable(SetupApp app)
    {
        if (!app.Portable) return null;
        try
        {
            var pattern = Path.GetFileNameWithoutExtension(app.FileName) + "*" + Path.GetExtension(app.FileName);
            return new DirectoryInfo(PortableFolder).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>aria2c.exe placed by the user in the KOS tools folder (the toolbox doesn't download executables on its own).</summary>
    public static string? Aria2 => new[] { Path.Combine(ToolDiscovery.Root, "aria2c.exe"), Path.Combine(ToolDiscovery.Root, "aria2", "aria2c.exe") }.FirstOrDefault(File.Exists);

    /// <summary>Link to the program's current installer, with a timeout.</summary>
    public static async Task<string> ResolveAsync(SetupApp app, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await app.Resolve!(Http, timeout.Token);
    }

    /// <summary>Downloads the file; returns the path and whether aria2 was used.</summary>
    public static async Task<(string Path, bool Aria2)> DownloadAsync(string url, string fileName, string folder, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        PrepareFolder(folder);
        var aria = Aria2;
        // When elevated, the toolbox would run aria2c as administrator: only if it is owned by SYSTEM/Administrators.
        if (aria is not null && Elevation.IsAdmin && ToolDiscovery.OwnerProblem(aria) is { } problem) { Log.Warn($"Setup: not using aria2c. {problem}"); aria = null; }

        string path;
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var disposition = response.Content.Headers.ContentDisposition;
            path = Path.Combine(folder, FileNameFor(disposition?.FileNameStar ?? disposition?.FileName, response.RequestMessage?.RequestUri, fileName));
            if (aria is null) { await CopyAsync(response, path, progress, ct); return (path, false); }
            url = response.RequestMessage!.RequestUri!.AbsoluteUri; // aria2 gets the final link (without the redirects)
        }
        try
        {
            await Aria2Async(aria, url, path, progress, ct);
            return (path, true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"Setup: aria2c failed for {url}, using the built-in downloader. {ex.Message}");
        }
        using var again = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        again.EnsureSuccessStatusCode();
        await CopyAsync(again, path, progress, ct);
        return (path, false);
    }

    /// <summary>
    /// File name: the server's (Content-Disposition or end of the link), if it has the same extension as the catalog; a
    /// server-provided name can't turn the installer into a .bat or .lnk or escape the folder.
    /// </summary>
    internal static string FileNameFor(string? disposition, Uri? final, string fallback)
    {
        static string Clean(string n) => string.Concat(Path.GetFileName(n.Trim('"', ' ')).Split(Path.GetInvalidFileNameChars())).Trim();
        var name = Clean(disposition ?? Uri.UnescapeDataString(final?.Segments.LastOrDefault() ?? ""));
        fallback = Clean(fallback); // the Store's package names come from its service: never a path out of the folder
        return name.Length > 0 && Path.GetExtension(name).Equals(Path.GetExtension(fallback), StringComparison.OrdinalIgnoreCase) ? name : fallback;
    }

    static async Task CopyAsync(HttpResponseMessage response, string path, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var total = response.Content.Headers.ContentLength;
        var part = path + ".part";
        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0, lastBytes = 0;
                var clock = Stopwatch.StartNew();
                var last = TimeSpan.Zero;
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                while (true)
                {
                    stall.CancelAfter(Stall);
                    var n = await src.ReadAsync(buffer, stall.Token);
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    received += n;
                    if (clock.Elapsed - last < Tick) continue;
                    progress.Report(new(received, total, (received - lastBytes) / (clock.Elapsed - last).TotalSeconds));
                    last = clock.Elapsed;
                    lastBytes = received;
                }
                if (total is { } t && received != t) throw new IOException($"The download stopped at {received:N0} of {t:N0} bytes.");
                progress.Report(new(received, total, 0));
            }
            File.Move(part, path, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(part);
            throw new TimeoutException("The server stopped sending data for 60 seconds.");
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    static async Task Aria2Async(string exe, string url, string path, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[]
                 {
                     "--dir", Path.GetDirectoryName(path)!, "--out", Path.GetFileName(path), "--max-connection-per-server=8", "--split=8", "--min-split-size=1M",
                     "--file-allocation=none", "--allow-overwrite=true", "--auto-file-renaming=false", "--summary-interval=1", "--console-log-level=warn",
                     "--download-result=hide", "--user-agent=" + UserAgent,
                     // A metalink or torrent answer (plain-HTTP links can be answered by anyone on the way) must not make it fetch other
                     // files into the folder, and a user's aria2.conf (hooks that run commands) isn't read by the elevated toolbox.
                     "--follow-metalink=false", "--follow-torrent=false", "--no-conf", url,
                 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("aria2c didn't start.");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var lastMessage = "";
        try
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (ParseAria2(line) is { } r) progress.Report(r);
                else if (line.Trim() is { Length: > 0 } text && !text.StartsWith("***") && !text.StartsWith("==") && !text.StartsWith("--")) lastMessage = text;
            }
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
            TryDelete(path);
            TryDelete(path + ".aria2");
            throw;
        }
        if (p.ExitCode != 0)
        {
            TryDelete(path);
            TryDelete(path + ".aria2");
            throw new IOException($"aria2c exited with code {p.ExitCode}. {(await stderr).Trim()} {lastMessage}".Trim());
        }
    }

    static readonly Regex Aria2Readout = new(@"\[#\w+\s+([\d.]+)([KMG]?i?B)/([\d.]+)([KMG]?i?B)\(\d+%\).*?DL:([\d.]+)([KMG]?i?B)", RegexOptions.Compiled);

    /// <summary>aria2c progress line, e.g.: [#2089b0 400.0KiB/33.2MiB(1%) CN:1 DL:115.7KiB ETA:4m51s].</summary>
    internal static DownloadProgress? ParseAria2(string line)
    {
        var m = Aria2Readout.Match(line);
        if (!m.Success) return null;
        double Bytes(int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * m.Groups[g + 1].Value switch
        {
            "KiB" => 1024.0, "MiB" => 1024.0 * 1024, "GiB" => 1024.0 * 1024 * 1024, _ => 1,
        };
        return new((long)Bytes(1), (long)Bytes(3), Bytes(5), Aria2: true);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    static string KnownDownloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
