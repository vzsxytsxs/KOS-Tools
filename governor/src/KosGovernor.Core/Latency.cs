using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KosGovernor.Core;

/// <summary>
/// The system timer resolution the engine asks for (NtSetTimerResolution, 100 ns units), held while <see cref="TimerSettings.Mode"/> says so.
/// Since Windows 10 2004 a request only counts for the process that made it, unless "Global timer resolution requests" is on
/// (HKLM\...\Session Manager\kernel GlobalTimerResolutionRequests = 1, a toolbox setting). Games are also told to keep their own request
/// in the background (PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION off): Windows 11 otherwise drops it when the window is minimized
/// or hidden.
/// </summary>
public static class TimerResolution
{
    public const int Finest = 5000, Coarsest = 156250;
    const uint IgnoreTimerResolution = 0x4;
    static uint? _held, _failed;
    /// <summary>The resolution the engine holds now, in 100 ns units.</summary>
    public static uint? Held => _held;

    public static void Sync(TimerSettings s, bool gamemode)
    {
        uint? want = s.Mode == TimerMode.Always || s.Mode == TimerMode.Gamemode && gamemode ? (uint)Math.Clamp(s.Resolution, Finest, Coarsest) : null;
        if (want == _held || want is not null && want == _failed) return;
        if (_held is { } old)
        {
            NtSetTimerResolution(old, false, out _);
            _held = null;
            Log.Info("Timer resolution request released.");
        }
        _failed = null;
        if (want is not { } res) return;
        var status = NtSetTimerResolution(res, true, out var current);
        if (status < 0)
        {
            _failed = res;
            Log.Warn($"Timer resolution {res / 10000.0:0.###} ms wasn't set: {Native.NtMessage(status)}");
            return;
        }
        _held = res;
        Log.Info($"Timer resolution {res / 10000.0:0.###} ms requested (now {current / 10000.0:0.###} ms)"
                 + (GlobalRequests ? "." : "; \"Global timer resolution requests\" is off, so it only applies to the engine's own timers."));
    }

    /// <summary>Engine exiting.</summary>
    public static void Release() => Sync(new TimerSettings { Mode = TimerMode.Off }, false);

    /// <summary>Whether one process's request raises the resolution for all (Windows 11; needs a restart after changing).</summary>
    public static bool GlobalRequests
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            return key?.GetValue("GlobalTimerResolutionRequests") is int v && v == 1;
        }
    }

    /// <summary>Coarsest, finest and current resolution (100 ns units).</summary>
    public static (uint Coarsest, uint Finest, uint Current)? Query() => NtQueryTimerResolution(out var max, out var min, out var cur) >= 0 ? (max, min, cur) : null;

    /// <summary>The game keeps its own timer resolution request while minimized or hidden. Other power-throttling choices stay as they are.</summary>
    public static bool KeepInBackground(int pid, long created)
    {
        using var h = ProcessControl.Open(pid, Native.ProcessQueryLimited | Native.ProcessSetInformation);
        if (h is null || !Affinity.Same(h, created)) return false;
        var s = new Native.ProcessPowerThrottling { Version = 1 };
        var size = Marshal.SizeOf<Native.ProcessPowerThrottling>();
        if (!Native.GetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref s, size)) s = new Native.ProcessPowerThrottling { Version = 1 };
        s.Version = 1;
        s.ControlMask |= IgnoreTimerResolution;
        s.StateMask &= ~IgnoreTimerResolution;
        return Native.SetProcessInformation(h, Native.ProcessPowerThrottlingClass, ref s, size);
    }

    [DllImport("ntdll.dll")] static extern int NtSetTimerResolution(uint desired, [MarshalAs(UnmanagedType.U1)] bool set, out uint current);
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
}

/// <summary>
/// Wi-Fi while gamemode is on (wlanapi, a plain C API): no background scans, which cause ping spikes, and the driver's media streaming mode.
/// Both only last while the interface stays connected; the handle stays open meanwhile and the values are set back on restore.
/// </summary>
public static class WifiLatency
{
    const int BackgroundScan = 2, MediaStreaming = 3, InfoSize = 16 + 512 + 4, Connected = 1;
    static IntPtr _handle;

    /// <summary>Returns how many connected interfaces were changed; <paramref name="changed"/> records each one before it changes.</summary>
    public static int Apply(Action<Guid> changed)
    {
        var count = 0;
        foreach (var guid in Interfaces(connectedOnly: true))
        {
            changed(guid);
            Set(guid, BackgroundScan, 0);
            Set(guid, MediaStreaming, 1);
            count++;
        }
        return count;
    }

    public static void Restore(IEnumerable<Guid> interfaces)
    {
        var present = Interfaces(connectedOnly: false).ToHashSet();
        foreach (var guid in interfaces.Where(present.Contains))
        {
            try { Set(guid, BackgroundScan, 1); Set(guid, MediaStreaming, 0); }
            catch (InvalidOperationException ex) { Log.Warn($"Wi-Fi {guid}: {ex.Message}"); } // disconnected since: the driver already reset them
        }
        if (_handle != IntPtr.Zero) { WlanCloseHandle(_handle, IntPtr.Zero); _handle = IntPtr.Zero; }
    }

    static IntPtr Handle()
    {
        if (_handle != IntPtr.Zero) return _handle;
        var error = WlanOpenHandle(2, IntPtr.Zero, out _, out var h);
        if (error == 1062) return IntPtr.Zero; // ERROR_SERVICE_NOT_ACTIVE: WLAN AutoConfig doesn't run without a Wi-Fi adapter
        if (error != 0) throw new InvalidOperationException(Native.Win32Message((int)error));
        return _handle = h;
    }

    /// <summary>The Wi-Fi interfaces; none when WLAN AutoConfig isn't running (a PC without Wi-Fi).</summary>
    static List<Guid> Interfaces(bool connectedOnly)
    {
        var list = new List<Guid>();
        var handle = Handle();
        if (handle == IntPtr.Zero) return list;
        var error = WlanEnumInterfaces(handle, IntPtr.Zero, out var p);
        if (error != 0) throw new InvalidOperationException(Native.Win32Message((int)error));
        try
        {
            var count = Marshal.ReadInt32(p);
            for (var i = 0; i < count; i++)
            {
                var item = p + 8 + i * InfoSize;
                if (connectedOnly && Marshal.ReadInt32(item, 16 + 512) != Connected) continue;
                list.Add(Marshal.PtrToStructure<Guid>(item));
            }
        }
        finally { WlanFreeMemory(p); }
        return list;
    }

    static void Set(Guid guid, int opcode, int value)
    {
        var error = WlanSetInterface(Handle(), ref guid, opcode, 4, ref value, IntPtr.Zero);
        if (error != 0) throw new InvalidOperationException(Native.Win32Message((int)error));
    }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, uint size, ref int data, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);
}

/// <summary>
/// Low-latency audio while gamemode is on: an IAudioClient3 stream on the default playback device, opened at the driver's smallest
/// shared-mode period and kept running, makes the whole shared audio engine on that device use the small period (10 ms otherwise).
/// COM through the vtable with plain delegates (the Native AOT engine can't use built-in COM interop).
/// </summary>
public static class LowLatencyAudio
{
    static readonly Guid MmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), IMmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    const uint ClsctxAll = 0x17;
    static IntPtr _client;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDefaultEndpointFn(IntPtr self, int flow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ActivateFn(IntPtr self, ref Guid iid, uint clsctx, IntPtr parameters, out IntPtr iface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMixFormatFn(IntPtr self, out IntPtr format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnginePeriodFn(IntPtr self, IntPtr format, out uint defaultFrames, out uint fundamental, out uint min, out uint max);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitSharedFn(IntPtr self, uint flags, uint periodFrames, IntPtr format, IntPtr session);

    static T Method<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Method<ReleaseFn>(obj, 2)(obj); }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).");
    }

    public static void Start()
    {
        if (_client != IntPtr.Zero) return;
        CoInitializeEx(IntPtr.Zero, 0); // multithreaded; already initialized is fine
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, client = IntPtr.Zero, format = IntPtr.Zero;
        try
        {
            var clsid = MmDeviceEnumerator;
            var iid = IMmDeviceEnumerator;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out enumerator), "MMDeviceEnumerator");
            Check(Method<GetDefaultEndpointFn>(enumerator, 4)(enumerator, 0, 0, out device), "GetDefaultAudioEndpoint"); // eRender, eConsole
            var audio = IAudioClient3;
            Check(Method<ActivateFn>(device, 3)(device, ref audio, ClsctxAll, IntPtr.Zero, out client), "IAudioClient3");
            Check(Method<GetMixFormatFn>(client, 8)(client, out format), "GetMixFormat");
            Check(Method<EnginePeriodFn>(client, 18)(client, format, out var standard, out _, out var min, out _), "GetSharedModeEnginePeriod");
            if (min >= standard)
            {
                Log.Info("Gamemode: the audio driver has no smaller buffer than the default; low-latency audio not used.");
                return;
            }
            Check(Method<InitSharedFn>(client, 20)(client, 0, min, format, IntPtr.Zero), "InitializeSharedAudioStream");
            Check(Method<NoArgFn>(client, 10)(client), "Start");
            var rate = Marshal.ReadInt32(format, 4); // WAVEFORMATEX.nSamplesPerSec
            Log.Info($"Gamemode: low-latency audio on ({min} frames = {min * 1000.0 / rate:0.##} ms instead of {standard * 1000.0 / rate:0.##} ms).");
            (_client, client) = (client, IntPtr.Zero);
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    public static void Stop()
    {
        if (_client == IntPtr.Zero) return;
        try { Method<NoArgFn>(_client, 11)(_client); } catch { /* device gone */ }
        Release(_client);
        _client = IntPtr.Zero;
        Log.Info("Gamemode: low-latency audio off.");
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);
}
