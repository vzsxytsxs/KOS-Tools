using System.Windows.Input;
using KosToolbox.Core.Persistence;
using KosToolbox.Core.SystemInfo;
using KosToolbox.Core.Text;

namespace KosToolbox.ViewModels;

/// <summary>
/// Diagnose › Slow: a verdict over what's frozen, slow or full now (3 s sample), the Application Hang history and when the sign-in programs
/// started after boot. Read only, no administrator. Rows reuse <see cref="StabilityRow"/> (first column, title, detail, warning).
/// </summary>
public sealed class SlowPageViewModel : PageViewModel
{
    static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(3);
    IReadOnlyList<string> _verdict = Array.Empty<string>();
    IReadOnlyList<StabilityRow> _hung = Array.Empty<StabilityRow>(), _disks = Array.Empty<StabilityRow>(), _hangs = Array.Empty<StabilityRow>(), _startup = Array.Empty<StabilityRow>();
    string _memory = "", _disksNote = "", _hangsNote = "", _boot = "", _startupNote = "", _bootLogNote = "";
    bool _loading;

    public SlowPageViewModel(TweakViewModel? diagnostics = null) : base("slow", Txt.PageSlow, "", Txt.PageSlowSubtitle) // Speed high
    {
        Diagnostics = diagnostics;
        OpenLatencyCommand = new RelayCommand(() => OpenLatency?.Invoke());
        OpenStabilityCommand = new RelayCommand(() => OpenStability?.Invoke());
    }

    public TweakViewModel? Diagnostics { get; }
    public static Action? OpenLatency { get; set; }
    public static Action? OpenStability { get; set; }
    public ICommand OpenLatencyCommand { get; }
    public ICommand OpenStabilityCommand { get; }

    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public IReadOnlyList<string> Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public IReadOnlyList<StabilityRow> Hung { get => _hung; private set { Set(ref _hung, value); Raise(nameof(NoHung)); } }
    public bool NoHung => !_loading && _hung.Count == 0;
    public IReadOnlyList<StabilityRow> Disks { get => _disks; private set => Set(ref _disks, value); }
    public string DisksNote { get => _disksNote; private set => Set(ref _disksNote, value); }
    public string Memory { get => _memory; private set => Set(ref _memory, value); }
    public IReadOnlyList<StabilityRow> Hangs { get => _hangs; private set => Set(ref _hangs, value); }
    public string HangsNote { get => _hangsNote; private set => Set(ref _hangsNote, value); }
    public string Boot { get => _boot; private set => Set(ref _boot, value); }
    public IReadOnlyList<StabilityRow> Startup { get => _startup; private set => Set(ref _startup, value); }
    public string StartupNote { get => _startupNote; private set => Set(ref _startupNote, value); }
    public string BootLogNote { get => _bootLogNote; private set => Set(ref _bootLogNote, value); }

    public override async Task RefreshAsync()
    {
        if (_loading) return; // every visit and the header's Refresh both land here
        IsLoading = true;
        Raise(nameof(NoHung));
        try
        {
            if (Diagnostics is not null) await Diagnostics.RefreshAsync();
            // History and boot first (quick), while the 3 s sample runs alongside.
            var sampleTask = Task.Run(() => Slowness.Sample(SampleLength));
            var (hangs, hangsError) = await Task.Run(() =>
            {
                try { return (Slowness.Group(Slowness.Hangs(), DateTime.Now), (string?)null); }
                catch (Exception ex) { return (new List<HangGroup>(), ex.Message); }
            });
            Hangs = hangs.Select(h => new StabilityRow(h.Last.ToString("g"), h.Program, Txt.SlowHangCount(h.Count, h.Path), h.Count >= Slowness.FrequentHangs)).ToList();
            HangsNote = hangsError ?? (hangs.Count == 0 ? Txt.SlowHangsNone : "");

            var boot = Slowness.BootTime();
            var (programs, refused, fast, bootLogEmpty, startupError) = await Task.Run(() =>
            {
                bool? f = null;
                try { f = Slowness.FastStartup(); } catch (Exception ex) { Log.Warn($"Slow: Fast Startup couldn't be read: {ex.Message}"); }
                try { return (Slowness.StartupPrograms(boot, out var r), r, f, Slowness.BootLogEmpty(), (string?)null); }
                catch (Exception ex) { return (new List<BootProgram>(), 0, f, Slowness.BootLogEmpty(), ex.Message); }
            });
            Boot = Txt.SlowBoot(boot, DateTime.Now - boot, fast);
            Startup = programs.Select(p => new StabilityRow(Txt.SlowAfterBoot(p.SecondsAfterBoot), p.Name, p.Path, false)).ToList();
            StartupNote = string.Join(" ", new[] { startupError ?? (programs.Count == 0 ? Txt.SlowStartupNone : null), refused > 0 ? Txt.SlowStartupRefused(refused) : null, Txt.SlowStartupNote }
                .Where(s => s is not null));
            BootLogNote = bootLogEmpty == true ? Txt.SlowBootLogEmpty : "";

            SlowSample? sample = null;
            try { sample = await sampleTask; }
            catch (Exception ex) { Log.Warn($"Slow: sampling failed: {ex.Message}"); DisksNote = ex.Message; }
            if (sample is not null)
            {
                Hung = sample.Hung.Select(h => new StabilityRow("", h.Process, h.Title, true)).ToList();
                Disks = sample.Disks.Select(d => new StabilityRow(Txt.SlowDiskSpeed(d.Speed), d.Name, Txt.SlowDiskDetail(d), d.Speed is DiskSpeed.Slow or DiskSpeed.VerySlow)).ToList();
                DisksNote = sample.DisksNote ?? "";
                Memory = Txt.SlowMemory(sample.Memory);
            }
            Verdict = Slowness.Verdict(sample?.Hung ?? Array.Empty<HungWindow>(), sample?.Disks ?? Array.Empty<DiskSample>(), sample?.Memory, hangs);
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(NoHung));
        }
    }
}
