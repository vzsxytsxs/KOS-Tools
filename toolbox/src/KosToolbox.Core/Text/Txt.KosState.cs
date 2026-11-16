using KosToolbox.Core.KosState;

namespace KosToolbox.Core.Text;

/// <summary>Texts for the KOS state page: what Windows Update, sfc or DISM put back after the KOS changed it.</summary>
public static partial class Txt
{
    public const string PageKosState = "KOS state",
        PageKosStateSubtitle = "What Windows Update, sfc or DISM put back after the KOS changed it.";
    public const string KosStateIntro = "Compares this PC with what every install of this KOS sets, with your KOS profile's defaults, and the other values of the KOS keys with their last good state. Nothing changes until you apply.";
    public const string KosCheckNow = "Check", KosApplyChecked = "Apply checked", KosNothing = "Everything matches the KOS.",
        KosChecking = "Checking…", KosNotChecked = "Not checked yet.";
    public const string KosApplyNote = "Checked items go back to the KOS value (or to the last good state's). Some need a restart. What you leave unchecked in the other changes is kept as the new last good state.";

    public const string KosAbsent = "absent", KosDeleted = "deleted", KosExists = "exists", KosDefaultValue = "(Default)";

    public static string KosSectionTitle(KosSection s) => s switch
    {
        KosSection.Outside => "Changed outside the toolbox",
        KosSection.Other => "Other changes in the KOS keys",
        _ => "Changed in the toolbox",
    };

    public static string KosSectionNote(KosSection s) => s switch
    {
        KosSection.Outside => "Windows Update, sfc, DISM or a manual change. Checked: they go back to the KOS value.",
        KosSection.Other => "Values in the keys the KOS sets that changed since the last good state. Checked: they go back to it.",
        _ => "You changed these on purpose in the toolbox (its history). Check one to put the KOS value back.",
    };

    public static string KosAreaName(KosArea a) => a switch
    {
        KosArea.Settings => "Setting",
        KosArea.Services => "Service",
        KosArea.Tasks => "Scheduled task",
        KosArea.Apps => "App",
        KosArea.Features => "Windows feature",
        KosArea.Files => "File",
        KosArea.Ai => "AI",
        KosArea.Defender => "Defender / SmartScreen",
        _ => "Registry",
    };

    public const string KosRemoved = "removed";
    public static string KosInstalled(string version, bool provisionedOnly) =>
        provisionedOnly ? $"provisioned for new accounts ({version})" : $"installed ({version})";
    public static string KosFeatureState(bool capability, bool on) => capability ? (on ? "installed" : "not installed") : on ? "on" : "off";
    public const string KosCapabilityNotInstalled = "The toolbox doesn't install capabilities: add it in Settings › System › Optional features.";
    public const string KosDefenderSafeMode = "Defender's own files are in use and protected while it runs: they can only be removed with Defender, in Safe Mode (not available yet).";
    public const string KosRestartToFinish = "Windows features and capabilities finish changing after a restart.";

    public static string KosValues(KosSection s, string expected, string current) =>
        s == KosSection.Other ? $"Last good: {expected} · now: {current}" : $"KOS: {expected} · now: {current}";

    public static string KosService(string service, string value) =>
        value.Equals("Start", StringComparison.OrdinalIgnoreCase) ? $"{service} (service)" : $"{service} (service) → {value}";
    public static string KosServiceKey(string service) => $"{service} (service key)";
    public static string KosKey(string name) => $"{name} (key)";
    public static string KosTaskState(string state) => state switch { "absent" => "deleted", "enabled" => "enabled", _ => "disabled" };
    public static string KosShortValue(string v)
    {
        var one = v.Replace('\n', ' ');
        return one.Length == 0 ? "(empty)" : one.Length > 48 ? one[..47] + "…" : one;
    }
    public static string KosSourceDetail(string? source) => string.IsNullOrEmpty(source) ? "" : $"Set by {source}";
    public static string KosCantWriteType(string type) => $"A {type} value can't be written back.";

    public static string KosReferenceText(KosReference? machine, KosReference? user)
    {
        var r = machine ?? user;
        if (r is null) return "No saved state yet: the first check with nothing to fix (or the first fix) saves the last good state for the other changes.";
        var when = DateTime.TryParse(r.Snapshot.CapturedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("g") : r.Snapshot.CapturedAt ?? "?";
        return r.Kind == KosReferenceKind.LastGood ? $"Other changes are compared with the last good state ({when})."
            : $"Other changes are compared with the state the KOS setup left ({when}).";
    }

    public static string KosSummary(int compared, int outside, int other, int toolbox) =>
        outside + other + toolbox == 0 ? $"{compared:N0} items checked. {KosNothing}"
        : $"{compared:N0} items checked: {outside} changed outside the toolbox, {other} other changes, {toolbox} changed in the toolbox.";
    public static string KosUnreadable(int count, bool admin) =>
        $"{count} items couldn't be read{(admin ? "" : ": restart as administrator to check them")}.";
    public const string KosSavedLastGood = "Nothing to fix: saved as the last good state.";
    public static string KosCheckedCount(int count) => count == 1 ? "1 item checked" : $"{count} items checked";
    public static string KosApplied(int ok, int total) => ok == total
        ? $"{total} {(total == 1 ? "item is" : "items are")} back to the KOS state (read back)."
        : $"{ok} of {total} items are back to the KOS state; the others are still different (details in the log).";
    public const string KosHomeTitle = "KOS state", KosHomeReview = "Review",
        KosHomeNote = "Read-only check, run when Windows isn't at the build of the last KOS state check. Review and put them back on that page.";
    public static string KosHomeNotice(string? before, string now, int count)
    {
        var items = count == 1 ? "1 KOS item" : $"{count} KOS items";
        return before is null ? $"{items} differ from what the KOS sets (Windows {now})."
            : $"Windows was updated ({before} → {now}): {items} {(count == 1 ? "was" : "were")} put back or changed.";
    }

    public const string KosDefenderNote = "Defender and SmartScreen items put back to the KOS state lower this PC's protection: files, downloads and apps are no longer scanned or checked by Windows Security.";
    // ---------- Repair Windows (sfc, DISM) ----------
    public const string TabKosCheck = "Check", TabWindowsRepair = "Repair Windows";
    public const string RepairIntro = "Windows' own repair and cleanup tools, one at a time, each with its progress. They need administrator and can take from a few minutes to half an hour. sfc and the component store repair can put back files and settings the KOS removed: check the KOS state afterwards.";
    public const string SfcTitle = "System File Checker", SfcCommand = "sfc /scannow",
        SfcDescription = "Checks the protected Windows files and replaces damaged or missing ones from the component store.";
    public const string DismTitle = "Component store repair", DismCommand = "DISM /Online /Cleanup-Image /RestoreHealth",
        DismDescription = "Checks the component store that sfc repairs from and fixes it with clean files from Windows Update. Run it first when sfc can't fix everything.";
    public const string StoreAnalyzeTitle = "Component store size", StoreAnalyzeCommand = "DISM /Online /Cleanup-Image /AnalyzeComponentStore",
        StoreAnalyzeDescription = "Measures the component store (WinSxS): its real size, what backups and replaced updates take, the last cleanup and whether one is recommended. Changes nothing.";
    public const string StoreCleanupTitle = "Component store cleanup", StoreCleanupCommand = "DISM /Online /Cleanup-Image /StartComponentCleanup",
        StoreCleanupDescription = "Removes component versions that newer updates replaced. The KOS image was cleaned when it was built; every update installed since leaves the old versions behind.";
    public const string StoreResetBaseTitle = "Component store cleanup with ResetBase", StoreResetBaseCommand = "DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase",
        StoreResetBaseDescription = "Also removes the old versions each installed update keeps, which frees the most space. Afterwards the installed updates can't be uninstalled.";
    public const string StoreResetBaseNote = "ResetBase finished: the updates installed so far can no longer be uninstalled.";
    public const string RepairRun = "Run", RepairStarting = "Starting…", RepairCheckNow = "Check KOS state";
    public static string RepairRunning(RepairProgress p) =>
        $"{(p.Percent is { } v ? v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " %" : "…")}{(p.Status is { } s ? " · " + s : "")}";
    public static string RepairFinished(RepairResult r) =>
        r.Error is { } e ? $"Didn't run: {e}"
        : r.ExitCode == -1 ? "Stopped after 4 hours without finishing."
        : r.Summary.Count > 0 ? string.Join("\n", r.Summary)
        : $"Finished (exit code {r.ExitCode}).";
    public const string RepairNoResult = "The repair ended without a result: details are in the toolbox log.";
    public const string DismSourceHint = "DISM couldn't get clean files: it downloads them through Windows Update, which the KOS turns off. Set Windows › System › Windows Update to On or Paused, run DISM again, then set it back.";
    public const string RepairCheckSuggestion = "sfc and DISM can put back what the KOS removed: check the KOS state now.";

    public const string KosApplyFailed = "Applying didn't finish";
    public static string KosApplyError(string message) => $"{KosApplyFailed}: {message}";
}
