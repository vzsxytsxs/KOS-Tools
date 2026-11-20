using System.Net;
using System.Runtime.InteropServices;

namespace KosToolbox.Core.Firewall;

/// <summary>A Windows Filtering Platform call failed; <see cref="Code"/> is its FWP_E_* / Win32 code.</summary>
public sealed class WfpException(string what, uint code) : Exception($"{what} failed: 0x{code:X8}{Wfp.Explain(code)}")
{
    public uint Code { get; } = code;
}

public enum WfpAction { Block, Permit }

/// <summary>One condition of a filter: a field, how it is compared, and the value (see the factory methods).</summary>
public sealed record WfpCondition(Guid Field, uint Match, uint Type, object Value)
{
    /// <summary>The program at this path (ALE_APP_ID: the device path Windows keys connections by).</summary>
    public static WfpCondition App(string path) => new(Wfp.ConditionAppId, Wfp.MatchEqual, Wfp.TypeByteBlob, path);
    public static WfpCondition Protocol(byte protocol) => new(Wfp.ConditionProtocol, Wfp.MatchEqual, Wfp.TypeUInt8, protocol);
    public static WfpCondition RemotePort(ushort port) => new(Wfp.ConditionRemotePort, Wfp.MatchEqual, Wfp.TypeUInt16, port);
    public static WfpCondition LocalPort(ushort port) => new(Wfp.ConditionLocalPort, Wfp.MatchEqual, Wfp.TypeUInt16, port);
    public static WfpCondition RemotePorts(ushort from, ushort to) => new(Wfp.ConditionRemotePort, Wfp.MatchRange, Wfp.TypeRange, (from, to));
    public static WfpCondition LocalPorts(ushort from, ushort to) => new(Wfp.ConditionLocalPort, Wfp.MatchRange, Wfp.TypeRange, (from, to));
    /// <summary>The remote address inside this network (an IPv4 or IPv6 address and its prefix length).</summary>
    public static WfpCondition RemoteNetwork(IPAddress address, int prefix) => Network(Wfp.ConditionRemoteAddress, address, prefix);
    /// <summary>A forwarded packet's destination inside this network (the IP forward layers have no remote address).</summary>
    public static WfpCondition DestinationNetwork(IPAddress address, int prefix) => Network(Wfp.ConditionDestinationAddress, address, prefix);
    static WfpCondition Network(Guid field, IPAddress address, int prefix) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        ? new(field, Wfp.MatchEqual, Wfp.TypeV4AddrMask, (address, prefix))
        : new(field, Wfp.MatchEqual, Wfp.TypeV6AddrMask, (address, prefix));
    /// <summary>A process whose token carries this SID (a service's S-1-5-80-... SID): ALE_USER_ID with a security descriptor granting it.</summary>
    public static WfpCondition UserSid(string sid) => new(Wfp.ConditionUserId, Wfp.MatchEqual, Wfp.TypeSecurityDescriptor, sid);
    /// <summary>A Store app (its package SID, S-1-15-2-...).</summary>
    public static WfpCondition Package(string sid) => new(Wfp.ConditionPackageId, Wfp.MatchEqual, Wfp.TypeSid, sid);
    /// <summary>Loopback traffic (the connection never leaves the PC).</summary>
    public static WfpCondition Loopback() => new(Wfp.ConditionFlags, Wfp.MatchFlagsAllSet, Wfp.TypeUInt32, Wfp.FlagIsLoopback);
}

/// <summary>A filter to add. <paramref name="Weight"/> 0..15: the higher one is evaluated first inside the sublayer.</summary>
/// <summary>
/// One filter. In a non-dynamic session it's persistent (kept across restarts), or <paramref name="BootTime"/> (enforced while
/// Windows starts), or <paramref name="UntilRestart"/>: neither, so the filter engine keeps it only until it stops (a restart).
/// </summary>
public sealed record WfpFilter(string Name, Guid Layer, WfpAction Action, byte Weight, IReadOnlyList<WfpCondition> Conditions, bool BootTime = false,
    bool UntilRestart = false);

/// <summary>A filter of ours as the engine has it.</summary>
public sealed record WfpFilterInfo(Guid Key, ulong Id, string Name, Guid Layer, WfpAction Action, bool Persistent, bool BootTime);

/// <summary>A connection the filter engine dropped (or allowed), as its net-event subscription reports it.</summary>
public sealed record WfpNetEvent(DateTime Time, bool Dropped, string? AppDevicePath, byte Protocol, IPAddress? Local, int LocalPort, IPAddress? Remote,
    int RemotePort, bool Inbound, ulong FilterId, bool Loopback);

/// <summary>
/// A session with the Windows filter engine (BFE), through fwpuclnt.dll: plain P/Invoke with blittable structures (the Native AOT
/// engine uses it too), no kernel driver. The structure layouts and every GUID and constant were printed from the Windows SDK
/// headers (10.0.26100, x64) by a C program and are pinned by a unit test. KOS's objects all belong to one provider and one
/// sublayer, so they can be found and removed without touching anyone else's.
/// A dynamic session's objects vanish when it closes (tests, a crash-safe trial); otherwise they are persistent: kept by the
/// engine across restarts of the PC, enforced with no KOS process running.
/// </summary>
public sealed class WfpSession : IDisposable
{
    /// <summary>KOS's provider and sublayer.</summary>
    public static readonly Guid ProviderKey = new("5b6d6a43-2d0f-4a3e-9d7b-1c3f8a2e4b61"), SubLayerKey = new("8a1f7c52-64b3-4f0e-b2a9-3e5d7c9f1a84");
    const string ProviderName = "KOS Firewall";

    IntPtr _engine;
    readonly bool _dynamic;
    IntPtr _events;
    Wfp.NetEventCallback? _callback; // kept: the engine calls it while subscribed
    Guid _subLayer = SubLayerKey;
    Guid? _provider = ProviderKey; // null: filters without a provider, in a sublayer of their own (OpenSubLayer)

    WfpSession(IntPtr engine, bool dynamic) => (_engine, _dynamic) = (engine, dynamic);

    /// <summary>
    /// A dynamic session whose filters go to a sublayer of its own (created here, weighted above KOS Firewall's), without a
    /// provider: KOS Firewall's changes, which work on its provider's filters, never see them. Everything vanishes when it closes.
    /// </summary>
    public static WfpSession OpenSubLayer(Guid key, string name)
    {
        var s = Open(dynamic: true);
        (s._subLayer, s._provider) = (key, null);
        try
        {
            using var mem = new Arena();
            var sub = new Wfp.SubLayer { Key = key, Weight = 0xFFFF };
            sub.Display.Name = mem.String(name);
            Wfp.Check(Wfp.FwpmSubLayerAdd0(s._engine, ref sub, IntPtr.Zero), "Adding the sublayer");
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    public static WfpSession Open(bool dynamic)
    {
        var session = new Wfp.Session { Flags = dynamic ? Wfp.SessionFlagDynamic : 0, TxnWaitTimeoutInMSec = 10_000 };
        var name = Marshal.StringToHGlobalUni(ProviderName);
        try
        {
            session.Display.Name = name;
            Wfp.Check(Wfp.FwpmEngineOpen0(null, Wfp.RpcCAuthnDefault, IntPtr.Zero, ref session, out var engine), "Opening the filter engine");
            return new WfpSession(engine, dynamic);
        }
        finally { Marshal.FreeHGlobal(name); }
    }

    /// <summary>Everything inside is applied together or not at all.</summary>
    public void Transaction(Action work)
    {
        Wfp.Check(Wfp.FwpmTransactionBegin0(_engine, 0), "Starting a filter transaction");
        try { work(); }
        catch { Wfp.FwpmTransactionAbort0(_engine); throw; }
        Wfp.Check(Wfp.FwpmTransactionCommit0(_engine), "Committing the filter transaction");
    }

    /// <summary>KOS's provider and sublayer, created if they aren't there (persistent unless the session is dynamic).</summary>
    public void EnsureProvider()
    {
        using var mem = new Arena();
        var provider = new Wfp.Provider { Key = ProviderKey, Flags = _dynamic ? 0 : Wfp.ProviderFlagPersistent };
        provider.Display.Name = mem.String(ProviderName);
        provider.Display.Description = mem.String("Filters of the KOS Toolbox firewall.");
        var r = Wfp.FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
        if (r != Wfp.EAlreadyExists) Wfp.Check(r, "Adding the provider");
        var sub = new Wfp.SubLayer { Key = SubLayerKey, Flags = _dynamic ? 0 : Wfp.SubLayerFlagPersistent, ProviderKey = mem.Guid(ProviderKey), Weight = 0xFFFE };
        sub.Display.Name = mem.String(ProviderName);
        sub.Display.Description = mem.String("Filters of the KOS Toolbox firewall.");
        r = Wfp.FwpmSubLayerAdd0(_engine, ref sub, IntPtr.Zero);
        if (r != Wfp.EAlreadyExists) Wfp.Check(r, "Adding the sublayer");
    }

    /// <summary>Adds the filter to KOS's sublayer (or the session's own); returns the id the engine gave it.</summary>
    public ulong Add(WfpFilter f)
    {
        using var mem = new Arena();
        var conditions = mem.Alloc(Math.Max(1, f.Conditions.Count) * Marshal.SizeOf<Wfp.Condition>());
        for (var i = 0; i < f.Conditions.Count; i++)
        {
            var c = f.Conditions[i];
            var native = new Wfp.Condition { Field = c.Field, Match = c.Match, Value = Value(c, mem) };
            Marshal.StructureToPtr(native, conditions + i * Marshal.SizeOf<Wfp.Condition>(), false);
        }
        var filter = new Wfp.Filter
        {
            Key = Guid.NewGuid(), ProviderKey = _provider is { } provider ? mem.Guid(provider) : IntPtr.Zero, Layer = f.Layer, SubLayer = _subLayer,
            // A dynamic session's filters can't be persistent; boot-time and persistent are two different filters
            Flags = _dynamic || f.UntilRestart ? 0 : f.BootTime ? Wfp.FilterFlagBootTime : Wfp.FilterFlagPersistent,
            Weight = new Wfp.Value { Type = Wfp.TypeUInt8, UInt8 = f.Weight },
            NumConditions = (uint)f.Conditions.Count, Conditions = f.Conditions.Count == 0 ? IntPtr.Zero : conditions,
            ActionType = f.Action == WfpAction.Block ? Wfp.ActionBlock : Wfp.ActionPermit,
        };
        filter.Display.Name = mem.String(f.Name);
        Wfp.Check(Wfp.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id), $"Adding the filter \"{f.Name}\"");
        return id;
    }

    static Wfp.Value Value(WfpCondition c, Arena mem)
    {
        var v = new Wfp.Value { Type = c.Type };
        switch (c.Type)
        {
            case Wfp.TypeUInt8: v.UInt8 = (byte)c.Value; break;
            case Wfp.TypeUInt16: v.UInt16 = (ushort)c.Value; break;
            case Wfp.TypeUInt32: v.UInt32 = (uint)c.Value; break;
            case Wfp.TypeByteBlob: v.Pointer = mem.AppId((string)c.Value); break;
            case Wfp.TypeSecurityDescriptor: v.Pointer = mem.SidDescriptor((string)c.Value); break;
            case Wfp.TypeSid: v.Pointer = mem.Sid((string)c.Value); break;
            case Wfp.TypeRange:
            {
                var (from, to) = ((ushort, ushort))c.Value;
                var range = mem.Alloc(32); // FWP_RANGE0: two FWP_VALUE0
                Marshal.StructureToPtr(new Wfp.Value { Type = Wfp.TypeUInt16, UInt16 = from }, range, false);
                Marshal.StructureToPtr(new Wfp.Value { Type = Wfp.TypeUInt16, UInt16 = to }, range + 16, false);
                v.Pointer = range;
                break;
            }
            case Wfp.TypeV4AddrMask:
            {
                var (address, prefix) = ((IPAddress, int))c.Value;
                var p = mem.Alloc(8); // FWP_V4_ADDR_AND_MASK: address and mask, host byte order; the address reduced to its network
                var mask = prefix <= 0 ? 0u : 0xFFFFFFFF << (32 - Math.Min(prefix, 32));
                Marshal.WriteInt32(p, unchecked((int)(HostOrder(address) & mask)));
                Marshal.WriteInt32(p + 4, unchecked((int)mask));
                v.Pointer = p;
                break;
            }
            case Wfp.TypeV6AddrMask:
            {
                var (address, prefix) = ((IPAddress, int))c.Value;
                var p = mem.Alloc(17); // FWP_V6_ADDR_AND_MASK: 16 address bytes, then the prefix length
                var bytes = address.GetAddressBytes();
                prefix = Math.Clamp(prefix, 0, 128);
                for (var i = 0; i < 16; i++) bytes[i] &= (byte)(i * 8 >= prefix ? 0 : i * 8 + 8 <= prefix ? 0xFF : 0xFF << (8 - (prefix - i * 8)));
                Marshal.Copy(bytes, 0, p, 16);
                Marshal.WriteByte(p + 16, (byte)prefix);
                v.Pointer = p;
                break;
            }
            default: throw new NotSupportedException($"Condition value type {c.Type}.");
        }
        return v;
    }

    static uint HostOrder(IPAddress v4)
    {
        var b = v4.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    /// <summary>Every filter that belongs to KOS's provider, in any layer (a session of its own sublayer: the filters in it).</summary>
    public List<WfpFilterInfo> Filters()
    {
        var found = new List<WfpFilterInfo>();
        Wfp.Check(Wfp.FwpmFilterCreateEnumHandle0(_engine, IntPtr.Zero, out var handle), "Listing the filters");
        try
        {
            while (true)
            {
                Wfp.Check(Wfp.FwpmFilterEnum0(_engine, handle, 256, out var entries, out var count), "Reading the filters");
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var f = Marshal.PtrToStructure<Wfp.Filter>(Marshal.ReadIntPtr(entries, i * IntPtr.Size));
                        if (_provider is { } provider ? f.ProviderKey == IntPtr.Zero || Marshal.PtrToStructure<Guid>(f.ProviderKey) != provider : f.SubLayer != _subLayer) continue;
                        found.Add(new(f.Key, f.FilterId, Marshal.PtrToStringUni(f.Display.Name) ?? "", f.Layer,
                            f.ActionType == Wfp.ActionBlock ? WfpAction.Block : WfpAction.Permit,
                            (f.Flags & Wfp.FilterFlagPersistent) != 0, (f.Flags & Wfp.FilterFlagBootTime) != 0));
                    }
                }
                finally { if (entries != IntPtr.Zero) Wfp.FwpmFreeMemory0(ref entries); }
                if (count < 256) break;
            }
        }
        finally { Wfp.FwpmFilterDestroyEnumHandle0(_engine, handle); }
        return found;
    }

    public void Remove(ulong filterId)
    {
        var r = Wfp.FwpmFilterDeleteById0(_engine, filterId);
        if (r != Wfp.EFilterNotFound) Wfp.Check(r, "Deleting a filter");
    }

    /// <summary>Deletes every KOS filter, then the sublayer and the provider. Returns how many filters there were.</summary>
    public int RemoveEverything()
    {
        var filters = Filters();
        Transaction(() =>
        {
            foreach (var f in filters) Remove(f.Id);
            var sub = SubLayerKey;
            var r = Wfp.FwpmSubLayerDeleteByKey0(_engine, ref sub);
            if (r is not (Wfp.ESubLayerNotFound or Wfp.ENotFound)) Wfp.Check(r, "Deleting the sublayer");
            var provider = ProviderKey;
            r = Wfp.FwpmProviderDeleteByKey0(_engine, ref provider);
            if (r is not (Wfp.EProviderNotFound or Wfp.ENotFound)) Wfp.Check(r, "Deleting the provider");
        });
        return filters.Count;
    }

    /// <summary>Whether KOS's provider exists (its filters may still be none).</summary>
    public bool ProviderExists()
    {
        var key = ProviderKey;
        var r = Wfp.FwpmProviderGetByKey0(_engine, ref key, out var provider);
        if (r == 0 && provider != IntPtr.Zero) Wfp.FwpmFreeMemory0(ref provider);
        return r == 0;
    }

    /// <summary>Whether the filter engine keeps net events at all (a system-wide option, on by default).</summary>
    public bool CollectsNetEvents
    {
        get
        {
            Wfp.Check(Wfp.FwpmEngineGetOption0(_engine, Wfp.EngineCollectNetEvents, out var value), "Reading the net-event option");
            try { return Marshal.PtrToStructure<Wfp.Value>(value).UInt32 != 0; }
            finally { Wfp.FwpmFreeMemory0(ref value); }
        }
        set
        {
            var v = new Wfp.Value { Type = Wfp.TypeUInt32, UInt32 = value ? 1u : 0 };
            Wfp.Check(Wfp.FwpmEngineSetOption0(_engine, Wfp.EngineCollectNetEvents, ref v), "Setting the net-event option");
        }
    }

    /// <summary>The drops (and allows) still in the filter engine's own event log (about the last hundred, emptied by a restart), oldest first.</summary>
    public List<WfpNetEvent> RecentEvents()
    {
        var found = new List<WfpNetEvent>();
        Wfp.Check(Wfp.FwpmNetEventCreateEnumHandle0(_engine, IntPtr.Zero, out var handle), "Reading the net-event log");
        try
        {
            while (true)
            {
                Wfp.Check(Wfp.FwpmNetEventEnum2(_engine, handle, 256, out var entries, out var count), "Reading the net-event log");
                try
                {
                    for (var i = 0; i < count; i++)
                        if (Wfp.ReadNetEvent(Marshal.ReadIntPtr(entries, i * IntPtr.Size)) is { } e) found.Add(e);
                }
                finally { if (entries != IntPtr.Zero) Wfp.FwpmFreeMemory0(ref entries); }
                if (count < 256) break;
            }
        }
        finally { Wfp.FwpmNetEventDestroyEnumHandle0(_engine, handle); }
        return found;
    }

    /// <summary>Calls <paramref name="handler"/> (on a thread of the engine's) for every dropped connection, until the session closes.</summary>
    public void Subscribe(Action<WfpNetEvent> handler)
    {
        if (_events != IntPtr.Zero) throw new InvalidOperationException("Already subscribed.");
        _callback = (_, e) =>
        {
            try { if (Wfp.ReadNetEvent(e) is { } parsed) handler(parsed); }
            catch { } // never let an exception cross into the engine's thread
        };
        var subscription = new Wfp.NetEventSubscription();
        Wfp.Check(Wfp.FwpmNetEventSubscribe1(_engine, ref subscription, Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero, out _events),
            "Subscribing to net events");
    }

    public void Dispose()
    {
        if (_engine == IntPtr.Zero) return;
        if (_events != IntPtr.Zero) Wfp.FwpmNetEventUnsubscribe0(_engine, _events);
        Wfp.FwpmEngineClose0(_engine);
        (_engine, _events, _callback) = (IntPtr.Zero, IntPtr.Zero, null);
    }

    /// <summary>Unmanaged memory for one call's pointers, freed together.</summary>
    sealed class Arena : IDisposable
    {
        readonly List<IntPtr> _blocks = new(), _appIds = new(), _local = new();

        public IntPtr Alloc(int bytes)
        {
            var p = Marshal.AllocHGlobal(bytes);
            for (var i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
            _blocks.Add(p);
            return p;
        }

        public IntPtr String(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            _blocks.Add(p);
            return p;
        }

        public IntPtr Guid(Guid g)
        {
            var p = Alloc(16);
            Marshal.StructureToPtr(g, p, false);
            return p;
        }

        /// <summary>The engine's own id for the program at this path (a FWP_BYTE_BLOB it allocates).</summary>
        public IntPtr AppId(string path)
        {
            Wfp.Check(Wfp.FwpmGetAppIdFromFileName0(path, out var blob), $"Getting the application id of {path}");
            _appIds.Add(blob);
            return blob;
        }

        /// <summary>A FWP_BYTE_BLOB holding a self-relative security descriptor that grants the SID (how ALE_USER_ID matches a token).</summary>
        public IntPtr SidDescriptor(string sid)
        {
            if (!Wfp.ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:LSD:(A;;CC;;;{sid})", 1, out var sd, out var size))
                throw new WfpException($"Building the security descriptor for {sid}", (uint)Marshal.GetLastWin32Error());
            _local.Add(sd);
            var blob = Alloc(16);
            Marshal.WriteInt32(blob, (int)size);
            Marshal.WriteIntPtr(blob, 8, sd);
            return blob;
        }

        public IntPtr Sid(string sid)
        {
            if (!Wfp.ConvertStringSidToSidW(sid, out var p)) throw new WfpException($"Reading the SID {sid}", (uint)Marshal.GetLastWin32Error());
            _local.Add(p);
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _local) Wfp.LocalFree(p);
            foreach (var p in _blocks) Marshal.FreeHGlobal(p);
            for (var i = 0; i < _appIds.Count; i++) { var p = _appIds[i]; Wfp.FwpmFreeMemory0(ref p); }
        }
    }
}

/// <summary>fwpuclnt.dll: the structures (x64 layouts from the SDK), GUIDs, constants and functions <see cref="WfpSession"/> uses.</summary>
public static class Wfp
{
    // Layers: an outbound connection being authorized, an inbound one being accepted (per IP version)
    public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"),
        LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    // Every packet sent or received (established connections too), per IP version (fwpmu.h, 10.0.26100)
    public static readonly Guid LayerOutboundTransportV4 = new("09e61aea-d214-46e2-9b21-b26b0b2f28c8"), LayerOutboundTransportV6 = new("e1735bde-013f-4655-b351-a49e15762df0"),
        LayerInboundTransportV4 = new("5926dfc8-e3cf-4426-a283-dc393f5d0f9d"), LayerInboundTransportV6 = new("634a869f-fc23-4b90-b0c1-bf620a36ae6f");
    // Packets this PC forwards for others (WSL, Hyper-V and Docker networks, a mobile hotspot)
    public static readonly Guid LayerIpForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4"), LayerIpForwardV6 = new("7b964818-19c7-493a-b71f-832c3684d28c");
    public static readonly Guid ConditionDestinationAddress = new("2d79133b-b390-45c6-8699-acaceaafed33");
    public static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971"), ConditionUserId = new("af043a0a-b34d-4f86-979c-c90371af6e66"),
        ConditionPackageId = new("71bc78fa-f17c-4997-a602-6abb261f351c"), ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"),
        ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957"), ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b"),
        ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b"), ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"),
        ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");

    public const uint SessionFlagDynamic = 1, ProviderFlagPersistent = 1, SubLayerFlagPersistent = 1, FilterFlagPersistent = 1, FilterFlagBootTime = 2;
    public const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    public const uint TypeUInt8 = 1, TypeUInt16 = 2, TypeUInt32 = 3, TypeByteArray16 = 11, TypeByteBlob = 12, TypeSid = 13, TypeSecurityDescriptor = 14,
        TypeV4AddrMask = 0x100, TypeV6AddrMask = 0x101, TypeRange = 0x102;
    public const uint MatchEqual = 0, MatchRange = 5, MatchFlagsAllSet = 6, MatchFlagsAnySet = 7, MatchFlagsNoneSet = 8, MatchNotEqual = 10;
    public const uint FlagIsLoopback = 1;
    public const uint NetEventClassifyDrop = 3, NetEventClassifyAllow = 6;
    public const uint NetEventFlagProtocol = 0x1, NetEventFlagLocalAddr = 0x2, NetEventFlagRemoteAddr = 0x4, NetEventFlagLocalPort = 0x8,
        NetEventFlagRemotePort = 0x10, NetEventFlagAppId = 0x20, NetEventFlagIpVersion = 0x100;
    public const uint EngineCollectNetEvents = 0, EngineNetEventMatchAnyKeywords = 1;
    public const uint RpcCAuthnDefault = 0xFFFFFFFF;
    public const uint EFilterNotFound = 0x80320003, EProviderNotFound = 0x80320005, ESubLayerNotFound = 0x80320007, ENotFound = 0x80320008,
        EAlreadyExists = 0x80320009, EInUse = 0x8032000A;
    public const byte ProtocolIcmp = 1, ProtocolTcp = 6, ProtocolUdp = 17, ProtocolIcmpV6 = 58;

    [StructLayout(LayoutKind.Sequential)] public struct DisplayData { public IntPtr Name, Description; }
    [StructLayout(LayoutKind.Sequential)] public struct ByteBlob { public uint Size; public IntPtr Data; }

    /// <summary>FWP_VALUE0 / FWP_CONDITION_VALUE0: a type and a union (small values in place, the rest behind a pointer).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct Value
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public byte UInt8;
        [FieldOffset(8)] public ushort UInt16;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct Session
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public DisplayData Display;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(36)] public uint TxnWaitTimeoutInMSec;
        [FieldOffset(40)] public uint ProcessId;
        [FieldOffset(48)] public IntPtr Sid;
        [FieldOffset(56)] public IntPtr Username;
        [FieldOffset(64)] public int KernelMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Provider
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public DisplayData Display;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public ByteBlob Data;
        [FieldOffset(56)] public IntPtr ServiceName;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct SubLayer
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public DisplayData Display;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr ProviderKey;
        [FieldOffset(48)] public ByteBlob Data;
        [FieldOffset(64)] public ushort Weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct Condition
    {
        [FieldOffset(0)] public Guid Field;
        [FieldOffset(16)] public uint Match;
        [FieldOffset(24)] public Value Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    public struct Filter
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public DisplayData Display;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr ProviderKey;
        [FieldOffset(48)] public ByteBlob Data;
        [FieldOffset(64)] public Guid Layer;
        [FieldOffset(80)] public Guid SubLayer;
        [FieldOffset(96)] public Value Weight;
        [FieldOffset(112)] public uint NumConditions;
        [FieldOffset(120)] public IntPtr Conditions;
        [FieldOffset(128)] public uint ActionType;   // FWPM_ACTION0: the type, then a GUID (callouts only)
        [FieldOffset(132)] public Guid ActionKey;
        [FieldOffset(152)] public ulong RawContext;
        [FieldOffset(168)] public IntPtr Reserved;
        [FieldOffset(176)] public ulong FilterId;
        [FieldOffset(184)] public Value EffectiveWeight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct NetEventSubscription
    {
        [FieldOffset(0)] public IntPtr EnumTemplate;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public Guid SessionKey;
    }

    // FWPM_NET_EVENT2 (FwpmNetEventSubscribe1 and FwpmNetEventEnum2, Windows 8 and later): the header's fields, then the type and
    // the drop details. Subscribe2 is documented to deliver FWPM_NET_EVENT3, but on Windows 11 26200 its callback got this layout.
    public const int EventFlags = 8, EventIpVersion = 12, EventProtocol = 16, EventLocalAddr = 20, EventRemoteAddr = 36, EventLocalPort = 52,
        EventRemotePort = 54, EventAppIdSize = 64, EventAppIdData = 72, EventType = 104, EventDetails = 112;
    public const int DropFilterId = 0, DropDirection = 24, DropLoopback = 28;
    public const int DirectionIn = 0x3900; // msFwpDirection: FWP_DIRECTION_IN / _OUT (0x3901)

    public delegate void NetEventCallback(IntPtr context, IntPtr netEvent);

    /// <summary>A FWPM_NET_EVENT2 as a record; null for the event types that aren't a classify drop or allow.</summary>
    public static WfpNetEvent? ReadNetEvent(IntPtr e)
    {
        var type = (uint)Marshal.ReadInt32(e, EventType);
        if (type is not (NetEventClassifyDrop or NetEventClassifyAllow)) return null;
        var flags = (uint)Marshal.ReadInt32(e, EventFlags);
        var v6 = (flags & NetEventFlagIpVersion) != 0 && Marshal.ReadInt32(e, EventIpVersion) == 1;
        IPAddress? Address(int offset, uint flag)
        {
            if ((flags & flag) == 0) return null;
            if (!v6) { var a = (uint)Marshal.ReadInt32(e, offset); return new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }); }
            var bytes = new byte[16];
            Marshal.Copy(e + offset, bytes, 0, 16);
            return new IPAddress(bytes);
        }
        string? app = null;
        if ((flags & NetEventFlagAppId) != 0 && Marshal.ReadIntPtr(e, EventAppIdData) is var data && data != IntPtr.Zero)
            app = Marshal.PtrToStringUni(data, Marshal.ReadInt32(e, EventAppIdSize) / 2).TrimEnd('\0');
        var details = Marshal.ReadIntPtr(e, EventDetails);
        return new WfpNetEvent(DateTime.FromFileTimeUtc(Marshal.ReadInt64(e, 0)), type == NetEventClassifyDrop, app,
            (flags & NetEventFlagProtocol) != 0 ? Marshal.ReadByte(e, EventProtocol) : (byte)0,
            Address(EventLocalAddr, NetEventFlagLocalAddr), (flags & NetEventFlagLocalPort) != 0 ? (ushort)Marshal.ReadInt16(e, EventLocalPort) : 0,
            Address(EventRemoteAddr, NetEventFlagRemoteAddr), (flags & NetEventFlagRemotePort) != 0 ? (ushort)Marshal.ReadInt16(e, EventRemotePort) : 0,
            details != IntPtr.Zero && Marshal.ReadInt32(details, DropDirection) == DirectionIn,
            details != IntPtr.Zero ? (ulong)Marshal.ReadInt64(details, DropFilterId) : 0,
            details != IntPtr.Zero && Marshal.ReadInt32(details, DropLoopback) != 0);
    }

    public static void Check(uint result, string what)
    {
        if (result != 0) throw new WfpException(what, result);
    }

    internal static string Explain(uint code) => code switch
    {
        5 => " (access denied: administrator needed)",
        0x6D9 or 0x800706D9 => " (the Base Filtering Engine service isn't running)",
        EAlreadyExists => " (already exists)", EInUse => " (in use)", EFilterNotFound or EProviderNotFound or ESubLayerNotFound or ENotFound => " (not found)",
        _ => "",
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sd, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ConvertStringSidToSidW(string sid, out IntPtr p);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr p);

    const string Dll = "fwpuclnt.dll";
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint FwpmEngineOpen0(string? server, uint authnService, IntPtr authIdentity, ref Session session, out IntPtr engine);
    [DllImport(Dll)] public static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport(Dll)] public static extern uint FwpmEngineGetOption0(IntPtr engine, uint option, out IntPtr value);
    [DllImport(Dll)] public static extern uint FwpmEngineSetOption0(IntPtr engine, uint option, ref Value value);
    [DllImport(Dll)] public static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport(Dll)] public static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport(Dll)] public static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport(Dll)] public static extern uint FwpmProviderAdd0(IntPtr engine, ref Provider provider, IntPtr sd);
    [DllImport(Dll)] public static extern uint FwpmProviderGetByKey0(IntPtr engine, ref Guid key, out IntPtr provider);
    [DllImport(Dll)] public static extern uint FwpmProviderDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport(Dll)] public static extern uint FwpmSubLayerAdd0(IntPtr engine, ref SubLayer subLayer, IntPtr sd);
    [DllImport(Dll)] public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport(Dll)] public static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport(Dll)] public static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport(Dll)] public static extern uint FwpmFilterCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr handle);
    [DllImport(Dll)] public static extern uint FwpmFilterEnum0(IntPtr engine, IntPtr handle, uint requested, out IntPtr entries, out uint returned);
    [DllImport(Dll)] public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engine, IntPtr handle);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);
    [DllImport(Dll)] public static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport(Dll)] public static extern uint FwpmNetEventSubscribe1(IntPtr engine, ref NetEventSubscription subscription, IntPtr callback, IntPtr context, out IntPtr handle);
    [DllImport(Dll)] public static extern uint FwpmNetEventUnsubscribe0(IntPtr engine, IntPtr handle);
    [DllImport(Dll)] public static extern uint FwpmNetEventCreateEnumHandle0(IntPtr engine, IntPtr template, out IntPtr handle);
    [DllImport(Dll)] public static extern uint FwpmNetEventEnum2(IntPtr engine, IntPtr handle, uint requested, out IntPtr entries, out uint returned);
    [DllImport(Dll)] public static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engine, IntPtr handle);
}
