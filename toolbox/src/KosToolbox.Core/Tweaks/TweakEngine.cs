using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;

namespace KosToolbox.Core.Tweaks;

public sealed record OperationResult(bool Success, string Message, TweakState State);

public enum RestoreTarget { Previous, Original }

/// <summary>
/// Queries, applies, verifies and reverts tweaks. One system operation at a time (avoids
/// conflicts between tweaks that touch the same keys or services).
/// </summary>
public sealed class TweakEngine
{
    public static TweakEngine Instance { get; } = new();

    readonly SemaphoreSlim _gate = new(1, 1);
    public BackupStore Backups { get; } = new();
    public ChangeHistory History { get; } = new();
    public PendingRebootStore Pending { get; } = new();

    /// <summary>Delegate for running elevated jobs; replaceable in tests.</summary>
    public Func<ElevatedJob, CancellationToken, Task<int>> RunElevated { get; set; } = ElevatedRunner.RunAsync;
    /// <summary>Wait between checks; replaceable in tests.</summary>
    public TimeSpan VerifyDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool IsBusy => _gate.CurrentCount == 0;

    public TweakState Query(Tweak tweak)
    {
        try { return TweakEvaluator.Evaluate(tweak); }
        catch (Exception ex)
        {
            Log.Error($"Query {tweak.Id} failed", ex);
            return TweakState.ReadError(ex.Message);
        }
    }

    public bool IsRebootPending(string tweakId) => Pending.Current().ContainsKey(tweakId);

    /// <summary>A system change outside the catalog (BIOS): goes through the same queue, and closing the window in the middle of it warns.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> op)
    {
        await _gate.WaitAsync();
        try { return await op(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// <paramref name="ct"/> only cancels while the operation waits in the queue: interrupting a script or a
    /// write midway would leave the system half-configured. <paramref name="started"/> signals when it leaves the queue.
    /// </summary>
    public async Task<OperationResult> ApplyAsync(Tweak tweak, string option, CancellationToken ct = default, Action? started = null)
    {
        if (tweak.Options.All(o => o.Key != option)) throw new ArgumentException($"Invalid option: {option}");
        await _gate.WaitAsync(ct);
        started?.Invoke();
        ct = CancellationToken.None;
        try
        {
            var label = tweak.Options.First(o => o.Key == option).Label;
            var before = Query(tweak);
            if (before.Status == TweakStatus.Unavailable)
                return new(false, before.Detail ?? Txt.Unavailable, before);
            if (before.Status == TweakStatus.ReadError)
                return new(false, Txt.CantApplyReadError(before.Detail), before);
            if (before.Status == TweakStatus.Matched && before.Option == option)
                return new(true, Txt.AlreadySet(label), before);

            var snapshot = TakeSnapshot(tweak, before);
            Log.Info($"Apply {tweak.Id} -> {option}: started (before: {before.Status} {before.Option})");
            try
            {
                if (tweak.Scripts.TryGetValue(option, out var script))
                {
                    var problem = script.Problem();
                    if (problem is not null) return new(false, problem, before);
                    if (script.RequiresAdmin && !Elevation.IsAdmin)
                        await RunElevated(new ElevatedJob { TweakId = tweak.Id, Option = option }, ct);
                    else
                        await script.RunAsync(ct);
                }
                else
                {
                    var machine = ElevatedJob.MachineWrites(tweak, option);
                    if (machine.Count > 0 && !Elevation.IsAdmin)
                        await RunElevated(new ElevatedJob { TweakId = tweak.Id, Option = option }, ct);
                    else
                        foreach (var (s, v) in machine) Write(s, v);
                    foreach (var (s, v) in UserWrites(tweak, option)) Write(s, v);
                }
            }
            catch (ElevationCancelledException) { throw; } // nothing changed: the saved previous state still holds
            catch { Backups.Record(tweak.Id, snapshot); Remember(tweak, snapshot, option, null); throw; }
            Backups.Record(tweak.Id, snapshot);

            var result = await VerifyAsync(tweak, option, label);
            Remember(tweak, snapshot, option, result.State);
            return result;
        }
        catch (ElevationCancelledException ex) { return Fail(tweak, ex.Message); }
        catch (Exception ex) { Log.Error($"Apply {tweak.Id} -> {option}: failed", ex); return Fail(tweak, ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<OperationResult> RestoreAsync(Tweak tweak, RestoreTarget target, CancellationToken ct = default, Action? started = null)
    {
        var entry = Backups.Get(tweak.Id);
        var snap = target == RestoreTarget.Original ? entry?.Original : entry?.Previous;
        if (snap is null) return new(false, Txt.NoSavedState, Query(tweak));
        return await RestoreAsync(tweak, snap, target.ToString(), ct, started);
    }

    /// <summary>
    /// Undoes a change from the history: puts back the state before it, only if the setting is still as that change left it (the
    /// same option, or the same values when it matched none).
    /// </summary>
    public async Task<OperationResult> UndoAsync(Tweak tweak, HistoryEntry change, CancellationToken ct = default, Action? started = null)
    {
        var now = Query(tweak);
        if (!StillAsLeft(tweak, change, now)) return new(false, Txt.HistoryChangedSince, now);
        return await RestoreAsync(tweak, change.Before, "undo", ct, started);
    }

    /// <summary>The setting is still as <paramref name="change"/> left it: the same option, or the same values when it matched none.</summary>
    internal static bool StillAsLeft(Tweak tweak, HistoryEntry change, TweakState now)
    {
        if (change.After is { } after) return now is { Status: TweakStatus.Matched } && now.Option == after;
        var compared = tweak.Specs.Where(s => change.AfterValues.ContainsKey(s.Setting.Key)).ToList();
        return compared.Count > 0 && compared.All(s => SafeEquals(s.Setting, change.AfterValues[s.Setting.Key]));
    }

    async Task<OperationResult> RestoreAsync(Tweak tweak, Snapshot snap, string what, CancellationToken ct, Action? started)
    {
        // If the saved state was a known option, re-applies the option (includes the scripts and their side effects).
        if (snap.Option is not null) return await ApplyAsync(tweak, snap.Option, ct, started);

        if (!tweak.Restorable || tweak.Scripts.Count > 0)
            return new(false, Txt.CustomStateScriptRestore, Query(tweak));

        await _gate.WaitAsync(ct);
        started?.Invoke();
        ct = CancellationToken.None;
        try
        {
            var wanted = tweak.Specs.Where(s => snap.Values.ContainsKey(s.Setting.Key))
                .Select(s => (Spec: s, Value: snap.Values[s.Setting.Key])).ToList();
            var writes = wanted.Where(w => Restorable(w.Spec, w.Value)).Select(w => (w.Spec.Setting, w.Value)).ToList();
            // The elevated process only writes catalog values; custom values require the toolbox to already be elevated.
            if (!Elevation.IsAdmin && writes.Any(w => w.Setting.MachineWide))
                return new(false, Txt.CustomStateNeedsAdmin, Query(tweak));
            Log.Info($"Restore {tweak.Id} ({what}): started");
            var before = TakeSnapshot(tweak, Query(tweak));
            try { foreach (var (s, v) in writes) Write(s, v); }
            finally { Remember(tweak, before, null, null); }
            var after = Query(tweak);
            var mismatched = wanted.Where(w => !SafeEquals(w.Spec.Setting, w.Value)).Select(w => w.Spec.Setting.Display).ToList();
            if (tweak.RequiresReboot) Pending.Mark(tweak.Id);
            return mismatched.Count == 0
                ? new(true, Txt.ValuesRestored, after)
                : new(false, Txt.SomeValuesNotRestored(string.Join("; ", mismatched)), after);
        }
        catch (ElevationCancelledException ex) { return Fail(tweak, ex.Message); }
        catch (Exception ex) { Log.Error($"Restore {tweak.Id}: failed", ex); return Fail(tweak, ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<string> RunActionAsync(ActionItem action, CancellationToken ct = default, Action? started = null)
    {
        await _gate.WaitAsync(ct);
        started?.Invoke();
        ct = CancellationToken.None;
        try
        {
            Log.Info($"Action {action.Id}: started");
            if (action.Run is not null)
            {
                if (!action.RunElevated || Elevation.IsAdmin) return await action.Run(ct);
                await RunElevated(new ElevatedJob { ActionId = action.Id }, ct);
                if (action.RequiresReboot) Pending.Mark(action.Id);
                Log.Info($"Action {action.Id}: finished (elevated)");
                return action.RequiresReboot ? Txt.DoneRestart : Txt.Done;
            }
            var script = action.Script ?? throw new InvalidOperationException(Txt.ActionNotImplemented);
            var problem = script.Problem();
            if (problem is not null) throw new InvalidOperationException(problem);
            bool nonZero;
            if (script.RequiresAdmin && !Elevation.IsAdmin)
                nonZero = await RunElevated(new ElevatedJob { ActionId = action.Id }, ct) == ElevatedJob.ExitScriptCode;
            else
                nonZero = (await script.RunAsync(ct)).ExitCode != 0;
            if (action.RequiresReboot) Pending.Mark(action.Id);
            var text = action.RequiresReboot ? Txt.DoneRestart : Txt.Done;
            Log.Info($"Action {action.Id}: finished{(nonZero ? " (script returned a nonzero exit code)" : "")}");
            return nonZero ? text + " " + Txt.ScriptNonZero : text;
        }
        finally { _gate.Release(); }
    }

    static IEnumerable<(ISetting Setting, string? Value)> UserWrites(Tweak tweak, string option) =>
        tweak.Specs
            .Where(s => !s.Setting.MachineWide && s.Targets.TryGetValue(option, out var t) && !t.Ignore && s.Setting.Unavailable() is null)
            .Select(s => (s.Setting, s.Targets[option].Value));

    static void Write(ISetting s, string? v)
    {
        s.Write(v);
        Log.Info($"{s.Display} = {v ?? "(deleted)"}");
    }

    /// <summary>Adds the change to the history with what is read now (<paramref name="after"/> when already read). Never fails the change.</summary>
    void Remember(Tweak tweak, Snapshot before, string? option, TweakState? after)
    {
        try
        {
            after ??= Query(tweak);
            History.Add(new HistoryEntry
            {
                At = DateTime.Now, TweakId = tweak.Id, Before = before, Option = option,
                After = after.Status == TweakStatus.Matched ? after.Option : null,
                AfterValues = TakeSnapshot(tweak, after).Values,
                Confirmed = option is null || after.Status == TweakStatus.Matched && after.Option == option,
            });
        }
        catch (Exception ex) { Log.Warn($"History: {tweak.Id} wasn't recorded: {ex.Message}"); }
    }

    static Snapshot TakeSnapshot(Tweak tweak, TweakState current)
    {
        var snap = new Snapshot { TakenAt = DateTime.Now, Option = current.Status == TweakStatus.Matched ? current.Option : null };
        foreach (var spec in tweak.Specs.Where(s => s.Setting.Unavailable() is null))
        {
            try { snap.Values[spec.Setting.Key] = spec.Setting.Read(); }
            catch (Exception ex) { Log.Warn($"Backup skipped {spec.Setting.Display}: {ex.Message}"); }
        }
        return snap;
    }

    /// <summary>Re-reads the system until the option is confirmed (scripts via PowerRun may write with a delay).</summary>
    async Task<OperationResult> VerifyAsync(Tweak tweak, string option, string label)
    {
        TweakState state = Query(tweak);
        for (var i = 0; i < 10 && !(state.Status == TweakStatus.Matched && state.Option == option); i++)
        {
            await Task.Delay(VerifyDelay);
            state = Query(tweak);
        }
        if (state.Status == TweakStatus.Matched && state.Option == option)
        {
            if (tweak.RequiresReboot) Pending.Mark(tweak.Id);
            var msg = tweak.RequiresReboot ? Txt.AppliedRestart(label) : Txt.AppliedVerified(label);
            Log.Info($"Apply {tweak.Id} -> {option}: verified");
            return new(true, msg, state);
        }
        var detail = state.Detail is null ? "" : $"\n{state.Detail}";
        Log.Warn($"Apply {tweak.Id} -> {option}: not confirmed ({state.Status}).{detail}");
        return new(false, Txt.NotConfirmed(Txt.StatusName(state.Status, tweak.Options.FirstOrDefault(o => o.Key == state.Option)?.Label)), state);
    }

    OperationResult Fail(Tweak tweak, string message) => new(false, message, Query(tweak));

    /// <summary>
    /// Saved values come from backups.json and history.json, which the user's unelevated programs can change: a machine-wide setting
    /// only gets back a value one of its own options writes (a custom value there is reported as not restored).
    /// </summary>
    internal static bool Restorable(SettingSpec spec, string? value) =>
        !spec.Setting.MachineWide || spec.Targets.Values.Any(t => !t.Ignore && spec.Setting.ValueEquals(t.Value, value));

    static bool SafeEquals(ISetting s, string? expected)
    {
        try { return s.ValueEquals(s.Read(), expected); } catch { return false; }
    }
}
