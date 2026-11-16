using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

public sealed record StabilityRow(string Time, string Title, string Detail, bool Severe);
public sealed record SessionChoice(string Label, GamemodeSession Session);

/// <summary>
/// Hardware › Stability: crash and hardware-error history from the System log (newest first) and the kernel crash dumps, the first
/// place to look after an overclock; and the gamemode session reports KOS Governor saves. Read on every visit.
/// </summary>
public sealed class StabilityPageViewModel : PageViewModel
{
    IReadOnlyList<StabilityRow> _events = Array.Empty<StabilityRow>();
    IReadOnlyList<StabilityRow> _dumps = Array.Empty<StabilityRow>();
    string _summary = "", _dumpsNote = "";
    IReadOnlyList<SessionChoice> _sessions = Array.Empty<SessionChoice>();
    SessionChoice? _session;

    public StabilityPageViewModel() : base("stability", Txt.PageStability, "", Txt.PageStabilitySubtitle)
    {
        OpenDumpsCommand = new RelayCommand(() =>
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var folder = Path.Combine(windows, "Minidump");
            if (!Directory.Exists(folder)) { Toasts.Show(Txt.FolderNotFound(folder), ToastKind.Info); return; }
            Process.Start(new ProcessStartInfo(Path.Combine(windows, "explorer.exe"), ProcessRunner.Quote(folder)) { UseShellExecute = true })?.Dispose();
        });
    }

    public ICommand OpenDumpsCommand { get; }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public IReadOnlyList<StabilityRow> Events { get => _events; private set { Set(ref _events, value); Raise(nameof(NoEvents)); } }
    public bool NoEvents => _events.Count == 0;
    public IReadOnlyList<StabilityRow> Dumps { get => _dumps; private set => Set(ref _dumps, value); }
    public string DumpsNote { get => _dumpsNote; private set => Set(ref _dumpsNote, value); }
    public IReadOnlyList<SessionChoice> Sessions { get => _sessions; private set { Set(ref _sessions, value); Raise(nameof(HasSessions)); Raise(nameof(NoSessions)); } }
    public bool HasSessions => _sessions.Count > 0;
    public bool NoSessions => _sessions.Count == 0;
    public SessionChoice? SelectedSession
    {
        get => _session;
        set { if (Set(ref _session, value)) Raise(nameof(SessionSections)); }
    }
    public IReadOnlyList<SessionSection> SessionSections => _session?.Session.Sections ?? new List<SessionSection>();

    public override async Task RefreshAsync()
    {
        var (events, error) = await Task.Run(() =>
        {
            try { return (Stability.Events(300), (string?)null); }
            catch (Exception ex) { return (new List<StabilityEvent>(), ex.Message); }
        });
        if (error is not null) Log.Warn($"Stability: {error}");
        Summary = error ?? Txt.StabilitySummary(Recent(events));
        // volmgr 46 repeats at every start while the page file is off: a setting, not an incident, so only the newest is listed.
        var dumpFailed = events.FirstOrDefault(e => e.Kind == StabilityKind.DumpFailed);
        Events = events.Where(e => e.Kind != StabilityKind.DumpFailed || e == dumpFailed)
            .Select(e => new StabilityRow(e.Time.ToString("g"), e.Title, e.Detail, e.Severe)).ToList();

        var (dumps, note) = await Task.Run(() =>
        {
            try
            {
                var list = Stability.Dumps();
                return (list, list.Count > 0 ? "" : dumpFailed is not null ? Txt.StabilityDumpsOff : Txt.StabilityNoDumps);
            }
            catch (UnauthorizedAccessException) { return (new List<CrashDump>(), Txt.StabilityDumpsNeedAdmin); }
            catch (Exception ex) { return (new List<CrashDump>(), ex.Message); }
        });
        var sessions = await Task.Run(() =>
        {
            try { return GamemodeSessions.Load(); }
            catch (Exception ex) { Log.Warn($"Stability: the gamemode sessions couldn't be read: {ex.Message}"); return new List<GamemodeSession>(); }
        });
        Sessions = sessions.Select(s => new SessionChoice(Txt.StabilitySessionLabel(s.Start, s.End, s.Game), s)).ToList();
        SelectedSession = Sessions.FirstOrDefault();

        Dumps = dumps.Select(d => new StabilityRow(d.Time.ToString("g"), Path.GetFileName(d.Path), $"{d.Size / 1048576.0:N1} MB · {d.Path}", false)).ToList();
        DumpsNote = note;
    }

    /// <summary>Incidents of the last 30 days, by kind (a clock set to the future once logged events years ahead: those don't count).</summary>
    internal static Dictionary<StabilityKind, int> Recent(IEnumerable<StabilityEvent> events)
    {
        var now = DateTime.Now;
        return events.Where(e => e.Kind != StabilityKind.DumpFailed && e.Time >= now.AddDays(-30) && e.Time <= now.AddDays(1))
            .GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
    }
}
