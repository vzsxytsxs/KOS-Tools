using System.ComponentModel;
using KosToolbox.Core.Peripherals.Batteries;
using KosToolbox.Core.Peripherals.Hid;

namespace KosToolbox.Core.Peripherals.Corsair;

/// <summary>A Bragi product: exact PID, its kind, and for a headset whether this PID is the receiver (the headset is then paired behind it).</summary>
public sealed record BragiModel(ushort ProductId, string Name, PeripheralKind Kind, bool Receiver);

/// <summary>
/// The Corsair K65 RGB Mini (<c>1B1C:1BAF</c>; 1BC3 and 1BCF are OpenLinkHub's other PIDs) and the Virtuoso RGB Wireless headset
/// (<c>0A41</c>/<c>0A43</c> and their receivers <c>0A42</c>/<c>0A44</c>) over the Bragi property protocol (<see cref="BragiLink"/>).
/// Reads and the property meanings below were checked on the friend's devices (2026-11-23) against iCUE: polling 01, brightness 02
/// (0–1000), firmware 13, sleep 0D/0E (the headset), battery 0F/10 (the headset, behind the receiver as target 09). What the devices
/// do not answer (status 05) is not offered: no sidetone (46/47), no auto-off on the keyboard, no polling on the headset. Writes go
/// to the device first in the mode it is in; only when it answers status 09 (refused outside software mode) does the driver switch
/// to software mode (03 = 2), write, and return to hardware mode (03 = 1) in a finally, confirmed by reading it. Mode 3 and 5 (the
/// bootloaders), file, reset and pairing opcodes cannot be built. Every write is read back. Untested: no write has reached a real
/// device, and the K65's onboard lighting write is unknown (it needs a capture of iCUE), so lighting is not offered.
/// </summary>
public sealed class BragiDriver : PeripheralDriver
{
    public static readonly IReadOnlyList<BragiModel> Models = new BragiModel[]
    {
        new(0x1BAF, "Corsair K65 RGB Mini", PeripheralKind.Keyboard, false), new(0x1BC3, "Corsair K65 RGB Mini", PeripheralKind.Keyboard, false),
        new(0x1BCF, "Corsair K65 RGB Mini", PeripheralKind.Keyboard, false),
        new(0x0A41, "Corsair Virtuoso RGB Wireless", PeripheralKind.Headset, false), new(0x0A43, "Corsair Virtuoso RGB Wireless", PeripheralKind.Headset, false),
        new(0x0A42, "Corsair Virtuoso RGB Wireless", PeripheralKind.Headset, true), new(0x0A44, "Corsair Virtuoso RGB Wireless", PeripheralKind.Headset, true),
    };

    const ushort Polling = 0x01, Brightness = 0x02, SleepEnabled = 0x0D, SleepMs = 0x0E, BatteryLevel = 0x0F, BatteryStatus = 0x10, Pid = 0x12,
        FirmwareVersion = 0x13, WinLock = 0x45, WinLockKeys = 0x4A, MaxPolling = 0x96;
    const uint WindowsKeyBit = 0x01;
    static readonly int[] PollingHz = { 125, 250, 500, 1000, 2000, 4000, 8000 }; // codes 1..7
    public const int SleepMinSeconds = 60, SleepMaxSeconds = 90 * 60;

    readonly BragiModel _model;
    readonly HidCollection _collection;
    byte? _target;
    bool _enteredSoftware;
    HashSet<PeripheralSetting> _writable = new();
    bool? _winLock;

    BragiDriver(HidDevice device, BragiModel model, HidCollection collection) : base(device, "Corsair", model.Name, tested: false)
    {
        _model = model;
        _collection = collection;
    }

    bool Headset => _model.Kind == PeripheralKind.Headset;

    /// <summary>The vendor collection: usage page FF42/0001, 1025-byte reports on the keyboard, 64 or 65 on a headset or receiver.</summary>
    public static BragiDriver? Claim(HidDevice d) =>
        d.VendorId == 0x1B1C && Models.FirstOrDefault(m => m.ProductId == d.ProductId) is { } m
        && d.Collections.FirstOrDefault(c => c.UsagePage == 0xFF42 && c.Usage == 0x0001 && (m.Kind == PeripheralKind.Headset
            ? c.OutputLength is 64 or 65 && c.InputLength is 64 or 65 : c.OutputLength == 1025 && c.InputLength == 1025)) is { } col
            ? new BragiDriver(d, m, col) : null;

    public override PeripheralKind Kind => _model.Kind;
    public override PeripheralConnection? Connection => Headset && _model.Receiver ? PeripheralConnection.Receiver : PeripheralConnection.Wired;
    public override bool StoredOnly => true;
    public override IReadOnlySet<PeripheralSetting> Writable => _writable;

    /// <summary>Win Lock as last read (keyboard only); null when unread or the keyboard has none.</summary>
    public bool? WinLockOn => _winLock;

    // ---------- reads ----------

    static uint? Value(BragiLink l, byte target, ushort property) => l.GetProperty(target, property) is { Ok: true } r ? r.Value : null;

    /// <summary>The address of the device itself: the keyboard 08; the headset the first of 09, 0A, 0B, 08 whose PID property says it is a Virtuoso headset (a receiver answers with its own PID on 08).</summary>
    byte? Target(BragiLink l)
    {
        if (!Headset) return BragiLink.SelfTarget;
        foreach (var target in new byte[] { _target ?? 0x09, 0x09, 0x0A, 0x0B, 0x08 }.Distinct())
            if (Value(l, target, Pid) is 0x0A41 or 0x0A43) return _target = target;
        return _target = null;
    }

    public override PeripheralState Read() => With(_collection, true, t =>
    {
        var l = new BragiLink(t);
        _writable = new();
        if (Target(l) is not { } target)
            return new PeripheralState { Name = _model.Name, Battery = Headset ? new PeripheralBattery(null, null, Asleep: true) : null };
        var firmware = Value(l, target, FirmwareVersion) is { } fw ? $"{fw & 0xFF}.{fw >> 8 & 0xFF}.{fw >> 16 & 0xFF}" : null;
        var brightness = Value(l, target, Brightness) is { } b and <= 1000 ? (int)((b + 5) / 10) : (int?)null;
        if (brightness is not null) _writable.Add(PeripheralSetting.Brightness);
        PeripheralPolling? polling = null;
        int? sleep = null;
        if (!Headset)
        {
            var max = Value(l, target, MaxPolling) is { } m and >= 1 and <= 7 ? (int)m : 7;
            if (Value(l, target, Polling) is { } p and >= 1 and <= 7)
            {
                polling = new PeripheralPolling(PollingHz[p - 1], PollingHz.Take(Math.Max(max, (int)p)).ToList());
                _writable.Add(PeripheralSetting.PollingRate);
            }
            _winLock = Value(l, target, WinLock) is { } w and <= 1 && Value(l, target, WinLockKeys) is not null ? w == 1 : null;
        }
        else if (Value(l, target, SleepMs) is { } ms and >= 1000 and <= SleepMaxSeconds * 1000u)
        {
            sleep = (int)(ms / 1000);
            _writable.Add(PeripheralSetting.Sleep);
        }
        return new PeripheralState
        {
            Name = _model.Name, Firmware = firmware, Battery = Headset ? Battery(l, target) : null, Brightness = brightness, Polling = polling, SleepSeconds = sleep,
        };
    });

    public override PeripheralBattery? ReadBattery() => !Headset ? null : With(_collection, true, t =>
    {
        var l = new BragiLink(t);
        return Target(l) is { } target ? Battery(l, target) : new PeripheralBattery(null, null, Asleep: true);
    });

    static PeripheralBattery? Battery(BragiLink l, byte target) =>
        Value(l, target, BatteryLevel) is { } level and <= 1000
            ? new PeripheralBattery(VendorBatteryCodec.BragiPercent(level), Value(l, target, BatteryStatus) is { } s ? s is 1 or 3 : null)
            : new PeripheralBattery(null, null, Asleep: true);

    // ---------- writes ----------

    public override PeripheralState SetPollingRate(int hz)
    {
        var rates = _writable.Contains(PeripheralSetting.PollingRate) ? Array.IndexOf(PollingHz, hz) : -1;
        Require(rates >= 0, "That polling rate isn't one this keyboard reports.");
        return Put((Polling, (uint)rates + 1));
    }

    public override PeripheralState SetBrightness(int percent)
    {
        Require(percent is >= 0 and <= 100, "Brightness goes from 0 to 100 %.");
        return Put((Brightness, (uint)percent * 10));
    }

    /// <summary>The headset's auto-off after this many seconds (whole minutes, 1 to 90); the timer is switched on with it.</summary>
    public override PeripheralState SetSleep(int seconds)
    {
        Require(Headset, "This device has no auto-off.");
        Require(seconds % 60 == 0 && seconds is >= SleepMinSeconds and <= SleepMaxSeconds, "The auto-off goes from 1 to 90 minutes in whole minutes.");
        return Put((SleepEnabled, 1), (SleepMs, (uint)seconds * 1000));
    }

    /// <summary>The keyboard's Win Lock. Turning it on while no key is selected also selects the Windows key (as OpenLinkHub's K65 does).</summary>
    public PeripheralState SetWinLock(bool on)
    {
        Require(!Headset && _winLock is not null, "This device has no Win Lock.");
        if (!on) return Put((WinLock, 0));
        var mask = With(_collection, true, t => Value(new BragiLink(t), BragiLink.SelfTarget, WinLockKeys));
        return mask is 0 ? Put((WinLockKeys, WindowsKeyBit), (WinLock, 1)) : Put((WinLock, 1));
    }

    /// <summary>Writes the properties in order, then reads each back. A refusal or a different read-back is a <see cref="ReadBackException"/>.</summary>
    PeripheralState Put(params (ushort Property, uint Value)[] writes)
    {
        string? problem;
        try { problem = With(_collection, true, t => { var l = new BragiLink(t); return Apply(l, Target(l) ?? throw new PeripheralAsleepException(), writes); }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { problem = AfterRestart(writes); } // a polling change can re-enumerate the device
        var state = Read();
        return problem is null ? state : throw new ReadBackException(problem, state);
    }

    // A SET whose reply never came: the change may have landed (a polling change restarts the keyboard), so it is read back, not called lost.
    static IOException LostReply() => new("The device didn't acknowledge the change.");

    string? Apply(BragiLink l, byte target, (ushort Property, uint Value)[] writes)
    {
        var original = Value(l, target, BragiLink.ModeProperty);
        try
        {
            foreach (var (property, value) in writes)
            {
                var r = l.SetProperty(target, property, value) ?? throw LostReply();
                if (r.Status == 9 && original == BragiLink.HardwareMode && !_enteredSoftware)
                {
                    if (l.SetProperty(target, BragiLink.ModeProperty, BragiLink.SoftwareMode) is not { Ok: true })
                        throw new PeripheralException("The device refused the change outside software mode and didn't accept software mode.");
                    _enteredSoftware = true;
                    r = l.SetProperty(target, property, value) ?? throw LostReply();
                }
                if (!r.Ok) return $"The device refused the change (status {r.Status}).";
            }
        }
        finally { if (_enteredSoftware) LeaveSoftwareMode(l, target); }
        return Verify(l, target, writes);
    }

    /// <summary>Back to hardware mode (03 = 1), confirmed by reading it; the device would otherwise keep its stored effect dark.</summary>
    void LeaveSoftwareMode(BragiLink l, byte target)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (l.SetProperty(target, BragiLink.ModeProperty, BragiLink.HardwareMode) is { Ok: true } && Value(l, target, BragiLink.ModeProperty) == BragiLink.HardwareMode)
            {
                _enteredSoftware = false;
                return;
            }
            Delay(300);
        }
        throw new PeripheralException("The device stayed in software mode; unplug it and plug it back in.");
    }

    static string? Verify(BragiLink l, byte target, (ushort Property, uint Value)[] writes) =>
        writes.All(w => Value(l, target, w.Property) == w.Value) ? null : "The device doesn't show the new value when read back.";

    /// <summary>The handle died (the device restarted): waits for it to answer again, leaves software mode if this driver entered it, then reads back.</summary>
    string? AfterRestart((ushort Property, uint Value)[] writes)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Delay(1000);
            try
            {
                return With(_collection, true, t =>
                {
                    var l = new BragiLink(t);
                    var target = Target(l) ?? throw new PeripheralAsleepException();
                    if (_enteredSoftware) LeaveSoftwareMode(l, target);
                    return Verify(l, target, writes);
                });
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or PeripheralAsleepException) { }
        }
        throw new PeripheralException("The device didn't answer after the change: it may have restarted. Read it again to see what it has.");
    }
}
