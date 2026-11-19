using System.Collections.Concurrent;
using System.Net;
using KosToolbox.Core.Firewall;

namespace KosGovernor.Core;

/// <summary>A program KOS Firewall blocked that has no rule yet: what the popup asks about.</summary>
public sealed record FirewallAsk(string Path, string Name, IPAddress? Remote, int RemotePort, byte Protocol);

/// <summary>The popup's answer; closing it without one is null.</summary>
public enum FirewallAnswer { Allow, AllowUntilRestart, Block }

/// <summary>
/// Which blocked programs to ask about. A program is asked once; while its popup is open or waiting it isn't asked again, and
/// after the popup is closed without an answer it stays blocked and is asked at its next blocked connection after
/// <see cref="Cooldown"/> (a program retries many times a second). Thread-safe.
/// </summary>
public sealed class FirewallAsks
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);
    readonly Dictionary<string, DateTime> _held = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldAsk(string path, DateTime now)
    {
        lock (_held)
        {
            if (_held.TryGetValue(path, out var until) && now < until) return false;
            _held[path] = DateTime.MaxValue;
            return true;
        }
    }

    /// <summary>Allowed or blocked: a rule decides from now on.</summary>
    public void Answered(string path) { lock (_held) _held.Remove(path); }

    public void Dismissed(string path, DateTime now) { lock (_held) _held[path] = now + Cooldown; }
}

/// <summary>
/// KOS Engine's part of KOS Firewall. The filters themselves live in the filter engine and hold without this; here the engine
/// subscribes to its drop events while KOS Firewall is on, asks (through <paramref name="ask"/>, a popup on the tray's thread)
/// about programs the catch-all block stopped, turns an answer into a rule, and turns KOS Firewall off when Windows Firewall is
/// turned on (the user's rule: one firewall at a time).
/// </summary>
internal sealed class FirewallWatch(Action<FirewallAsk> ask, Action<string, string> warn) : IDisposable
{
    public readonly AutoResetEvent Wake = new(false);
    readonly ConcurrentQueue<WfpNetEvent> _events = new();
    readonly FirewallAsks _asks = new();
    WfpSession? _session;
    volatile HashSet<ulong> _catchAll = [], _blocks = [];
    FirewallConfig _config = new();
    DateTime _stamp, _checked, _filtersRead;
    bool[]? _windows;
    bool _failed; // subscribing failed: not retried until the config changes
    bool _unreadable; // the config couldn't be read: the last one read is kept, logged once
    int _blocked;

    public bool On => _config.Enabled;
    public bool Popups => _config.Popups;
    /// <summary>KOS filters in the filter engine when last read; connections KOS's filters blocked since the engine started.</summary>
    public int Filters { get; private set; }
    public int Blocked => _blocked;

    /// <summary>On the engine's thread, every cycle and whenever a drop arrives.</summary>
    public void Sync()
    {
        var now = DateTime.UtcNow;
        if (now - _checked >= TimeSpan.FromSeconds(5))
        {
            _checked = now;
            Reload(now);
            WatchWindowsFirewall();
        }
        if (_config.Enabled && _session is null && !_failed) Subscribe();
        else if (!_config.Enabled && _session is not null) Unsubscribe();
        while (_events.TryDequeue(out var e)) Consider(e, now);
    }

    /// <summary>The config again when the file changed, KOS's filter ids with it (and every minute: the toolbox may rebuild them).</summary>
    void Reload(DateTime now)
    {
        var stamp = File.Exists(KosFirewall.ConfigPath) ? File.GetLastWriteTimeUtc(KosFirewall.ConfigPath) : default;
        var changed = stamp != _stamp;
        if (changed)
        {
            try
            {
                _config = KosFirewall.Load();
                _stamp = stamp; // only after a good read: an unreadable file is read again next time
                _failed = _unreadable = false;
            }
            catch (Exception ex)
            {
                if (!_unreadable) Log.Error("KOS Firewall: reading the config failed; keeping the last one read", ex);
                _unreadable = true;
            }
        }
        if (!_config.Enabled) { Filters = 0; _catchAll = []; _blocks = []; return; }
        if (!changed && now - _filtersRead < TimeSpan.FromMinutes(1)) return;
        _filtersRead = now;
        var filters = KosFirewall.Filters();
        Filters = filters.Count;
        _blocks = filters.Where(f => f.Action == WfpAction.Block).Select(f => f.Id).ToHashSet();
        _catchAll = filters.Where(f => f.Name == FirewallPolicy.DefaultBlockName).Select(f => f.Id).ToHashSet();
    }

    void Subscribe()
    {
        try
        {
            _session = WfpSession.Open(dynamic: true); // only the subscription: it creates no filter
            _session.Subscribe(e =>
            {
                if (!e.Dropped || !_blocks.Contains(e.FilterId)) return;
                Interlocked.Increment(ref _blocked);
                if (e.Inbound || _events.Count >= 256) return;
                _events.Enqueue(e);
                Wake.Set();
            });
            Log.Info("KOS Firewall: watching blocked connections.");
        }
        catch (Exception ex)
        {
            Log.Error("KOS Firewall: subscribing to blocked connections failed", ex);
            _session?.Dispose();
            _session = null;
            _failed = true;
        }
    }

    void Unsubscribe()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>A drop by the catch-all block: a program without a rule. Asked about once (see <see cref="FirewallAsks"/>).</summary>
    void Consider(WfpNetEvent e, DateTime now)
    {
        if (!_config.Popups || !_catchAll.Contains(e.FilterId)) return;
        if (FirewallIdentity.DosPath(e.AppDevicePath) is not { } path || !File.Exists(path)) return;
        if (!_asks.ShouldAsk(path, now)) return;
        // The file name, never its own description: a program could call itself anything (and the elevated engine reads no more of it).
        ask(new FirewallAsk(path, Path.GetFileName(path), e.Remote, e.RemotePort, e.Protocol));
    }

    /// <summary>The popup's answer (tray thread); null = closed (asked again later).</summary>
    public void Answer(FirewallAsk a, FirewallAnswer? answer)
    {
        if (answer is not { } chosen) { _asks.Dismissed(a.Path, DateTime.UtcNow); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Its outbound rule only, as the page sets it: an inbound rule for the same program stays.
                var target = new FirewallRule { Kind = FirewallRuleKind.App, Path = a.Path, Name = a.Name, Direction = FirewallDirection.Out };
                var result = KosFirewall.Change(c =>
                {
                    if (chosen == FirewallAnswer.AllowUntilRestart) FirewallRules.AllowUntilRestart(c, target);
                    else FirewallRules.Choose(c, target, chosen == FirewallAnswer.Allow);
                });
                var what = chosen switch { FirewallAnswer.Allow => "allowed", FirewallAnswer.AllowUntilRestart => "allowed until restart", _ => "blocked" };
                Log.Info($"KOS Firewall: {what} {a.Path} from a popup ({result?.Filters ?? 0} filters).");
                _asks.Answered(a.Path);
                _stamp = default; // read the new filter ids now
                Wake.Set();
            }
            catch (Exception ex)
            {
                Log.Error($"KOS Firewall: saving the answer for {a.Path} failed", ex);
                _asks.Dismissed(a.Path, DateTime.UtcNow);
                warn("KOS Firewall", $"The rule for {a.Name} couldn't be saved: {ex.Message}");
            }
        });
    }

    /// <summary>Windows Firewall turned on (by the toolbox or anywhere else) while KOS Firewall is on: KOS Firewall turns itself off.</summary>
    void WatchWindowsFirewall()
    {
        if (!_config.Enabled) { _windows = null; return; }
        var now = WindowsFirewall.State();
        // Only a change from all off to on counts: right after KOS Firewall is turned on, Windows Firewall is still on for a moment.
        if (_windows is { } before && !before.Any(on => on) && now.Any(on => on))
        {
            Log.Info("Windows Firewall was turned on: KOS Firewall turns itself off.");
            try
            {
                // Turned off from the page, Windows Firewall comes back on first: then the filters are already gone and this removes
                // none, and there's nothing to tell.
                if (KosFirewall.TurnOff(restoreWindows: false) > 0)
                    warn("KOS Firewall is off", "Windows Firewall was turned on, so KOS Firewall turned itself off. Turn it on again on the toolbox's Firewall page.");
            }
            catch (Exception ex) { Log.Error("KOS Firewall: turning off after Windows Firewall was turned on failed", ex); }
            _stamp = default;
            _windows = null;
            return;
        }
        _windows = now;
    }

    public void Dispose()
    {
        Unsubscribe();
        Wake.Dispose();
    }
}
