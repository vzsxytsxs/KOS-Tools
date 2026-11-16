using System.Diagnostics;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Text;
using KosToolbox.Core.Tools;
using KosToolbox.Core.Tweaks;
using Microsoft.Win32;
using static KosToolbox.Core.Settings.Hive;
using static KosToolbox.Core.Tweaks.Tweak;

namespace KosToolbox.Core.Catalog;

/// <summary>
/// GPU › AMD Radeon: driver overrides for Radeon cards, AMD Software settings and the Radeon Software Slimmer. They exist only
/// when an AMD display adapter is installed (<see cref="AmdGpus.ClassKeys"/>), so the section doesn't show on other PCs.
/// Only constants and methods here: static initializers across partial files have no guaranteed order.
/// </summary>
public static partial class KosCatalog
{
    const string AmdGroup = "AMD Radeon";
    const string AmdSetup = @"Setup\2-Install-Drivers\AMD\";

    // Safe: telemetry, Chill/Boost and the PCIe link power saving off; ULPS and D3cold overrides removed.
    static readonly string[] AmdSafeZero =
    {
        "ReportAnalytics", "NotifySubscription", "AllowSubscription", "ShowReleaseNotes", "KMD_EnableAmdFendrOptions", "KMD_ChillEnabled",
        "KMD_RadeonBoostEnabled", "EnableAspmL0s", "EnableAspmL1", "EnableAspmL1SS", "EnableSpreadSpectrum", "EnableVcePllSpreadSpectrum",
    };
    static readonly string[] AmdSafeOne = { "DisableAspmL0s", "DisableAspmL1" };
    static readonly string[] AmdSafeDelete = { "EnableUlps", "EnableUlps_NA", "PP_DisableULPS", "KMD_EnableULPS", "KMD_ForceD3ColdSupport" };

    // Experimental (on top of Safe): clock gating, power gating and deep sleep off, highest power state forced.
    static readonly string[] AmdExperimentalOne =
    {
        "PP_Force3DPerformanceMode", "PP_ForceHighDPMLevel", "PP_SclkDeepSleepDisable", "KMD_DeLagEnabled", "DalDisableStutter", "DisableBlockWrite",
        "DisableFBCSupport", "DisableFBCForFullScreenApp", "DisableGfxClockGating", "DisableVceClockGating", "DisableSamuClockGating",
        "DisableRomMGCGClockGating", "DisableGfxCoarseGrainClockGating", "DisableGfxMediumGrainClockGating", "DisableGfxFineGrainClockGating",
        "DisableHdpMGClockGating", "DisableXdmaSclkGating", "DisableRomMediumGrainClockGating", "DisableNbioMediumGrainClockGating",
        "DisableMcMediumGrainClockGating", "IRQMgrDisableIHClockGating", "DisableGfxMGLS", "DisableHdpClockPowerGating", "DisableUVDPowerGating",
        "DisableVCEPowerGating", "DisableAcpPowerGating", "DisableDrmdmaPowerGating", "DisableGfxCGPowerGating", "DisableStaticGfxMGPowerGating",
        "DisableDynamicGfxMGPowerGating", "DisableCpPowerGating", "DisableGDSPowerGating", "DisableXdmaPowerGating", "DisableGFXPipelinePowerGating",
        "DisableQuickGfxMGPowerGating", "DisablePowerGating", "SMU_DisableMmhubPowerGating", "SMU_DisableAthubPowerGating", "DalForceMaxDisplayClock",
        "DalDisableClockGating", "DalDisableDeepSleep", "DalDisableDiv2",
    };
    static readonly string[] AmdExperimentalZero =
    {
        "PP_GfxOffControl", "PP_ThermalAutoThrottlingEnable", "PP_EnableRaceToIdle", "StutterMode", "KMD_FramePacingSupport", "EnableVceSwClockGating",
        "EnableUvdClockGating", "EnableGfxClockGatingThruSmu", "EnableSysClockGatingThruSmu", "DalFineGrainClockGating",
    };
    // Full (on top of Experimental): every other power, clock, thermal and debug override. A name already in the lists above takes this value in Full.
    static readonly string[] AmdFullOne =
    {
        "DisableSAMUPowerGating", "DisableAllClockGating", "DisableSysClockGating", "DALDisableAzClockGating", "DalDisableFBCCompClkGate", "DalDisable48MhzPwrDwn",
        "PP_SkipQueryATPXPowerDown", "PP_SkipClockSlowDown", "PP_DisableClockStretcher", "PP_DisableDBRamping", "PP_DisableDIDT", "PP_DisableDPM",
        "PP_DisableEDCLeakageController", "PP_DisableEngineTransition", "PP_DisableFFC", "PP_DisableLightSleep", "PP_DisablePowerOptimization",
        "PP_DisablePCIePerformanceRequest", "PP_DisableULV", "PP_DisableMemoryTransition", "PP_DisableVoltageTransition", "PP_SclkDpmDisabled", "PP_PcieDpmDisabled",
        "PP_MclkDpmDisabled", "PP_DisablePowerContainment", "PP_DisablePowerControl", "PP_DisablePPM", "PP_DisableThermalManagement", "PP_SocclkDpmDisabled",
        "DisableAspmSWL1", "DisableClkReqSupport", "PP_ThermalOutGpioDisabled", "PP_VRHotGpioDisabled", "DisablePllOffInL1", "DalDisableIdlePowerOptimizations",
        "DalRegKey_DisableMemLowPower", "DalIgnoreDPRefClkSS", "DisableDrmdmaMGCG", "DisableDrmLightSleep", "DisableDrmMGCG", "DisableGfx3DCGCG", "DisableGfx3DCGLS",
        "DisableGfxCGTS", "DisableGfxCGTS_LS", "DisableGfxCoarseGrainLightSleep", "DisableGfxCpLightSleep", "DisableGfxMediumGrainLightSleep", "DisableGfxMGCGPerfMon",
        "DisableGfxPGCondClearStateWA", "DisableGfxRlcLightSleep", "DisableHdpLightSleep", "DisableMcLightSleep", "DisableMcMGCG", "DisableSamuLightSleep",
        "DisableSdmaMGCG", "DisableSdmaMGLS", "DisableVceLightSleep", "DisableXdmaLightSleep", "KMD_DisableDxvaVPClockManagement", "PP_LSCGDisable",
        "PP_UMDPStateDisable", "DisableBifLightSleep", "DisableBifMGCG", "DisableGCEDC", "DisableRlcSmuPGHandshake", "DisableSpuMGCG", "PP_CGCGDisable",
        "PP_DisableMultiUVDStates", "PP_DisableOCLPowerOptimization", "PP_DisableMCDownLoadFeature", "PP_DisableUVDVCEShutDown", "KMD_DisableCgOnShutdownOnly",
        "DisableKernelPowerInterface", "PP_MGCGCGTSSMDisable", "PP_MGCGDisable", "SMU_DisableACG", "DisableDfMGCG", "DalDisallowpstateChange", "DalDisableHubpPG",
        "DalDisableDscPG", "DalDisableDppPG", "DalDisableMpcOtgPG", "PO_DisableClockGating", "PP_ForceMCLKHigh", "DalDPSkipPowerOff", "DisableEarlySamuInit",
        "KMD_DisableDPD", "DalStutterIgnoreFbcForNBp", "PP_BACOSkipHardware", "PP_BACOSkipSMCInterrupt", "KMD_DisableATIDBGPOST", "KMD_ForceTmzDisable", "DalDisableFEC",
        "PP_ACPDPM", "DisableFBCForXDMA", "DisableForceUvdToSclk", "DisableLPTSupport", "DisableLTR", "DisableLTREnforcement", "DisableLTRNoSnoopRequirement",
        "DisableLTRSnoopRequirement", "DisableLTRStrap", "PP_ACDCGpioDisabled", "DisableAcpSupport", "PP_DisableAVFS", "PP_DisableCAC", "PP_DisableODStateInDC",
        "PP_DisableSMUUVDHandshake", "PP_DisableSPLLShutdownSupport", "PP_DisableUVDClientMCTuning", "PP_DisableXDMANaturalDPM", "KMD_DisableFBCRegion",
        "KMD_DisableFBCMixedMode", "DalDisableDSC",
    };
    static readonly string[] AmdFullZero =
    {
        "EnableUlps_NB", "PP_EnablePowerContainment", "PP_MMClockGatingEnabled", "PP_GPUPowerDownEnabled", "PP_EnablePowerSave", "PP_EnablePowerOptimization",
        "PP_EnableLongIdleBACOSupport", "PP_EnableHardwareThermalProtection", "PP_EnablePkgPwrTracking", "PP_EnablePerDPM", "PP_EnableMCLKOptimization",
        "PP_EnableRaceToIdle", "EnablePllOffInL1", "DalEnableIdleRegChecks", "DalReplayLowRefreshRateEnableOpt", "DalFeatureEnableUSB4PowerManagement", "DalPSRPowerOpt",
        "DalEnableFreesyncPowerOptimization", "EnableVPEMemoryShutdown", "KMD_EnableContextBasedPowerManagement", "KMD_StandbyOptimization", "KMD_LongIdleDetectOption",
        "KMD_BacoOnSingleGpu", "PP_ShadowPstateMode", "PP_UserBACOEnable", "PP_SAMUDPM", "PP_SclkThrottleLowNotification", "PP_UVDDPM", "PP_VCEDPM", "DalAllowNBPState",
        "DalAsicFIDLightSleep", "DalMPOSCLKDeepSleepIncrease", "PP_BAMACOEnabled", "KMD_ForceD3ColdAuxPowerSupport", "KMD_ForceD3hotWhenD3coldSupported",
        "KMD_ForceIpsForD3Cold", "PP_EnableACPIOptimization", "PP_EnableBACOSupportFeature", "EnableswGCCGPG", "EnableswGcLbpw", "EnableswGCFakeCGPG",
        "EnableswGCFakeCGCG", "PP_EnableVPEPG", "EnableVPECG", "EnableVPEDpm", "PP_EnableDummyPstateTable", "DalMemLowPowerSupport", "KMD_PXDPPEDynamicPowerStates",
        "KMD_PXS3S4OptimizationSupport", "KMD_WindowedModePowerManagement", "DalWirelessDisplayIdleSupport", "DalPowerGatingLb", "DalPowerGatingPipe", "PipePowerGating",
        "PP_GfxOffControl", "PP_EnableLoadFalconSmcFirmware", "PP_EnableChillOptimization", "DaleAllowCCState", "DalEnableTiledDisplay", "PP_isIcafeEnable",
        "DALExtraMCLatency", "DALExtraReorderingLatency", "DalLogEnable", "KMD_EnableEventLog", "KMD_EnableGpuMessage", "KMD_EnableIoMmuGpuIsolation",
        "KMD_EnableOPM2Interface", "KMD_EnableVirtualDalSupport", "ACGSupported", "DalAllowSelfRefreshControl", "EnableAtomworkDebugger", "EnableCPLIBLog",
        "EnableDfDramScrub", "EnableGPUVirtulizationFeature", "EnableLBPWSupport", "EnablePPSMSupport", "CailDisableGdbSpmProgramming", "CailDisableVbiosRegAccessDebug",
        "CAILEnableACPIOptimization", "DalEnableLTTPR", "KMD_EnableGuestHibernation", "KMD_EnableMesLog", "KMD_CpDebugDump", "PP_MemorySSEnabled", "PP_EngineSSEnabled",
        "PP_BreakOnAssert", "PP_BreakOnWarn", "MemoryBankDowngrade", "BankSwapDowngrade", "DP_ForceSSEnable", "DalEnableHdcp22Debugging", "KMD_EnableDebugVmid",
        "PP_AutoWattManDebug", "KMD_EnableAcpLogging", "KMD_EnableBigPageAppLogging", "KMD_InjectWait3DIdle", "KMD_EnableParaVirtualization", "DalPSRFeatureEnable",
        "KMD_EnableAllocStackTrace", "DalEnableDPMSTFeature", "DalForcePSR", "DalLimitModesOnSclk", "DalSendDPMSNotification", "KMD_DbgIntSupport", "KMD_EnableUvdRTPM",
        "KMD_RTPMEnabled", "PP_BACOUseIOAccess", "KMD_EnableKiqDbg", "PP_ForceHwAvfsEn", "DalLowVCEPerformance", "PP_GeminiLCSSupportFeature", "PP_ODNFeatureEnable",
        "DalSceOledEnable", "DalSceEnable", "EnableVCNMemoryShutdown", "EnableUmschSelfTest", "PipeTilingDowngrade", "GroupSizeDowngrade", "RowTilingDowngrade",
        "SampleSplitDowngrade", "KMD_LoopCountForIdle", "PP_EnableDynamicLTRSupport", "DalEnableOledEdpPowerUpOpt", "KMD_LogLevel_DCS", "KMD_LogLevel_DOPP",
        "KMD_LogLevel_FM_Monitor", "KMD_LogLevel_PIP", "KMD_LogLevel_SDI", "KMD_LogLevel_Stereo", "KMD_GameManagerSupport", "KMD_ActiveLogGroup", "KMD_FramePacingMode",
        "KMD_ActiveCrashRecoverySupport", "KMD_EnableGfxMidCmdPreemption", "KMD_EnableCPFrameSTBLog", "KMD_EnableVirtualGpuRender", "DalForceDSC", "StutterMode",
    };
    static readonly string[] AmdServices = { "AMD Crash Defender Service", "amdfendr", "amdfendrmgr", "amdlog" };

    internal static IEnumerable<Tweak> AmdTweaks()
    {
        var keys = AmdGpus.ClassKeys();
        if (keys.Count == 0) yield break;

        var driver = Choice("amd-radeon-tweaks", "Radeon driver tweaks",
                "Driver overrides for Radeon cards. Safe turns off AMD telemetry, Chill, Boost, PCIe link power saving and spread spectrum, removes the ULPS overrides, " +
                "disables the AMD crash defender and logging services, and turns off Multi-Plane Overlay. Experimental also turns off clock gating, power gating and deep sleep " +
                "and forces the highest power state: more heat and power use, and not advised on RX 7000 and RX 9000 cards. Full (not recommended) adds about 230 more overrides, " +
                "including turning off the driver's thermal management and the hardware thermal protection. Driver default removes all of it. Applied to every AMD adapter.",
                Pages.Gpu, null, AmdGroup, ("default", "Driver default"), ("safe", "Safe"), ("experimental", "Experimental"), ("full", "Full (not recommended)"));
        var full = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MclkDownHyst"] = "2147483647" }; // 0x7FFFFFFF
        foreach (var n in AmdFullOne) full[n] = "1";
        foreach (var n in AmdFullZero) full[n] = "0";
        Target Full(string name, Target experimental) => full.TryGetValue(name, out var v) ? S(v) : experimental;
        var listed = AmdSafeZero.Concat(AmdSafeOne).Concat(AmdSafeDelete).Concat(AmdExperimentalOne).Concat(AmdExperimentalZero).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            foreach (var n in AmdSafeZero) driver.Add(R(LocalMachine, k, n), ("default", Del), ("safe", S("0")), ("experimental", S("0")), ("full", Full(n, S("0"))));
            foreach (var n in AmdSafeOne) driver.Add(R(LocalMachine, k, n), ("default", Del), ("safe", S("1")), ("experimental", S("1")), ("full", Full(n, S("1"))));
            foreach (var n in AmdSafeDelete) driver.Add(R(LocalMachine, k, n), ("default", Any), ("safe", Del), ("experimental", Del), ("full", Full(n, Del)));
            foreach (var n in AmdExperimentalOne) driver.Add(R(LocalMachine, k, n), ("default", Del), ("safe", Del), ("experimental", S("1")), ("full", Full(n, S("1"))));
            foreach (var n in AmdExperimentalZero) driver.Add(R(LocalMachine, k, n), ("default", Del), ("safe", Del), ("experimental", S("0")), ("full", Full(n, S("0"))));
            foreach (var (n, v) in full)
                if (!listed.Contains(n)) driver.Add(R(LocalMachine, k, n), ("default", Del), ("safe", Del), ("experimental", Del), ("full", S(v)));
        }
        foreach (var s in AmdServices)
            driver.AddSpec(RegistrySetting.ServiceStart(s), true, ("default", S("2")), ("safe", S("4")), ("experimental", S("4")), ("full", S("4")));
        driver.Add(R(LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode"), ("default", Del), ("safe", S("5")), ("experimental", S("5")), ("full", S("5")));
        yield return driver.With(t =>
        {
            t.RequiresReboot = true; t.ConfirmationOption = "experimental";
            t.LessSecureOption = "full";
            t.SecurityNote = "Full turns off the driver's thermal management, the hardware thermal protection and the VR-hot and thermal-out signals: nothing slows the card down when it overheats, which can damage it. Watch temperatures, and go back to Safe at the first crash or artifact.";
            t.ConfirmationNote = "Experimental keeps the GPU at full clocks and turns off thermal auto-throttling: watch temperatures, and go back to Safe if you see crashes or artifacts.";
            t.Source = AmdSetup + "AMD Regs.bat; " + AmdSetup + "AMD Dwords (imribiy).bat";
        });

        yield return Toggle("amd-software-extras", "AMD Software extras",
                "Automatic update checks, the tray icon, web content (news and ads), toast notifications and animations of AMD Software: Adrenalin Edition, for this user. Off turns them off.",
                Pages.Gpu, null, AmdGroup)
            .Add(R(CurrentUser, @"Software\AMD\CN", "AutoUpdate"), (On, Del), (Off, S("0")))
            .Add(Sz(CurrentUser, @"Software\AMD\CN", "SystemTray"), (On, Del), (Off, S("false")))
            .Add(Sz(CurrentUser, @"Software\AMD\CN", "AllowWebContent"), (On, Del), (Off, S("false")))
            .Add(Sz(CurrentUser, @"Software\AMD\CN", "CN_Hide_Toast_Notification"), (On, Del), (Off, S("true")))
            .Add(Sz(CurrentUser, @"Software\AMD\CN", "AnimationEffect"), (On, Del), (Off, S("false")))
            .With(t => { t.WindowsDefault = On; t.Source = AmdSetup + "AMD Dwords (imribiy).bat (HKCU\\Software\\AMD\\CN)"; });
    }

    internal static IEnumerable<ActionItem> AmdActions()
    {
        if (AmdGpus.ClassKeys().Count == 0) yield break;
        yield return new ActionItem
        {
            Id = "amd-slimmer", Name = "Radeon Software Slimmer",
            Description = "Removes the parts of AMD Software you don't use (installers, telemetry, the overlay and other components) and its startup entries and tasks. Opens the tool from the KOS tools folder.",
            Page = Pages.Gpu, Group = AmdGroup, ButtonText = Txt.Open, Run = _ => OpenKosTool(@"RadeonSoftwareSlimmer\RadeonSoftwareSlimmer.exe"),
            Source = @"tools\RadeonSoftwareSlimmer",
        };
    }

    /// <summary>Starts a program from the KOS tools folder, only if it is owned by the system (see ToolDiscovery.OwnerProblem).</summary>
    static Task<string> OpenKosTool(string relative)
    {
        var path = Path.Combine(ToolDiscovery.Root, relative);
        if (!File.Exists(path)) throw new FileNotFoundException(Txt.ToolMissing(path), path);
        // Checked even unelevated: the tool asks for administrator itself, so a copy a user planted would run elevated after UAC.
        if (ToolDiscovery.OwnerProblem(path) is { } problem) throw new InvalidOperationException(problem);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! })?.Dispose();
        return Task.FromResult(Txt.Opened);
    }
}
