namespace KosToolbox.Core.Text;

public static partial class Txt
{
    public static string PlanTechnicalName(Guid id) => $"Power plan {id}";
    public static string SubgroupTechnicalName(Guid id) => $"Subgroup {id}";
    public static string SettingTechnicalName(Guid id) => $"Setting {id}";
    public static string ChoiceTechnicalName(uint value) => $"Value {value}";
    public const string PowerNoSubgroup = "Settings without a subgroup";
    public const string PowerNoMetadata = "Windows doesn't describe the allowed values for this setting, so it's shown read-only.";
    public static string PowerNotAChoice(uint value) => $"{value} isn't one of the values Windows allows for this setting.";
    public static string PowerOutOfRange(uint min, uint max, string units) => $"Enter a value from {min} to {max}{(units.Length == 0 ? "" : " " + units)}.";
    public static string PowerNotAStep(uint step, uint min) => $"The value must go in steps of {step} starting at {min}.";
    public const string PowerAccessDenied = "Windows denied access to the power plan.";
    public static string HibernateFailed(string arg, int code, string output) => $"powercfg /hibernate {arg} failed (exit code {code}): {output}";

    // ---------- Manage plans ----------
    public static string PowerNameInvalid(int max) => $"Enter a name of 1 to {max} characters, not starting with @, without line breaks or spaces at the start or end.";
    public static string PowerDescriptionInvalid(int max) => $"The description can have up to {max} characters, not starting with @, without line breaks or spaces at the start or end.";
    public static string PowerNewIdNotUsed(string call) => $"{call} didn't use the requested plan id.";
    public static string PowerNoOwnValue(Guid setting) => $"The plan has no own value for {setting}.";
    public static string PowerSettingMissing(Guid setting) => $"Windows doesn't list the power setting {setting}.";
    public const string PowerPowPathInvalid = "Choose a .pow file with a full path.";
    public static string PowerPowPathMissing(string path) => $"Not found: {path}";
    public static string PowerExportFailed(string output) => output.Length == 0 ? "powercfg couldn't export the plan." : $"powercfg couldn't export the plan: {output}";
    public const string PowerDeleteActive = "The active plan can't be deleted. Activate another plan first, then delete this one.";
    public const string PlanSaveCopy = "Save a copy", PlanRename = "Rename", PlanDelete = "Delete", PlanImport = "Import…", PlanExport = "Export…",
        PlanRestoreDefaults = "Restore default plans", PlanManagement = "Plan management";
    public const string PlanRestoreDefaultsTooltip = "Windows deletes every plan it didn't ship with (copies, imported and custom plans, including the KOS plans) and resets its own plans to their default settings. Export the plans you want to keep first.";
    public const string SaveCopyPrompt = "Name for the new plan. It starts with the saved settings of this plan (pending changes aren't copied).";
    public static string CopyName(string name) => $"{name} (copy)";
    public const string RenamePlanTitle = "Rename plan", RenamePlanPrompt = "Name of the plan:",
        PlanDescriptionTitle = "Plan description", PlanDescriptionPrompt = "Description of the plan (can be empty):";
    public const string ImportPlanTitle = "Import power plan", ExportPlanTitle = "Export power plan", PowFilter = "Power plan files (*.pow)|*.pow";
    public const string PowerPendingFirst = "Apply or discard the pending changes first.";
    public static string PlanCopied(string name) => $"\"{name}\" created and verified.";
    public static string PlanRenamed(string name) => $"Plan renamed to \"{name}\" and verified.";
    public static string PlanDescriptionChanged(string name) => $"Description of \"{name}\" changed and verified.";
    public static string PlanDeleted(string name) => $"\"{name}\" deleted and verified.";
    public static string PlanImported(string file) => $"{file} imported and verified.";
    public static string PlanExported(string path) => $"Plan exported to {path}.";
    public const string PlansRestored = "Default plans restored.";
    public const string PowerOpNotConfirmed = "Windows didn't confirm the change when it was read again. The page shows what Windows has now.";

    // ---------- Setting (⋯ menu) ----------
    public const string PowerCustomTitle = "Custom value", PowerCustomValue = "Custom value…", PowerDiscardSetting = "Discard change", PowerRemoveValue = "Remove value (use plan default)",
        PowerShowInWindows = "Show in Windows power options", PowerHideInWindows = "Hide in Windows power options", PowerSettingMenu = "Setting options";
    public const string PowerHiddenPill = "Hidden", PowerDiffersPill = "Differs", PowerHiddenTooltip = "Hidden in Windows power options (Control Panel).",
        PowerDefaultPill = "Default", PowerDefaultTooltip = "The plan stores no value here, so this plan's default value applies.";
    public static string PowerSubgroupIsHidden(string name, string group) =>
        $"\"{name}\" can't be shown on its own: its whole subgroup \"{group}\" is hidden in Windows power options.";
    public static string PowerCustomPrompt(string name, string mode) =>
        $"Value for \"{name}\" ({mode}): decimal, or hexadecimal starting with 0x. Values outside the list may not be supported by Windows or drivers.";
    public static string PowerValueRemoved(string name) => $"\"{name}\" now uses this plan's default value (verified).";
    public static string PowerAttributeChanged(string name, bool hidden) => hidden
        ? $"\"{name}\" is now hidden in Windows power options (verified)."
        : $"\"{name}\" now shows in Windows power options (verified).";

    // ---------- Comparison ----------
    public const string PowerCompare = "Compare with other plans", PowerOnlyDifferent = "Only settings that differ";
    public static string PowerCompareLine(string plan, string value) => $"{plan}: {value}";
}
