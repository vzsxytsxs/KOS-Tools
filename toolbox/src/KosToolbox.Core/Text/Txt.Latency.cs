namespace KosToolbox.Core.Text;

/// <summary>Texts for the Latency page (DPC/ISR per driver, timer delay, hard pagefaults).</summary>
public static partial class Txt
{
    public const string PageLatency = "Latency",
        PageLatencySubtitle = "How long drivers hold the processor (DPC and ISR), interrupt to thread latency and hard pagefaults.";
    public const string LatencyNote = "Start, then use the PC as you normally do for a few minutes (play, listen to music, watch a video) and come back. Measuring only reads: nothing is changed. It keeps running on other pages and stops with Stop or when the toolbox closes.",
        LatencyNeedsAdmin = "Measuring uses a kernel trace, which only an administrator can start.",
        LatencyStart = "Start", LatencyStop = "Stop",
        LatencyTimer = "Interrupt to thread", LatencyDpc = "Highest DPC", LatencyIsr = "Highest ISR", LatencyFaults = "Hard pagefaults",
        LatencyTimerTip = "How long a top-priority thread waited to run after the clock interrupt that expired its timer: the wait audio and games get on top of their own work.",
        LatencyDpcTip = "Deferred procedure call: work a driver does at high priority after its device interrupts. While it runs, no program runs on that processor.",
        LatencyIsrTip = "Interrupt service routine: a driver's first response to its device's interrupt, above everything else on that processor.",
        LatencyFaultsTip = "Memory a program needed that had to be read back from disk (page file or the program's own files). Many while playing mean too little free memory.",
        LatencyDrivers = "Drivers", LatencyCpus = "Processors", LatencyProcesses = "Hard pagefaults by process",
        ColDriver = "Driver", ColProcessor = "Processor", ColIsrs = "ISRs", ColIsrMax = "Highest ISR", ColIsrTotal = "ISR total",
        ColDpcs = "DPCs", ColDpcMax = "Highest DPC", ColDpcTotal = "DPC total", ColProcess = "Process", ColHardFaults = "Hard pagefaults",
        LatencyGood = "No driver ran for 1 ms or more at a time, and threads ran soon after their interrupt: nothing here points to audio dropouts or stutter.",
        LatencyTooShort = "Keep it running for a few minutes while you use the PC: short spikes only show up over time.",
        LatencyNoDriverNames = "Windows didn't give the drivers' addresses, so DPCs and ISRs can't be tied to drivers. The processor totals are still right.",
        LatencyNoFaults = "None so far.";
    public static string LatencyElapsed(TimeSpan t, bool running) => $"{(running ? "Measuring for" : "Measured for")} {t:hh\\:mm\\:ss}";
    public static string Microseconds(double us) => $"{us:N0} µs";
    public static string Milliseconds(double ms) => $"{ms:N1} ms";
    public static string LatencyAverage(double us) => $"average {us:N0} µs";
    public static string LatencyFaultsTop(string process) => $"most: {process}";
    public static string LatencySlowDriver(string driver, double us) =>
        $"{driver} ran for {us:N0} µs at once. From 1 ms (1,000 µs) on, a driver can cause audio dropouts, stutter and input lag: update or roll back the driver of the device that uses it, or disable that device to test.";
    public static string LatencySlowTimer(double us) =>
        $"A top-priority thread waited up to {us:N0} µs to run after its interrupt, though no single driver ran for 1 ms: many short DPCs and ISRs together, or a power-saving processor waking up, can delay audio and games.";
    public static string LatencyStartFailed(string message) => $"The measurement couldn't start: {message}";

    // ---------- Mouse tab (report rate test) ----------
    public const string MouseNote = "Measures the rate your mouse really sends reports at and how steady it is, from the raw input Windows receives. Move the mouse quickly in circles: a slow mouse sends fewer reports. Keep this window in front, because Windows lowers the raw input rate of background windows. Polling rate, sensor and USB port problems show as a lower rate or scattered intervals.";
    public const string MouseCancel = "Cancel", MouseMeasure = "Measure", MouseCollect = "Collect", MouseLog = "Log", MouseLogStop = "Stop (F2)", MouseSave = "Save CSV…", MouseLoad = "Load CSV…",
        MouseDistance = "Distance (cm)", MouseCpi = "CPI", MouseGraphLabel = "Graph", MouseStyleLabel = "Style", MouseStats = "Statistics",
        MouseRangeStart = "From point", MouseRangeEnd = "to point", MouseHideOutliers = "Hide stray points",
        MouseHideOutliersTip = "Keeps the few extreme points out of the view so the line isn't flattened (the statistics still count them).",
        MouseRecordingNoLive = "Recording: nothing is drawn until it stops, so the screen doesn't delay the mouse reports. Press F2 or the button to stop.";
    public static string MouseOutside(int count) => $"{count:N0} point{(count == 1 ? "" : "s")} outside the view.";
    public static string MouseRangeNote(int from, int to, int of) => $"Points {from:N0} to {to:N0} of {of:N0}.";
    public const string MouseMeasureTip = "Measures the CPI: enter a distance, press Measure, then hold the left button, move the mouse exactly that far to the right along a ruler and release.",
        MouseCollectTip = "Press Collect, then hold the left button and move the mouse; releasing the button stops.",
        MouseLogTip = "Records every report until you press F2 or Stop, without holding a button.";
    public const string MouseIdle = "Choose Collect, Log or Measure.",
        MouseArmedCollect = "Hold the left button and move the mouse quickly in circles; release to stop.",
        MouseCollecting = "Collecting… release the left button to stop.",
        MouseMeasuring = "Measuring… move the mouse to the end of the distance, then release.",
        MouseLogging = "Logging every report… press F2 or Stop to finish.",
        MouseNothing = "Nothing was recorded: move the mouse while recording.",
        MouseDistanceInvalid = "Enter the distance in centimeters (up to 200).";
    public static string MouseArmedMeasure(string cm) => $"Hold the left button and move the mouse {cm} cm to the right in a straight line, then release.";
    public static string MouseMeasured(double cpi) => $"About {cpi:N0} CPI (counts per inch). Measure again a few times and average if the result moves.";
    public static string MouseDone(int count) => $"{count:N0} reports recorded.";
    public static string MouseLoaded(int count, string file) => $"{count:N0} reports loaded from {file}.";
    public static string MouseSaved(string file) => $"Saved to {file}.";

    // ---------- Timer tab (Sleep(1) precision) ----------
    public const string TimerNote = "Times many Sleep(1) calls, the wait games use between frames and inputs. With the resolution left as is, it shows what a program that asks for nothing gets now; KOS Governor's timer resolution only reaches every program with \"Global timer resolution requests\" on. Choosing a resolution asks for it during the run only, to compare values: the best one gives an average closest to 1 ms with the smallest spread.";
    public const string TimerAsIs = "As it is now (no request)", TimerRun = "Run", TimerRequestLabel = "Resolution", TimerCountLabel = "Calls", TimerRuns = "Runs";
    public static string TimerNow(double current, double finest, double coarsest) => $"Timer resolution now {current:0.####} ms (finest {finest:0.####} ms, coarsest {coarsest:0.####} ms).";
    public static string TimerRunLabel(string request, double resolution) => $"{request} · {resolution:0.####} ms in effect";
    public static string TimerRunValue(double mean, double min, double max, double std) =>
        $"average {mean:0.0000} ms ({mean - 1:+0.0000;-0.0000} ms), {min:0.0000}–{max:0.0000} ms, spread {std:0.0000} ms";

    // ---------- Ping tab (round trip, jitter and loss) ----------
    public const string PingNote = "Pings a host at a steady pace (a lost reply is waited for up to 1 s) and graphs every round trip (white), the jitter, how much each reply changed from the previous one (grey), and each lost reply (a mark across the graph). Ping your router first: losses or spikes there come from the cable, Wi-Fi or the PC itself. Then a public server shows the route to the internet. Many game servers don't answer pings, so one that never replies isn't necessarily down.";
    public const string PingHostLabel = "Host", PingIntervalLabel = "Every", PingDurationLabel = "For", PingStart = "Start", PingStop = "Stop",
        PingStats = "Statistics", PingGraphX = "Seconds", PingGraphY = "Round trip (ms)", PingRouter = "Router", PingInvalidHost = "Enter a host name or an IP address, without a port.",
        PingRepliesLabel = "Replies", PingRoundTripLabel = "Round trip", PingJitterLabel = "Jitter";
    public static string PingRunning(string host, string address, int sent, int total) => $"Pinging {host}{(host == address ? "" : $" ({address})")}: {sent} of {total}.";
    public static string PingDone(string host, int sent) => $"Done: {sent} pings to {host}.";
    public static string PingStopped(int sent) => $"Stopped after {sent} pings.";
    public static string PingResolveFailed(string host, string message) => $"{host} couldn't be resolved: {message}";
    public static string PingReplies(int received, int sent, double lossPercent) => $"{received} of {sent} · {lossPercent:0.#}% lost";
    public static string PingRoundTrip(double min, double avg, double max) => $"min {min:0.0} ms · average {avg:0.0} ms · max {max:0.0} ms";
    public static string PingJitter(double avg, double max) => $"average {avg:0.00} ms · max {max:0.0} ms";
}
