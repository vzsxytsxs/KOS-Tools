using System.Runtime.InteropServices;

namespace KosToolbox.Core.Audio;

public enum AudioFlow { Playback, Recording }

/// <summary>A device format: samples per second, valid bits per sample, bits each sample is stored in, channels.</summary>
public sealed record AudioFormat(int SampleRate, int Bits, int Container, int Channels)
{
    public override string ToString() => FormattableString.Invariant($"{Bits} bit, {SampleRate} Hz");
}

/// <summary>An active playback or recording device as Windows has it set up. Null properties: the device doesn't have that setting.</summary>
public sealed record AudioDevice(string Id, string Name, AudioFlow Flow, bool Default, bool DefaultCommunications, float Volume, bool Muted,
    AudioFormat? Format, bool? ExclusiveAllowed, bool? ExclusivePriority, bool? EnhancementsOff);

/// <summary>
/// The format blob Windows keeps per endpoint (PKEY_AudioEngine_DeviceFormat): a 40-byte WAVEFORMATEXTENSIBLE. Pure, tested.
/// </summary>
public static class AudioFormatBlob
{
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71"), Float = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>The format in a blob (WAVEFORMATEX or WAVEFORMATEXTENSIBLE); null if it isn't one.</summary>
    public static AudioFormat? Parse(byte[] b)
    {
        if (b.Length < 16) return null;
        int tag = BitConverter.ToUInt16(b, 0), channels = BitConverter.ToUInt16(b, 2), rate = BitConverter.ToInt32(b, 4), container = BitConverter.ToUInt16(b, 14);
        var valid = tag == 0xFFFE && b.Length >= 40 ? BitConverter.ToUInt16(b, 18) : container;
        if (channels == 0 || rate <= 0 || container == 0) return null;
        return new AudioFormat(rate, valid == 0 ? container : valid, container, channels);
    }

    /// <summary>The channel mask of an extensible blob (speaker positions), or the default for its channel count.</summary>
    public static uint ChannelMask(byte[] b) => BitConverter.ToUInt16(b, 0) == 0xFFFE && b.Length >= 40 ? BitConverter.ToUInt32(b, 20) : DefaultMask(BitConverter.ToUInt16(b, 2));

    static uint DefaultMask(int channels) => channels switch { 1 => 0x4, 2 => 0x3, 4 => 0x33, 6 => 0x3F, 8 => 0x63F, _ => 0 };

    /// <summary>A WAVEFORMATEXTENSIBLE for this format (integer PCM, or 32-bit float when <paramref name="isFloat"/>).</summary>
    public static byte[] Build(AudioFormat f, uint channelMask, bool isFloat = false)
    {
        var b = new byte[40];
        var block = f.Channels * f.Container / 8;
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)f.Channels).CopyTo(b, 2);
        BitConverter.GetBytes(f.SampleRate).CopyTo(b, 4);
        BitConverter.GetBytes(f.SampleRate * block).CopyTo(b, 8);
        BitConverter.GetBytes((ushort)block).CopyTo(b, 12);
        BitConverter.GetBytes((ushort)f.Container).CopyTo(b, 14);
        BitConverter.GetBytes((ushort)22).CopyTo(b, 16);
        BitConverter.GetBytes((ushort)f.Bits).CopyTo(b, 18);
        BitConverter.GetBytes(channelMask).CopyTo(b, 20);
        (isFloat ? Float : Pcm).ToByteArray().CopyTo(b, 24);
        return b;
    }

    /// <summary>What the Sound control panel offers: 16, 24 and 32 bit at the usual rates.</summary>
    public static IEnumerable<AudioFormat> Candidates(int channels)
    {
        foreach (var (bits, container) in new[] { (16, 16), (24, 24), (24, 32), (32, 32) })
            foreach (var rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 })
                yield return new AudioFormat(rate, bits, container, channels);
    }
}

/// <summary>
/// Playback and recording devices through Core Audio (COM, toolbox only: KOS Engine never touches this). Writes go through the
/// policy interface the Sound control panel uses and are read back by the callers. None of this needs administrator.
/// </summary>
public static class AudioDevices
{
    static readonly PropertyKey FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    static readonly PropertyKey DeviceFormat = new(new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    // "Allow applications to take exclusive control" and "Give exclusive mode applications priority" (DWORD, endpoint store)
    static readonly PropertyKey ExclusiveAllow = new(new("b3f8fa53-0004-438e-9003-51a46e139bfc"), 3);
    static readonly PropertyKey ExclusivePriority = new(new("b3f8fa53-0004-438e-9003-51a46e139bfc"), 4);
    // PKEY_AudioEndpoint_Disable_SysFx (DWORD, effects store): 1 = enhancements off; absent where the device has none
    static readonly PropertyKey DisableSysFx = new(new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 5);
    const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;

    public static List<AudioDevice> List()
    {
        var list = new List<AudioDevice>();
        var enumerator = Enumerator();
        foreach (var flow in new[] { AudioFlow.Playback, AudioFlow.Recording })
        {
            Check(enumerator.EnumAudioEndpoints((int)flow, 1, out var devices), "Listing audio devices"); // DEVICE_STATE_ACTIVE
            Check(devices.GetCount(out var count), "Listing audio devices");
            var main = DefaultId(enumerator, flow, RoleConsole);
            var comms = DefaultId(enumerator, flow, RoleCommunications);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var d) != 0) continue;
                try { list.Add(Describe(d, flow, main, comms)); }
                catch (Exception ex) when (ex is COMException or InvalidOperationException) { } // unplugged meanwhile
                finally { Marshal.ReleaseComObject(d); }
            }
            Marshal.ReleaseComObject(devices);
        }
        Marshal.ReleaseComObject(enumerator);
        return list;
    }

    /// <summary>One device as it is now (after a change: the read-back); null if it's gone.</summary>
    public static AudioDevice? Read(string id) => List().FirstOrDefault(d => d.Id == id);

    static string? DefaultId(IMMDeviceEnumerator e, AudioFlow flow, int role)
    {
        if (e.GetDefaultAudioEndpoint((int)flow, role, out var d) != 0) return null;
        try { return d.GetId(out var id) == 0 ? id : null; }
        finally { Marshal.ReleaseComObject(d); }
    }

    static AudioDevice Describe(IMMDevice d, AudioFlow flow, string? main, string? comms)
    {
        Check(d.GetId(out var id), "Reading a device");
        Check(d.OpenPropertyStore(0, out var store), "Reading a device"); // STGM_READ
        try
        {
            var name = ReadString(store, FriendlyName) ?? id;
            var format = ReadBlob(store, DeviceFormat) is { } blob ? AudioFormatBlob.Parse(blob) : null;
            var allow = ReadUInt(store, ExclusiveAllow);
            var priority = ReadUInt(store, ExclusivePriority);
            var policy = Policy();
            uint? sysfx = null;
            try
            {
                var key = DisableSysFx;
                if (policy.GetPropertyValue(id, 1, ref key, out var v) == 0)
                    try { sysfx = v.Type == 19 ? (uint)v.P1.ToInt64() : null; }
                    finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(policy); }
            var (volume, muted) = Volume(d);
            return new AudioDevice(id, name, flow, id == main, id == comms, volume, muted, format,
                allow is null ? null : allow != 0, priority is null ? null : priority != 0, sysfx is null ? null : sysfx != 0);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static (float Volume, bool Muted) Volume(IMMDevice d)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        if (d.Activate(ref iid, 1, IntPtr.Zero, out var o) != 0) return (0, false); // CLSCTX_INPROC_SERVER
        var v = (IAudioEndpointVolume)o;
        try
        {
            v.GetMasterVolumeLevelScalar(out var level);
            v.GetMute(out var mute);
            return (level, mute);
        }
        finally { Marshal.ReleaseComObject(v); }
    }

    /// <summary>The formats the device takes in exclusive mode (what it can be set to), at its current channel count.</summary>
    public static List<AudioFormat> SupportedFormats(string id)
    {
        var found = new List<AudioFormat>();
        var enumerator = Enumerator();
        try
        {
            Check(enumerator.GetDevice(id, out var d), "Opening the device");
            try
            {
                Check(d.OpenPropertyStore(0, out var store), "Reading the device");
                byte[]? current;
                try { current = ReadBlob(store, DeviceFormat); }
                finally { Marshal.ReleaseComObject(store); }
                if (current is null || AudioFormatBlob.Parse(current) is not { } now) return found;
                var mask = AudioFormatBlob.ChannelMask(current);
                var iid = typeof(IAudioClient).GUID;
                Check(d.Activate(ref iid, 1, IntPtr.Zero, out var o), "Opening the device");
                var client = (IAudioClient)o;
                try
                {
                    foreach (var f in AudioFormatBlob.Candidates(now.Channels))
                    {
                        var blob = AudioFormatBlob.Build(f, mask);
                        var p = Marshal.AllocHGlobal(blob.Length);
                        try
                        {
                            Marshal.Copy(blob, 0, p, blob.Length);
                            if (client.IsFormatSupported(1, p, IntPtr.Zero) == 0) found.Add(f); // AUDCLNT_SHAREMODE_EXCLUSIVE
                        }
                        finally { Marshal.FreeHGlobal(p); }
                    }
                }
                finally { Marshal.ReleaseComObject(client); }
                // The current format is always one of them, even while another program holds the device exclusively.
                if (!found.Contains(now)) found.Insert(0, now);
            }
            finally { Marshal.ReleaseComObject(d); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return found;
    }

    /// <summary>Makes the device the default for everything, or for communications only.</summary>
    public static void SetDefault(string id, bool communications)
    {
        WithPolicy(p =>
        {
            if (communications) Check(p.SetDefaultEndpoint(id, RoleCommunications), "Setting the default communications device");
            else
            {
                Check(p.SetDefaultEndpoint(id, RoleConsole), "Setting the default device");
                Check(p.SetDefaultEndpoint(id, RoleMultimedia), "Setting the default device");
            }
        });
    }

    public static void SetVolume(string id, float level) => WithVolume(id, v => { var ctx = Guid.Empty; Check(v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx), "Setting the volume"); });

    public static void SetMute(string id, bool mute) => WithVolume(id, v => { var ctx = Guid.Empty; Check(v.SetMute(mute, ref ctx), "Muting"); });

    /// <summary>The format shared mode runs at (the Sound control panel's "Default Format"): the device format, the mix format in float.</summary>
    public static void SetFormat(string id, AudioFormat format)
    {
        if (format.SampleRate is < 8000 or > 768000 || format.Bits is not (16 or 24 or 32) || format.Container < format.Bits || format.Channels is < 1 or > 8)
            throw new ArgumentException("Not a device format.", nameof(format));
        var current = Read(id)?.Format ?? throw new InvalidOperationException("The device isn't there anymore.");
        if (current.Channels != format.Channels) throw new ArgumentException("The channel count comes from the speaker setup.", nameof(format));
        byte[]? blobNow = null;
        WithStore(id, s => blobNow = ReadBlob(s, DeviceFormat));
        var mask = blobNow is null ? 0x3u : AudioFormatBlob.ChannelMask(blobNow);
        var device = AudioFormatBlob.Build(format, mask);
        var mix = AudioFormatBlob.Build(format with { Bits = 32, Container = 32 }, mask, isFloat: true);
        var pd = Marshal.AllocHGlobal(40);
        var pm = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(device, 0, pd, 40);
            Marshal.Copy(mix, 0, pm, 40);
            WithPolicy(p => Check(p.SetDeviceFormat(id, pd, pm), "Setting the format"));
        }
        finally { Marshal.FreeHGlobal(pd); Marshal.FreeHGlobal(pm); }
    }

    public static void SetExclusive(string id, bool allow, bool priority)
    {
        WithPolicy(p =>
        {
            SetUInt(p, id, 0, ExclusiveAllow, allow ? 1u : 0);
            SetUInt(p, id, 0, ExclusivePriority, allow && priority ? 1u : 0);
        });
    }

    /// <summary>Turns the device's audio enhancements (its effects) off or on; only for devices that have them.</summary>
    public static void SetEnhancementsOff(string id, bool off) => WithPolicy(p => SetUInt(p, id, 1, DisableSysFx, off ? 1u : 0));

    static void SetUInt(IPolicyConfig p, string id, int fxStore, PropertyKey key, uint value)
    {
        var v = new PropVariant { Type = 19, P1 = (IntPtr)value }; // VT_UI4
        Check(p.SetPropertyValue(id, fxStore, ref key, ref v), "Changing a device setting");
    }

    // ---------- helpers ----------
    static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
    static IPolicyConfig Policy() => (IPolicyConfig)new PolicyConfigClass();

    static void WithPolicy(Action<IPolicyConfig> work)
    {
        var p = Policy();
        try { work(p); }
        finally { Marshal.ReleaseComObject(p); }
    }

    static void WithDevice(string id, Action<IMMDevice> work)
    {
        var e = Enumerator();
        try
        {
            Check(e.GetDevice(id, out var d), "Opening the device");
            try { work(d); }
            finally { Marshal.ReleaseComObject(d); }
        }
        finally { Marshal.ReleaseComObject(e); }
    }

    static void WithStore(string id, Action<IPropertyStore> work) => WithDevice(id, d =>
    {
        Check(d.OpenPropertyStore(0, out var s), "Reading the device");
        try { work(s); }
        finally { Marshal.ReleaseComObject(s); }
    });

    static void WithVolume(string id, Action<IAudioEndpointVolume> work) => WithDevice(id, d =>
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        Check(d.Activate(ref iid, 1, IntPtr.Zero, out var o), "Opening the device's volume");
        var v = (IAudioEndpointVolume)o;
        try { work(v); }
        finally { Marshal.ReleaseComObject(v); }
    });

    static string? ReadString(IPropertyStore s, PropertyKey key)
    {
        if (s.GetValue(ref key, out var v) != 0) return null;
        try { return v.Type == 31 ? Marshal.PtrToStringUni(v.P1) : null; } // VT_LPWSTR
        finally { PropVariantClear(ref v); }
    }

    static uint? ReadUInt(IPropertyStore s, PropertyKey key)
    {
        if (s.GetValue(ref key, out var v) != 0) return null;
        try { return v.Type == 19 ? (uint)v.P1.ToInt64() : null; }
        finally { PropVariantClear(ref v); }
    }

    static byte[]? ReadBlob(IPropertyStore s, PropertyKey key)
    {
        if (s.GetValue(ref key, out var v) != 0) return null;
        try
        {
            if (v.Type != 65) return null; // VT_BLOB: size at 8, data at 16
            var size = (int)(uint)v.P1.ToInt64();
            if (size is <= 0 or > 4096 || v.P2 == IntPtr.Zero) return null;
            var bytes = new byte[size];
            Marshal.Copy(v.P2, bytes, 0, size);
            return bytes;
        }
        finally { PropVariantClear(ref v); }
    }

    static void Check(int hr, string what)
    {
        if (hr != 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}" + (hr == unchecked((int)0x88890004) ? " (the device was removed)." : "."));
    }

    // ---------- COM ----------
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey(Guid fmtid, uint pid) { public Guid Fmtid = fmtid; public uint Pid = pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr P1; [FieldOffset(16)] public IntPtr P2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorClass { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant v);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, IntPtr closest);
    }

    /// <summary>The Sound control panel's own interface (undocumented, stable since Windows 7; this is its Windows 10+ IID).</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, out long def, out long min);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropertyKey key, out PropVariant v);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, ref PropertyKey key, ref PropVariant v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
