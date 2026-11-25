using System.Runtime.InteropServices;

namespace KosToolbox.Core.Audio;

/// <summary>
/// Hears a microphone through KOS Equalizer: shared-mode WASAPI capture (it goes through the endpoint's effects, so the equalizer
/// is in what comes out) played on the default playback device, live or after a short recording. Nothing is saved to disk.
/// </summary>
public sealed class MicMonitor : IDisposable
{
    public const int MaxRecordSeconds = 10;

    readonly CancellationTokenSource _stop = new();
    Task? _task;

    /// <summary>Raised when the loop ends: null for a normal stop (the user's, or the recording played out), else why it failed.</summary>
    public event Action<string?>? Ended;
    /// <summary>Seconds recorded so far / seconds played (for a progress line); raised from the worker thread.</summary>
    public event Action<string>? Progress;

    /// <summary>The microphone's sound straight to the default playback device until <see cref="Dispose"/>.</summary>
    public static MicMonitor Listen(string micId)
    {
        var m = new MicMonitor();
        return m.Begin(ct => Loop(micId, ct, live: true, null, m));
    }

    /// <summary>Records <paramref name="seconds"/> from the microphone, then plays the take on the default playback device.</summary>
    public static MicMonitor RecordAndPlay(string micId, int seconds)
    {
        var m = new MicMonitor();
        return m.Begin(ct => Loop(micId, ct, live: false, Math.Clamp(seconds, 1, MaxRecordSeconds), m));
    }

    MicMonitor Begin(Func<CancellationToken, string?> work)
    {
        _task = Task.Factory.StartNew(() =>
        {
            string? problem = null;
            try { problem = work(_stop.Token); }
            catch (Exception ex) { problem = ex.Message; }
            Ended?.Invoke(problem);
        }, TaskCreationOptions.LongRunning);
        return this;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _task?.Wait(3000); } catch { }
        _stop.Dispose();
    }

    static string? Loop(string micId, CancellationToken ct, bool live, int? seconds, MicMonitor? owner)
    {
        var scratch = new byte[8192];
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        object? captureObj = null, renderObj = null;
        IntPtr fmt = IntPtr.Zero;
        try
        {
            Check(enumerator.GetDevice(micId, out var mic), "Opening the microphone");
            Check(enumerator.GetDefaultAudioEndpoint(0, 1, out var speakers), "Opening the default playback device");
            var iid = typeof(IAudioClient).GUID;
            Check(mic.Activate(ref iid, 23, IntPtr.Zero, out captureObj), "Opening the microphone's audio client");
            Check(speakers.Activate(ref iid, 23, IntPtr.Zero, out renderObj), "Opening the playback device's audio client");
            var capture = (IAudioClient)captureObj;
            var render = (IAudioClient)renderObj;

            Check(capture.GetMixFormat(out fmt), "Reading the microphone's format");
            var format = Marshal.PtrToStructure<WaveFormat>(fmt);
            var frameBytes = format.BlockAlign;
            if (frameBytes <= 0) throw new InvalidOperationException("The microphone's format has no frame size.");
            // 100 ms buffers; the playback side converts the microphone's format to its own (sample rate and channels).
            Check(capture.Initialize(0, 0, 1_000_000, 0, fmt, IntPtr.Zero), "Starting the microphone");
            const uint AutoConvert = 0x80000000, SrcDefaultQuality = 0x08000000;
            Check(render.Initialize(0, AutoConvert | SrcDefaultQuality, 1_000_000, 0, fmt, IntPtr.Zero), "Starting playback");

            var gc = typeof(IAudioCaptureClient).GUID;
            Check(capture.GetService(ref gc, out var capService), "Reading the microphone's buffer");
            var reader = (IAudioCaptureClient)capService;
            var gr = typeof(IAudioRenderClient).GUID;
            Check(render.GetService(ref gr, out var renService), "Writing the playback buffer");
            var writer = (IAudioRenderClient)renService;
            Check(render.GetBufferSize(out var renderFrames), "Reading the playback buffer size");

            var take = live ? null : new MemoryStream();
            var needBytes = (long)(seconds ?? 0) * format.SamplesPerSec * frameBytes;
            Check(capture.Start(), "Starting the microphone");
            var started = false;
            var lastReport = 0;

            // Live: every packet goes straight to the playback buffer (dropped when it is full, which only happens if playback stalls).
            // Recording: packets go to memory until the take is long enough.
            while (!ct.IsCancellationRequested)
            {
                Check(reader.GetNextPacketSize(out var packet), "Reading the microphone");
                if (packet == 0) { Thread.Sleep(4); continue; }
                while (packet > 0 && !ct.IsCancellationRequested)
                {
                    Check(reader.GetBuffer(out var data, out var frames, out var flags, out _, out _), "Reading the microphone");
                    var silent = (flags & 2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                    if (live)
                    {
                        Check(render.GetCurrentPadding(out var padding), "Reading the playback buffer");
                        var room = renderFrames - padding;
                        var n = Math.Min(frames, room);
                        if (n > 0)
                        {
                            Check(writer.GetBuffer(n, out var dst), "Writing the playback buffer");
                            if (silent) Zero(dst, (int)n * frameBytes, ref scratch, owner); else Copy(data, dst, (int)n * frameBytes, ref scratch, format, owner);
                            Check(writer.ReleaseBuffer(n, 0), "Writing the playback buffer");
                            if (!started) { Check(render.Start(), "Starting playback"); started = true; }
                        }
                    }
                    else
                    {
                        var bytes = (int)frames * frameBytes;
                        var chunk = new byte[bytes];
                        if (!silent) Marshal.Copy(data, chunk, 0, bytes);
                        if (owner is not null) owner._peak = silent ? 0 : PeakOf(chunk, bytes, format);
                        take!.Write(chunk, 0, bytes);
                    }
                    Check(reader.ReleaseBuffer(frames), "Reading the microphone");
                    Check(reader.GetNextPacketSize(out packet), "Reading the microphone");
                }
                if (take is not null)
                {
                    var sec = (int)(take.Length / (format.SamplesPerSec * frameBytes));
                    if (sec != lastReport) { lastReport = sec; owner?.Progress?.Invoke($"Recording {sec}/{seconds} s"); }
                    if (take.Length >= needBytes) break;
                }
            }
            capture.Stop();
            if (take is null || ct.IsCancellationRequested) { if (started) render.Stop(); return null; }

            // Playback of the take.
            var bytesAll = take.ToArray();
            var pos = 0;
            owner?.Progress?.Invoke("Playing");
            Check(render.Start(), "Starting playback");
            while (pos < bytesAll.Length && !ct.IsCancellationRequested)
            {
                Check(render.GetCurrentPadding(out var padding), "Reading the playback buffer");
                var room = (int)(renderFrames - padding);
                var n = Math.Min(room, (bytesAll.Length - pos) / frameBytes);
                if (n <= 0) { Thread.Sleep(10); continue; }
                Check(writer.GetBuffer((uint)n, out var dst), "Writing the playback buffer");
                Marshal.Copy(bytesAll, pos, dst, n * frameBytes);
                Check(writer.ReleaseBuffer((uint)n, 0), "Writing the playback buffer");
                pos += n * frameBytes;
            }
            // Let what is already queued finish.
            while (!ct.IsCancellationRequested)
            {
                Check(render.GetCurrentPadding(out var padding), "Reading the playback buffer");
                if (padding == 0) break;
                Thread.Sleep(20);
            }
            render.Stop();
            return null;
        }
        finally
        {
            if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
            if (captureObj is not null) Marshal.ReleaseComObject(captureObj);
            if (renderObj is not null) Marshal.ReleaseComObject(renderObj);
        }
    }

    /// <summary>The loudest sample (0 to 1) of the last packet read from the microphone, for a level readout; updated by the worker thread.</summary>
    public float Peak => _peak;
    volatile float _peak;

    static void Copy(IntPtr from, IntPtr to, int bytes, ref byte[] scratch, WaveFormat format, MicMonitor? owner)
    {
        if (scratch.Length < bytes) scratch = new byte[bytes];
        Marshal.Copy(from, scratch, 0, bytes);
        if (owner is not null) owner._peak = PeakOf(scratch, bytes, format);
        Marshal.Copy(scratch, 0, to, bytes);
    }

    static void Zero(IntPtr to, int bytes, ref byte[] scratch, MicMonitor? owner)
    {
        if (scratch.Length < bytes) scratch = new byte[bytes];
        Array.Clear(scratch, 0, bytes);
        if (owner is not null) owner._peak = 0;
        Marshal.Copy(scratch, 0, to, bytes);
    }

    /// <summary>The largest absolute sample of 32-bit float or 16-bit PCM data (the formats a shared-mode microphone uses).</summary>
    internal static float PeakOf(byte[] data, int bytes, WaveFormat format)
    {
        var peak = 0f;
        if (format.BitsPerSample == 32)
            for (var i = 0; i + 4 <= bytes; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data, i)));
        else if (format.BitsPerSample == 16)
            for (var i = 0; i + 2 <= bytes; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data, i)) / 32768f);
        return Math.Min(peak, 1f);
    }

    static void Check(int hr, string what)
    {
        if (hr == 0) return;
        throw new InvalidOperationException(hr switch
        {
            unchecked((int)0x88890004) => $"{what} failed: the device was removed.",
            unchecked((int)0x8889000A) => $"{what} failed: the device is in use by a program that wants it exclusively.",
            unchecked((int)0x80070005) => $"{what} failed: access denied (Windows' microphone privacy setting may be off for desktop apps).",
            _ => $"{what} failed: 0x{hr:X8}.",
        });
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat { public ushort Tag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, Size; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, [MarshalAs(UnmanagedType.IUnknown)] out object devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
