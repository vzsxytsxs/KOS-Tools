using System.ComponentModel;
using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Corsair;

/// <summary>A Bragi reply: <see cref="Status"/> 0 OK, 1 invalid value, 3 failed or busy, 5 unsupported, 9 refused outside software mode.</summary>
public readonly record struct BragiReply(byte Status, uint Value)
{
    public bool Ok => Status == 0;
}

/// <summary>
/// Corsair's Bragi property protocol over one vendor collection (usage page FF42). Only the two property opcodes exist here, GET (02)
/// and SET (01): no file, handle, reset or pairing opcode can be built, and the operating mode (property 03) takes 1 (hardware) or
/// 2 (software) only, never the two bootloader modes. Frame (Win32 buffer after the report id): [0] target (08 the device itself,
/// 09+ a paired device behind a receiver), [1] opcode, [2..3] property little-endian, [4..7] value little-endian. A reply carries the
/// source in [1] (0 the device, 1 relayed), the opcode in [2], the status in [3] and the value in [4..7].
/// The report id depends on the collection (read on the friend's devices, 2026-11-23): the K65 RGB Mini's 1025-byte report is
/// unnumbered; the Slipstream receiver's 64-byte one is numbered (id 02); a 64-byte unnumbered collection takes id 0 with 02 in front
/// (tried only when Windows refuses the numbered id, which it does before anything reaches the device).
/// </summary>
public sealed class BragiLink
{
    public const byte Get = 0x02, Set = 0x01, SelfTarget = 0x08;
    public const ushort ModeProperty = 0x03;
    const int ErrorInvalidParameter = 87; // what Windows answers to a report id the collection doesn't have
    public const uint HardwareMode = 1, SoftwareMode = 2;

    enum Form { Unnumbered, Numbered, Prefixed }

    readonly IHidTransport _transport;
    Form _form;
    readonly bool _canFallBack;

    public BragiLink(IHidTransport transport)
    {
        _transport = transport;
        var length = transport.Collection.OutputLength;
        _form = length >= 1025 ? Form.Unnumbered : Form.Numbered;
        _canFallBack = length == 64;
    }

    /// <summary>The request after the report id.</summary>
    public static byte[] Payload(byte target, byte opcode, ushort property, uint value = 0)
    {
        if (opcode is not (Get or Set)) throw new ArgumentOutOfRangeException(nameof(opcode), "Only GET and SET are built.");
        return new[] { target, opcode, (byte)property, (byte)(property >> 8), (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
    }

    /// <summary>The reply to <paramref name="opcode"/> from <paramref name="target"/> in a report as read (id at [0]); null when it is another.</summary>
    public static BragiReply? Decode(ReadOnlySpan<byte> r, byte target, byte opcode) =>
        r.Length >= 8 && r[1] == (target == SelfTarget ? 0 : 1) && r[2] == opcode
            ? new BragiReply(r[3], (uint)(r[4] | r[5] << 8 | r[6] << 16 | r[7] << 24)) : null;

    public BragiReply? GetProperty(byte target, ushort property, int timeoutMs = 300) => Exchange(Payload(target, Get, property), target, Get, timeoutMs);

    /// <summary>SET (01). The mode property accepts 1 or 2 only.</summary>
    public BragiReply? SetProperty(byte target, ushort property, uint value, int timeoutMs = 300)
    {
        if (property == ModeProperty && value is not (HardwareMode or SoftwareMode))
            throw new ArgumentOutOfRangeException(nameof(value), "The mode is 1 (hardware) or 2 (software); the others are bootloaders.");
        return Exchange(Payload(target, Set, property, value), target, Set, timeoutMs);
    }

    BragiReply? Exchange(byte[] payload, byte target, byte opcode, int timeoutMs)
    {
        _transport.FlushQueue();
        Send(payload);
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (_transport.Read((int)Math.Max(1, end - Environment.TickCount64)) is not { } r) break;
            if (Decode(r, target, opcode) is { } reply) return reply;
        }
        return null;
    }

    void Send(byte[] payload)
    {
        if (_form == Form.Numbered)
        {
            try { _transport.Write(0x02, payload); return; }
            catch (Win32Exception ex) when (_canFallBack && ex.NativeErrorCode == ErrorInvalidParameter) { _form = Form.Prefixed; }
        }
        _transport.Write(0x00, _form == Form.Prefixed ? new byte[] { 0x02 }.Concat(payload).ToArray() : payload);
    }
}
