using System.Diagnostics;
using KosToolbox.Core.Execution;
using KosToolbox.Core.Interop;
using KosToolbox.Core.Text;
using KosToolbox.Core.Settings;
using KosToolbox.Core.Tweaks;
using Microsoft.Win32;
using static KosToolbox.Core.Settings.Hive;
using static KosToolbox.Core.Tweaks.Tweak;

namespace KosToolbox.Core.Catalog;

public static class Pages
{
    public const string Windows = "windows", Power = "power", Tools = "tools", Gpu = "gpu";
    public const string Performance = "Performance", Gaming = "Gaming", Visuals = "Visuals", SystemTab = "System", Peripherals = "Peripherals", Security = "Security", Connectivity = "Connectivity";
    /// <summary>Order of the categories on the Windows page.</summary>
    public static readonly string[] WindowsTabs = { Performance, Gaming, Visuals, SystemTab, Peripherals, Security, Connectivity };
}

/// <summary>
/// Every tweak and action of the toolbox (UI text in English). Each item cites the KOS project files its values came from,
/// or the documented key/policy they come from; no value was made up. To add a tweak, create an entry here: the UI, search,
/// backup and verification use this definition.
/// </summary>
public static partial class KosCatalog
{
    const string WinFeatures = @"Setup\3-Support\Windows Features\";
    const string Bats = @"Setup\4-Tweaking\Bats\";
    const string Fixes = @"Setup\3-Support\Troubleshooting - Fixes\";
    const string ProfileScript = WinFeatures + "Security Profile Basic-Advanced.cmd";
    const string Mm = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string Kernel = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    const string MemMgmt = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    const string Dg = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    const string DgPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";
    const string SysPolicy = @"SOFTWARE\Policies\Microsoft\Windows\System";
    const string Usbhub3 = @"SYSTEM\CurrentControlSet\Services\USBHUB3\Parameters";
    const string PsPolicy = @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";
    const string WuUx = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    const string DW = "DWord";
    static readonly string[] GameBarExes = { "GameBarPresenceWriter.exe", "bcastdvr.exe", "GamePanel.exe", "GameBar.exe", "GameBarFTServer.exe", "GameBarFT.exe", "XboxGameBarWidgets.exe" };
    static readonly string[] SearchExes = { "SearchApp.exe", "SearchUI.exe", "SearchHost.exe" };

    const string ExplorerAdvanced = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    static Target S(string v) => Target.Set(v);
    static readonly Target Del = Target.Delete, Any = Target.Any;
    static RegistrySetting R(Hive h, string path, string name, RegistryValueKind kind = RegistryValueKind.DWord) => new(h, path, name, kind);
    static RegistrySetting Sz(Hive h, string path, string name) => new(h, path, name, RegistryValueKind.String);

    static string? IntelOnly() => CpuVendor() == "GenuineIntel" ? null : Txt.IntelOnly;
    static string? AmdOnly() => CpuVendor() == "AuthenticAMD" ? null : Txt.AmdOnly;
    static string? CpuVendor()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return k?.GetValue("VendorIdentifier") as string;
    }

    public static IReadOnlyList<Tweak> Tweaks { get; } = BuildTweaks().Concat(NvidiaTweaks()).Concat(AmdTweaks()).ToList();
    public static IReadOnlyList<ActionItem> Actions { get; } = BuildActions().Concat(NvidiaActions()).Concat(AmdActions()).ToList();
    public static Tweak? Find(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    static List<Tweak> BuildTweaks() => new()
    {
        // ======================= WINDOWS › SYSTEM, VISUALS AND PERIPHERALS =======================
        Choice("windows-update", "Windows Update",
                "Windows Update mode. Paused keeps Windows Update on but uses its own pause, set to end in 2038, and stops automatic installs; Resume updates in Settings or On undoes it. Off also turns off the Update Health service, Delivery Optimization peering and Store app auto-updates. KOS reapplies the Off and Store modes at every startup (bin\\3\\windows-update-policy.cmd).",
                Pages.Windows, Pages.SystemTab, "Updates",
                ("on", Txt.Enabled), ("manual", "Manual, no drivers"), ("paused", "Paused until 2038"), ("store", "Microsoft Store only"), ("off", Txt.Disabled))
            .Add(Sz(LocalMachine, @"SOFTWARE\TheKOS", "forceDisableWup"), ("on", S("0")), ("manual", S("0")), ("paused", S("paused")), ("store", S("store")), ("off", S("1")))
            .Add(RegistrySetting.ServiceStart("wuauserv"), ("on", S("3")), ("manual", S("2")), ("paused", S("3")), ("store", S("4")), ("off", S("4")))
            .Add(RegistrySetting.ServiceStart("bits"), ("on", S("3")), ("manual", S("2")), ("paused", S("3")), ("store", S("2")), ("off", S("4")))
            .Add(RegistrySetting.ServiceStart("UsoSvc"), ("on", S("2")), ("manual", S("2")), ("paused", S("2")), ("store", S("2")), ("off", S("4")))
            .Add(RegistrySetting.ServiceStart("WaaSMedicSvc"), ("on", S("3")), ("manual", S("2")), ("paused", S("3")), ("store", S("4")), ("off", S("4")))
            .Add(RegistrySetting.ServiceStart("dosvc"), ("on", S("3")), ("manual", S("2")), ("paused", S("3")), ("store", S("2")), ("off", S("4")))
            .AddSpec(RegistrySetting.ServiceStart("uhssvc"), true, ("on", S("2")), ("manual", S("2")), ("paused", S("2")), ("store", S("4")), ("off", S("4")))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate"), ("on", Del), ("manual", S("1")), ("paused", S("1")), ("store", S("1")), ("off", S("1")))
            .Add(Sz(LocalMachine, WuUx, "PauseUpdatesExpiryTime"), ("on", Del), ("manual", Del), ("paused", S("2038-01-19T03:14:07Z")), ("store", Del), ("off", Del))
            .Script("on", WuScript("1")).Script("manual", WuScript("2")).Script("paused", WuScript("5")).Script("off", WuScript("3")).Script("store", WuScript("4"))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "off"; t.Source = WinFeatures + "Windows Update Enable-Disable.cmd; RunOnce.cmd"; }),

        Toggle("wu-drivers", "Driver updates through Windows Update",
                "Windows Update and device setup download drivers by themselves, and can replace a GPU driver installed by hand. Off excludes drivers from quality updates and stops the driver search on Windows Update.",
                Pages.Windows, Pages.SystemTab, "Updates")
            .Reg(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", RegistryValueKind.DWord, null, "1")
            .Reg(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DriverSearching", "SearchOrderConfig", RegistryValueKind.DWord, null, "0")
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .With(t => { t.KosDefault = Off; t.Source = "RunOnce.cmd; " + WinFeatures + "Windows Update Enable-Disable.cmd (Manual and Off modes)"; }),

        Toggle("teredo", "Teredo and IP Helper",
                "The IP Helper service (iphlpsvc) and Teredo, which tunnels IPv6 over IPv4. Xbox party chat, its network check and some peer-to-peer games use Teredo: with it off they may report a strict NAT. KOS turns both off.",
                Pages.Windows, Pages.Connectivity, "Network")
            .Svc("iphlpsvc", "2", "4")
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\TCPIP\v6Transition", "Teredo_State", RegistryValueKind.String), (On, Del), (Off, Target.SetOrAbsent("Disabled")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = @"RunOnce.cmd (iphlpsvc); Setup\4-Tweaking\Network\Advanced\1-Basics.bat (Teredo)"; }),

        Toggle("store-services", "Microsoft Store",
                "The Store app's install service (InstallService) and installs sent from the web or another device (PushToInstall). The Store app also downloads through Windows Update's services, which KOS turns off: set Windows Update to Microsoft Store only for that. Install › Microsoft Store installs Store apps without them.",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .SvcList(new[] { "InstallService", "PushToInstall" }, "3", "4")
            .With(t => { t.RequiresReboot = true; t.KosDefault = On; t.WindowsDefault = On; t.Source = WinFeatures + "MS-Store Enable-Disable.cmd"; }),

        Toggle("appx-services", "App installs and licenses",
                "AppX Deployment (AppXSvc), Client License (ClipSVC) and License Manager install, update and license every Store and MSIX app, Calculator, Photos, Notepad and Terminal included, and Install › Microsoft Store uses them. Off: those apps can't be installed or updated, and licensed ones may not start.",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .SvcList(new[] { "AppXSvc", "ClipSVC", "LicenseManager" }, "3", "4")
            .With(t => { t.RequiresReboot = true; t.KosDefault = On; t.WindowsDefault = On; t.Source = WinFeatures + "MS-Store Enable-Disable.cmd (Enable)"; }),

        Toggle("windows-hello", "Windows Hello and PIN",
                "Microsoft Passport (NgcSvc) and its container (NgcCtnrSvc): signing in with a PIN, fingerprint or face, and passkeys. Off: only the password signs in.",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .SvcList(new[] { "NgcSvc", "NgcCtnrSvc" }, "3", "4")
            .With(t =>
            {
                t.RequiresReboot = true; t.KosDefault = On; t.WindowsDefault = On;
                t.ConfirmationOption = Off;
                t.ConfirmationNote = "After the restart, an account that signs in only with a PIN, fingerprint or face (a Microsoft account without a password, or with \"Only allow Windows Hello sign-in\" on) has no way left to sign in. Make sure it has a password before restarting.";
                t.Source = "Windows default start type (Manual); MS-Store Enable-Disable.cmd used to turn them off with the Store";
            }),

        Toggle("notifications", "Notifications",
                "System notifications (toasts) and the push notification services.",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .Reg(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", RegistryValueKind.DWord, "1", "0")
            .Svc("WpnService", "2", "4").Svc("WpnUserService", "2", "4", optional: true)
            .With(t => { t.RequiresReboot = true; t.Source = WinFeatures + "Notification-Enable-Disable.bat"; }),

        Choice("clipboard", "Clipboard history",
                "Clipboard history (Win+V), clipboard sync and the clipboard service (cbdhsvc). Copy and paste keep working when it's off. \"Off, with notifications\" also turns off the notification services and toasts, which the Notifications setting controls too.",
                Pages.Windows, Pages.Peripherals, "Clipboard and capture", (On, Txt.Enabled), ("history-off", "Off, keep notifications"), (Off, "Off, with notifications"))
            .AddSpec(RegistrySetting.ServiceStart("cbdhsvc"), true, (On, S("3")), ("history-off", S("4")), (Off, S("4")))
            .Add(RegistrySetting.ServiceStart("WpnService"), (On, S("2")), ("history-off", S("2")), (Off, S("4")))
            .AddSpec(RegistrySetting.ServiceStart("WpnUserService"), true, (On, S("2")), ("history-off", S("2")), (Off, S("4")))
            .Add(R(LocalMachine, SysPolicy, "EnableCdp"), (On, Target.SetOrAbsent("1")), ("history-off", S("0")), (Off, S("0")))
            .Add(R(LocalMachine, SysPolicy, "AllowClipboardHistory"), (On, Target.SetOrAbsent("1")), ("history-off", S("0")), (Off, S("0")))
            .Add(R(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled"), (On, Target.SetOrAbsent("1")), ("history-off", Any), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "history-off"; t.WindowsDefault = On; t.Source = WinFeatures + "Clipboard Enable-Disable.cmd; RunOnce.cmd (cbdhsvc); building\\ntlite.xml; bin\\2\\TheKOS_reg.reg (EnableCdp)"; }),

        Toggle("printing", "Printing",
                "Print spooler, scanning (WIA), print notifications and USB printers. The script also turns virtual printers, scheduled tasks and the printing optional features (DISM) on or off, which can take a while; the check verifies the services.",
                Pages.Windows, Pages.Peripherals, "Devices")
            .Svc("Spooler", "2", "4").Svc("PrintWorkFlowUserSvc", "3", "4", optional: true).Svc("StiSvc", "3", "4", optional: true)
            .Svc("PrintNotify", "3", "4", optional: true).Svc("usbprint", "3", "4", optional: true)
            .Svc("PrintScanBrokerService", "3", "4", optional: true).Svc("PrintDeviceConfigurationService", "3", "4", optional: true)
            .Script(On, new ScriptRef(WinFeatures + "Printer Enable - Disable.cmd", "450CE7EC2B187FFC824A7634191AFD3085B04044094DC3544F56EC985F29484F") { Stdin = "1\r\n", Timeout = TimeSpan.FromMinutes(15) })
            .Script(Off, new ScriptRef(WinFeatures + "Printer Enable - Disable.cmd", "450CE7EC2B187FFC824A7634191AFD3085B04044094DC3544F56EC985F29484F") { Stdin = "2\r\n", Timeout = TimeSpan.FromMinutes(15) })
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.Source = WinFeatures + "Printer Enable - Disable.cmd; RunOnce.cmd"; }),

        // Method from ctfmon.vercel.app: Suspended turns off the input service (ctfmon stays in the process, stopped, as the KOS RunOnce
        // leaves it); Disabled also points TextInputManagementService to MSCTF.DLL, and the service fails to start.
        Choice("ctfmon", "CTF Loader (ctfmon.exe)",
                "Text Services Framework host: input methods (IMEs), handwriting, speech and touch keyboard input. Suspended turns the input service off, so ctfmon.exe stays running but idle instead of handling every keystroke. Disabled also points the Text Input Management service at MSCTF.DLL, so it fails to start. Apps keep typing through MSCTF.DLL in their own process.",
                Pages.Windows, Pages.SystemTab, "Windows features", ("enabled", "Enabled"), ("suspended", "Suspended"), ("disabled", "Disabled"))
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\Input", "InputServiceEnabled"), ("enabled", Target.SetOrAbsent("1")), ("suspended", S("0")), ("disabled", S("0")))
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\Input", "InputServiceEnabledForCCI"), ("enabled", Target.SetOrAbsent("1")), ("suspended", S("0")), ("disabled", S("0")))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\TextInputManagementService\Parameters", "ServiceDll", RegistryValueKind.ExpandString),
                ("enabled", S(@"%SystemRoot%\System32\TabSvc.dll")), ("suspended", S(@"%SystemRoot%\System32\TabSvc.dll")), ("disabled", S(@"%SystemRoot%\System32\MSCTF.DLL")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "enabled"; t.KosDefault = "suspended"; t.Source = "ctfmon.vercel.app; RunOnce.cmd (InputServiceEnabled)"; }),

        Toggle("screen-capture", "Windows screen capture shortcut (Win+Shift+S)",
                "The Win+Shift+S shortcut and the Print Screen key opening the Snipping Tool. In KOS, Lightshot uses Win+Shift+S.",
                Pages.Windows, Pages.Peripherals, "Clipboard and capture")
            .Reg(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisabledHotkeys", RegistryValueKind.String, null, "S")
            .Reg(CurrentUser, @"Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", RegistryValueKind.DWord, "1", "0")
            .With(t => { t.RestartExplorer = true; t.KosDefault = Off; t.Source = WinFeatures + "Win+Shift+S-Enable-Disable.bat; RunOnce.cmd"; }),

        Choice("alt-tab", "Alt+Tab style", "Modern (immersive) or classic window switcher.",
                Pages.Windows, Pages.Visuals, "Interface", ("immersive", "Modern"), ("classic", "Classic"))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "AltTabSettings"), ("immersive", S("0")), ("classic", S("1")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "HoverSelectDesktops"), ("immersive", S("1")), ("classic", S("0")))
            .With(t => { t.RestartExplorer = true; t.KosDefault = "immersive"; t.Source = WinFeatures + "Alt-Tab-Settings.bat; RunOnce.cmd"; }),

        Toggle("reliability", "Reliability Monitor", "Data collection for Reliability Monitor (perfmon /rel).",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .Reg(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Reliability Analysis\WMI", "WMIEnable", RegistryValueKind.DWord, "1", "0")
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.Source = Fixes + "Reliability Analysis.cmd; bin\\2\\TheKOS_reg.reg"; }),

        Toggle("transparency", "Transparency effects", "Translucent taskbar, Start menu and windows (Settings › Personalization › Colors).",
                Pages.Windows, Pages.Visuals, "Interface")
            .Reg(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegistryValueKind.DWord, "1", "0")
            .With(t => { t.RestartExplorer = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = "EnableTransparency; RunOnce.cmd"; }),

        Choice("context-menu", "Context menu style",
                "Windows 11 menu or the classic full menu without \"Show more options\". Classic blocks the Windows 11 menu's COM class for this user.",
                Pages.Windows, Pages.Visuals, "Interface", ("modern", "Windows 11"), ("classic", "Classic"))
            .Add(new RegistryKeySetting(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"), ("modern", Del), ("classic", S("1")))
            .With(t => { t.RestartExplorer = true; t.WindowsDefault = "modern"; t.Source = "Old Context Menu"; }),

        Choice("start-menu", "Start menu",
                "Which Start menu the Start button and the Windows key open. Windows brings back the Windows Start menu and turns Open-Shell off for this account (exits it and stops it from starting at sign-in; it stays installed). Open-Shell renames the Windows Start menu app to .old and starts Open-Shell again.",
                Pages.Windows, Pages.Visuals, "Start menu", ("windows", "Windows"), ("openshell", "Open-Shell"))
            .Add(new StartMenuHostSetting(), ("windows", S(On)), ("openshell", S(Off)))
            .Add(new OpenShellAutoStartSetting(), ("windows", S(Off)), ("openshell", S(On)))
            .Script("windows", StartMenuScript("1")).Script("openshell", StartMenuScript("2"))
            .With(t => { t.RestartExplorer = true; t.KosDefault = "openshell"; t.Source = WinFeatures + "Start Menu Enable-Disable.bat; RunOnce.cmd (Open-Shell)"; }),

        Toggle("start-menu-search", "Start menu search",
                "Windows Search in the Windows Start menu: the search box and type-to-search, the taskbar Search button and search shortcuts such as Win+S (DisableSearch policy, Windows 11 22H2 or later). Open-Shell has its own search, so this is unavailable while it's the Start menu.",
                Pages.Windows, Pages.Visuals, "Start menu")
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearch"), (On, Target.SetOrAbsent("0")), (Off, S("1")))
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\default\Search\DisableSearch", "value"), (On, Target.SetOrAbsent("0")), (Off, S("1")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Compatibility = StartMenus.SearchUnavailable; t.Source = "Policy CSP Search/DisableSearch"; }),

        Toggle("startup-sound", "Windows startup sound", "The sound played at sign-in after Windows starts.",
                Pages.Windows, Pages.Peripherals, "Audio")
            .Reg(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation", "DisableStartupSound", RegistryValueKind.DWord, "0", "1")
            .With(t => { t.Source = "Disable startup sound"; }),

        Toggle("autoplay", "AutoPlay and AutoRun", "Asking what to do, or running autorun.inf, when a drive, media or phone is connected.",
                Pages.Windows, Pages.Peripherals, "Devices")
            .Reg(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", RegistryValueKind.DWord, "0", "1")
            .Reg(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", RegistryValueKind.DWord, null, "255")
            .With(t => { t.WindowsDefault = On; t.Source = "Autorun & Autoplay"; }),

        Choice("comms-ducking", "Communications activity",
                "What Windows does to other sounds when it detects a call (Sound control panel › Communications). Voice apps such as Discord can trigger it.",
                Pages.Windows, Pages.Peripherals, "Audio",
                ("0", "Mute all other sounds"), ("1", "Reduce by 80%"), ("2", "Reduce by 50%"), ("3", "Do nothing"))
            .Add(R(CurrentUser, @"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference"),
                ("0", S("0")), ("1", Target.SetOrAbsent("1")), ("2", S("2")), ("3", S("3")))
            .With(t => { t.WindowsDefault = "1"; t.Source = "UserDuckingPreference"; }),

        Toggle("mono-audio", "Mono audio", "Combines the left and right channels into one (Settings › Accessibility › Audio).",
                Pages.Windows, Pages.Peripherals, "Audio")
            .Add(R(CurrentUser, @"Software\Microsoft\Multimedia\Audio", "AccessibilityMonoMixState"), (On, S("1")), (Off, Target.SetOrAbsent("0")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "AccessibilityMonoMixState"; }),

        Choice("short-date", "Short date format", "How dates appear in the taskbar clock, Explorer and apps that follow Windows formats.",
                Pages.Windows, Pages.SystemTab, "Date and time",
                ("M/d/yyyy", "M/d/yyyy"), ("dd/MM/yyyy", "dd/MM/yyyy"), ("dd.MM.yyyy", "dd.MM.yyyy"), ("yyyy-MM-dd", "yyyy-MM-dd"))
            .Add(Sz(CurrentUser, @"Control Panel\International", "sShortDate"),
                ("M/d/yyyy", S("M/d/yyyy")), ("dd/MM/yyyy", S("dd/MM/yyyy")), ("dd.MM.yyyy", S("dd.MM.yyyy")), ("yyyy-MM-dd", S("yyyy-MM-dd")))
            .With(t => { t.RestartExplorer = true; t.Source = "sShortDate"; }),

        Choice("time-format", "Time format", "12-hour or 24-hour clock in the taskbar and in apps that follow Windows formats.",
                Pages.Windows, Pages.SystemTab, "Date and time", ("12", "12-hour (1:05 PM)"), ("24", "24-hour (13:05)"))
            .Add(Sz(CurrentUser, @"Control Panel\International", "sShortTime"), ("12", S("h:mm tt")), ("24", S("HH:mm")))
            .Add(Sz(CurrentUser, @"Control Panel\International", "sTimeFormat"), ("12", S("h:mm:ss tt")), ("24", S("HH:mm:ss")))
            .With(t => { t.RestartExplorer = true; t.Source = "sShortTime, sTimeFormat"; }),

        Choice("first-day-of-week", "First day of the week", "First day shown in the taskbar calendar and in apps that follow Windows formats.",
                Pages.Windows, Pages.SystemTab, "Date and time", ("0", "Monday"), ("6", "Sunday"), ("5", "Saturday"))
            .Add(Sz(CurrentUser, @"Control Panel\International", "iFirstDayOfWeek"), ("0", S("0")), ("6", S("6")), ("5", S("5")))
            .With(t => { t.RestartExplorer = true; t.Source = "iFirstDayOfWeek"; }),

        Choice("ntp-server", "Time server (NTP)", "Server Windows uses to synchronize the clock. Takes effect when the Windows Time service restarts.",
                Pages.Windows, Pages.SystemTab, "Date and time",
                ("kos", "pool.ntp.org, 4 servers (KOS)"), ("time.windows.com,0x9", "time.windows.com (Windows)"),
                ("time.cloudflare.com,0x9", "time.cloudflare.com"), ("time.google.com,0x9", "time.google.com"))
            .Add(Sz(LocalMachine, @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", "NtpServer"),
                ("kos", S("0.pool.ntp.org 1.pool.ntp.org 2.pool.ntp.org 3.pool.ntp.org")), ("time.windows.com,0x9", S("time.windows.com,0x9")),
                ("time.cloudflare.com,0x9", S("time.cloudflare.com,0x9")), ("time.google.com,0x9", S("time.google.com,0x9")))
            .With(t =>
            {
                t.RequiresReboot = true; t.KosDefault = "kos"; t.WindowsDefault = "time.windows.com,0x9";
                t.Source = @"bin\3\startuptweaks.cmd (w32tm /manualpeerlist)";
            }),

        Toggle("background-apps", "Background apps",
                "Lets Store apps keep running in the background for notifications and live updates; messaging apps from the Store need it. Desktop programs aren't affected. Disabling denies it to every app (LetAppsRunInBackground policy), turns off the per-user switch and the Background Activity Moderator driver (bam).",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground"), (On, Del), (Off, S("2")))
            .With(t => { foreach (var list in new[] { "UserInControlOfTheseApps", "ForceAllowTheseApps", "ForceDenyTheseApps" })
                t.Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground_" + list, RegistryValueKind.MultiString), (On, Any), (Off, Del)); })
            .Add(R(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled"), (On, Target.SetOrAbsent("0")), (Off, S("1")))
            .Svc("bam", "1", "4", optional: true)
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = @"building\ntlite.xml (GlobalUserDisabled); RunOnce.cmd (bam); bin\2\TheKOS_reg.reg (LetAppsRunInBackground)"; }),

        Toggle("diagnostics-services", "System usage and diagnostics",
                "The Diagnostic Policy service and its hosts (problem detection, troubleshooters, the app history in Task Manager) and sleep study logging. Windows keeps working without them.",
                Pages.Windows, Pages.SystemTab, "Windows features")
            .Svc("DPS", "2", "4").Svc("diagsvc", "3", "4", optional: true).Svc("WdiServiceHost", "3", "4", optional: true).Svc("WdiSystemHost", "3", "4", optional: true)
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "SleepStudyDisabled"), (On, Del), (Off, S("1")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = @"RunOnce.cmd (service list); bin\2\TheKOS_reg.reg (SleepStudyDisabled)"; }),

        Choice("windows-search", "Windows Search",
                "On: the search indexer and the search app. No indexing: the Windows Search service is off; searching still works, more slowly. Off: also blocks the search app (SearchHost.exe renamed to .old, as KOS does, and redirected to a program that does nothing) and hides the taskbar search box. Open-Shell's Start menu search uses neither.",
                Pages.Windows, Pages.SystemTab, "Windows features", (On, Txt.Enabled), ("no-index", "No indexing"), (Off, Txt.Disabled))
            .Add(RegistrySetting.ServiceStart("WSearch"), (On, S("2")), ("no-index", S("4")), (Off, S("4")))
            .BlockExes(SearchExes, @"%SystemRoot%\System32\systray.exe", Off)
            .AddSpec(new RenamedExeSetting("file:search-host", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe")), true, (On, S(On)), ("no-index", S(On)), (Off, S(Off)))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode"), (On, Any), ("no-index", Any), (Off, S("0")))
            .Script(On, SearchScript("on")).Script("no-index", SearchScript("noindex")).Script(Off, SearchScript("off"))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = WinFeatures + "Windows Search Enable-Disable.cmd; RunOnce.cmd (WSearch, SearchHost.old); bin\\2\\TheKOS_reg.reg"; }),

        Toggle("themes-service", "Visual styles (Themes service)",
                "The Themes service draws the Windows visual styles. Off gives classic windows and dialogs their basic look; some apps look different.",
                Pages.Windows, Pages.Visuals, "Interface")
            .Svc("Themes", "2", "4")
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = "RunOnce.cmd (service list)"; }),

        Toggle("quick-access", "Quick Access in File Explorer", "The Quick Access entry in File Explorer's navigation pane.",
                Pages.Windows, Pages.Visuals, "File Explorer")
            .Reg(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "HubMode", RegistryValueKind.DWord, null, "1")
            .With(t => { t.RestartExplorer = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = "RunOnce.cmd (HubMode)"; }),

        Choice("visual-effects", "Visual effects",
                "Window, taskbar and menu animations, shadows under the pointer and desktop icons, the translucent selection rectangle and Peek. Reduced is what KOS sets (Performance Options shows \"Custom\"). It rewrites the whole UserPreferencesMask value, as KOS does. Applies after signing out.",
                Pages.Windows, Pages.Visuals, "Interface", ("reduced", "Reduced (KOS)"), ("full", "Full (Windows)"))
            .Add(R(CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask", RegistryValueKind.Binary), ("reduced", S("9012038010000000")), ("full", S("9E1E078012000000")))
            .Add(R(CurrentUser, ExplorerAdvanced, "ListviewShadow"), ("reduced", S("0")), ("full", Target.SetOrAbsent("1")))
            .Add(R(CurrentUser, ExplorerAdvanced, "ListviewAlphaSelect"), ("reduced", S("0")), ("full", Target.SetOrAbsent("1")))
            .Add(R(CurrentUser, ExplorerAdvanced, "TaskbarAnimations"), ("reduced", S("0")), ("full", Target.SetOrAbsent("1")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"), ("reduced", S("3")), ("full", Target.SetOrAbsent("0")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\DWM", "EnableAeroPeek"), ("reduced", S("0")), ("full", Target.SetOrAbsent("1")))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DWM", "DisallowAnimations"), ("reduced", S("1")), ("full", Del))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DWM", "DWMA_TRANSITTIONS_FORCEDISABLED"), ("reduced", S("1")), ("full", Del))
            .With(t => { t.RequiresReboot = true; t.RestartExplorer = true; t.KosDefault = "reduced"; t.WindowsDefault = "full"; t.Source = "RunOnce.cmd (visual effects)"; }),

        Toggle("taskbar-end-task", "End task in the taskbar", "Adds End task to a taskbar button's right-click menu: it closes the program the way Task Manager does.",
                Pages.Windows, Pages.Visuals, "Interface")
            .Add(R(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask"), (On, S("1")), (Off, Target.SetOrAbsent("0")))
            .With(t => { t.KosDefault = On; t.WindowsDefault = Off; t.Source = @"bin\2\TheKOS_reg.reg (TaskbarEndTask)"; }),

        Toggle("explorer-home", "Home in File Explorer", "The Home entry (recent and favorite files) in File Explorer's navigation pane.",
                Pages.Windows, Pages.Visuals, "File Explorer")
            .Add(R(CurrentUser, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .With(t => { t.RestartExplorer = true; t.WindowsDefault = On; t.Source = "System.IsPinnedToNameSpaceTree"; }),

        Choice("background-mouse-rate", "Mouse input rate for background apps",
                "How often apps in the background get raw mouse input; Windows 11 limits it to save power. The lower rate frees CPU time while a game is in front, but background apps that track the mouse (recorders, macro tools) may skip movements.",
                Pages.Windows, Pages.Peripherals, "Mouse and keyboard", ("50", "50 Hz (KOS)"), ("125", "125 Hz (Windows)"))
            .Add(R(CurrentUser, @"Control Panel\Mouse", "RawMouseThrottleEnabled"), ("50", Target.SetOrAbsent("1")), ("125", Target.SetOrAbsent("1")))
            .Add(R(CurrentUser, @"Control Panel\Mouse", "RawMouseThrottleDuration"), ("50", S("20")), ("125", Target.SetOrAbsent("8")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "50"; t.WindowsDefault = "125"; t.Source = @"bin\2\TheKOS_reg.reg (RawMouseThrottleDuration)"; }),

        Toggle("mouse-acceleration", "Enhance pointer precision (mouse acceleration)",
                "Moves the pointer further when the mouse moves faster. Off, the same mouse movement always moves the pointer the same distance. Takes effect at the next sign-in.",
                Pages.Windows, Pages.Peripherals, "Mouse and keyboard")
            .Add(R(CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String), (On, S("1")), (Off, S("0")))
            .Add(R(CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String), (On, S("6")), (Off, S("0")))
            .Add(R(CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String), (On, S("10")), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = "RunOnce.cmd (MouseSpeed, MouseThreshold1, MouseThreshold2)"; }),

        Toggle("device-coinstallers", "Device software auto-install",
                "Lets a device run its maker's co-installer when it's plugged in, which can install a whole suite by itself (Razer Synapse is the known case). Off blocks co-installers; the driver itself still installs. Some older printer and audio drivers rely on them.",
                Pages.Windows, Pages.Peripherals, "Devices")
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers"), (On, Target.SetOrAbsent("0")), (Off, S("1")))
            .With(t => { t.KosDefault = On; t.WindowsDefault = On; t.Source = @"bin\2\TheKOS_reg.reg (DisableCoInstallers = 0)"; }),

        Toggle("miracast", "Miracast (wireless display)", "Projecting to a wireless display or TV over Wi-Fi Direct (Win+K).",
                Pages.Windows, Pages.Peripherals, "Devices")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "MiracastForceDisable", RegistryValueKind.DWord, null, "1")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = "MiracastForceDisable"; }),

        Toggle("serial-ports", "Serial and parallel ports",
                "COM and LPT ports and their drivers. Some laptop touchpads and USB serial adapters (Arduino, modems) need them; on desktops they're rarely used. On first boot KOS keeps them on laptops.",
                Pages.Windows, Pages.Peripherals, "Devices")
            .SvcList(new[] { "Serenum", "Serial", "sermouse", "usbser", "Parport" }, "3", "4")
            .Script(On, SerialScript("on")).Script(Off, SerialScript("off"))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = WinFeatures + "Serial Ports Enable-Disable.cmd; bin\\2\\DeviceProfile.cmd"; }),

        Toggle("usb-power-saving", "USB power saving",
                "USB selective suspend and link power management, in the registry and in the active power plan (plugged in). Turning it off can fix USB devices that disconnect or lag after idling. The Desktop power profile turns it off too.",
                Pages.Windows, Pages.Peripherals, "Devices")
            .Add(R(LocalMachine, Usbhub3, "DisableLPM"), (On, Del), (Off, S("1")))
            .Add(R(LocalMachine, Usbhub3, "D3ColdSupported"), (On, Del), (Off, S("0")))
            .Add(R(LocalMachine, Usbhub3, "DisableSelectiveSuspendUI"), (On, Del), (Off, S("1")))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\usbccgp\Parameters", "D3ColdSupported"), (On, Del), (Off, S("0")))
            .Add(new PowerSetting(new Guid("2a737441-1930-4402-8d77-b2bebba308a3"), new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"), "USB selective suspend"), (On, S("1")), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = Bats + "Configure Powersavings.bat"; }),

        Toggle("write-cache-flush", "Write-cache buffer flushing",
                "Windows makes each disk flush its write cache so data really reaches the disk. Off makes writes faster, but data still in the cache can be lost and files damaged on a power cut or crash: use it with a UPS or a drive with power-loss protection. USB drives aren't changed.",
                Pages.Windows, Pages.Peripherals, "Storage")
            .Add(new WriteCacheFlushSetting(), (On, S("1")), (Off, S("0")))
            .With(t =>
            {
                t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.LessSecureOption = Off;
                t.SecurityNote = "With write-cache flushing off, a power cut or crash can lose recent writes or damage files.";
                t.Source = "RunOnce.cmd (UserWriteCacheSetting, CacheIsPowerProtected)";
            }),

        // ======================= WINDOWS › GAMING AND PERFORMANCE =======================
        Toggle("game-bar", "Game Bar and game recording",
                "Xbox Game Bar (Win+G), background recording and automatic Game Mode. Disabling also stops the Game Bar processes from starting (Image File Execution Options redirect to systray.exe); enabling removes the redirect.",
                Pages.Windows, Pages.Gaming, "Game Bar and Xbox")
            .Add(R(CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode"), (On, Del), (Off, S("0")))
            .Add(R(CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"), (On, Del), (Off, S("0")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AudioCaptureEnabled"), (On, Del), (Off, S("0")))
            .Add(R(CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "CursorCaptureEnabled"), (On, Del), (Off, S("0")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled"), (On, S("1")), (Off, S("0")))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR"), (On, Del), (Off, S("0")))
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\default\ApplicationManagement\AllowGameDVR", "value"), (On, S("1")), (Off, S("0")))
            .BlockExes(GameBarExes, @"%SystemRoot%\System32\systray.exe", Off)
            .With(t => { t.KosDefault = Off; t.WindowsDefault = On; t.Source = @"Setup\4-Tweaking\FSE - FSO - GameBar\*.reg; bin\2\TheKOS_reg.reg"; }),

        Choice("fullscreen-mode", "Fullscreen mode",
                "How fullscreen DirectX games are shown: with fullscreen optimizations (FSO) Windows runs them as borderless flip windows, so Alt+Tab and overlays are quick; exclusive fullscreen (FSE) gives the game the whole display.",
                Pages.Windows, Pages.Gaming, "Game Bar and Xbox", ("fso", "Fullscreen optimizations (FSO)"), ("fse", "Exclusive fullscreen (FSE)"))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_DSEBehavior"), ("fso", Del), ("fse", S("2")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible"), ("fso", Target.SetOrAbsent("0")), ("fse", S("1")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_EFSEFeatureFlags"), ("fso", Target.SetOrAbsent("0")), ("fse", S("0")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehavior"), ("fso", Del), ("fse", S("2")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode"), ("fso", Target.SetOrAbsent("2")), ("fse", S("2")))
            .Add(R(CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode"), ("fso", Target.SetOrAbsent("0")), ("fse", S("1")))
            .With(t => { t.KosDefault = "fso"; t.WindowsDefault = "fso"; t.Source = @"Setup\4-Tweaking\FSE - FSO - GameBar\*.reg; bin\2\TheKOS_reg.reg"; }),

        Toggle("windowed-optimizations", "Optimizations for windowed games",
                "Moves DirectX 10 and 11 games that run windowed or borderless to the flip presentation model: lower latency, and Auto HDR and variable refresh rate work there (Settings › Display › Graphics).",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Add(new DxSetting("SwapEffectUpgradeEnable"), (On, S("1")), (Off, S("0")))
            .With(t => { t.KosDefault = On; t.Source = "RunOnce.cmd; NTLite (DirectXUserGlobalSettings)"; }),

        Toggle("variable-refresh-rate", "Variable refresh rate (windowed games)",
                "Lets G-SYNC or FreeSync drive windowed DirectX 11 and 12 games that don't support it themselves (Settings › Display › Graphics). Needs a variable refresh rate display.",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Add(new DxSetting("VRROptimizeEnable"), (On, S("1")), (Off, S("0")))
            .With(t => { t.KosDefault = Off; t.Source = "RunOnce.cmd; NTLite (DirectXUserGlobalSettings)"; }),

        Toggle("auto-hdr", "Auto HDR", "Turns SDR DirectX 11 and 12 games into HDR. Works only on an HDR display with HDR on.",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Add(new DxSetting("AutoHDREnable"), (On, S("1")), (Off, S("0")))
            .With(t => { t.Source = "DirectXUserGlobalSettings (AutoHDREnable)"; }),

        Toggle("fivem-services", "Services for FiveM and Minecraft servers",
                "Some FiveM and Minecraft servers check that these Windows services run before letting you in: Program Compatibility Assistant, Diagnostic Policy, Connected User Experiences (DiagTrack), SysMain with prefetch, Data Usage and the Background Activity Moderator. Event Log, DNS Client and Application Information are only turned on, never off.",
                Pages.Windows, Pages.Gaming, "Game servers")
            .Svc("PcaSvc", "2", "4").Svc("DPS", "2", "4").Svc("DiagTrack", "2", "4", optional: true).Svc("SysMain", "2", "4")
            .Svc("DusmSvc", "2", "4", optional: true).Svc("bam", "1", "4", optional: true)
            .Add(R(LocalMachine, MemMgmt + @"\PrefetchParameters", "EnablePrefetcher"), (On, S("1")), (Off, S("0")))
            .AddSpec(RegistrySetting.ServiceStart("EventLog"), false, (On, S("2")), (Off, Any))
            .AddSpec(RegistrySetting.ServiceStart("Dnscache"), false, (On, S("2")), (Off, Any))
            .AddSpec(RegistrySetting.ServiceStart("Appinfo"), false, (On, S("3")), (Off, Any))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.Source = WinFeatures + "Safe FiveM-Minecraft Services.cmd; RunOnce.cmd; bin\\2\\TheKOS_reg.reg (EnablePrefetcher)"; }),

        Toggle("xbox-services", "Xbox services",
                "Xbox networking, cloud saves, sign-in and controller services.",
                Pages.Windows, Pages.Gaming, "Game Bar and Xbox")
            .SvcList(new[] { "XboxNetApiSvc", "XblGameSave", "XblAuthManager", "xbgm", "XboxGipSvc" }, "3", "4")
            .With(t => { t.RequiresReboot = true; t.Source = WinFeatures + "Xbox Services Enable-Disable.cmd (reimplemented: the script uses the ControlSet\\Services path and the name XblAuthManage)"; }),

        Toggle("hags", "Hardware-accelerated GPU scheduling",
                "The GPU schedules its own work instead of Windows (Settings › Display › Graphics). Needs a supported GPU and driver; NVIDIA DLSS Frame Generation requires it.",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueKind.DWord, "2", "1")
            .With(t => { t.RequiresReboot = true; t.Source = "HwSchMode"; }),

        Toggle("hags-experimental", "HAGS experimental features",
                "Treats the GPU scheduler's experimental features as stable. Needs hardware-accelerated GPU scheduling on. On = enabled (1); off = the Windows default (absent).",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchTreatExperimentalAsStable", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "HwSchTreatExperimentalAsStable"; }),

        Toggle("mpo", "Multi-Plane Overlay (MPO)",
                "Hardware plane composition. Disabling it can fix flickering with some drivers, but can affect fullscreen optimizations.",
                Pages.Windows, Pages.Gaming, "Graphics")
            .Reg(LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", RegistryValueKind.DWord, null, "5")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = Bats + "MultiPlane Overlay.bat (reimplemented: the script's Enable option has a missing quote and fails)"; }),

        Choice("system-responsiveness", "SystemResponsiveness (MMCSS)",
                "Percentage of CPU the multimedia scheduler reserves for low-priority tasks. 100 turns off the MMCSS reservation.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS", ("20", "20 (Windows)"), ("10", "10 (KOS)"), ("100", "100"))
            .Add(R(LocalMachine, Mm, "SystemResponsiveness"), ("20", S("20")), ("10", S("10")), ("100", S("100")))
            .With(t => { t.KosDefault = "10"; t.WindowsDefault = "20"; t.Source = Bats + "SystemResponsiveness.cmd; bin\\2\\TheKOS_reg.reg"; }),

        Toggle("no-lazy-mode", "NoLazyMode (MMCSS)",
                "Makes MMCSS run in a more \"real-time\" mode, using more CPU cycles. Experimental: test before keeping it.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS")
            .Reg(LocalMachine, Mm, "NoLazyMode", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = Bats + "NoLazyMode.bat"; }),

        Choice("lazy-mode-timeout", "LazyModeTimeout (MMCSS)", "How long MMCSS waits between idle periods before suspending cycles. Experimental.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS",
                ("default", Txt.NotConfiguredOption), ("10000", "10000"), ("25000", "25000"), ("150000", "150000"))
            .Add(R(LocalMachine, Mm, "LazyModeTimeout"), ("default", Del), ("10000", S("10000")), ("25000", S("25000")), ("150000", S("150000")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "default"; t.Source = Bats + "LazyModeTimeout.bat"; }),

        Toggle("mmcss", "MMCSS service", "Multimedia Class Scheduler. Disabling it can change latency in games and audio; test before keeping it.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS")
            .Svc("MMCSS", "2", "4")
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "MMCSS.bat (the toolbox doesn't force a restart like the script)"; }),

        Choice("mmcss-preset", "MMCSS preset",
                "How the multimedia scheduler prioritizes audio and multimedia threads. Windows default puts back the Windows values. Low latency tunes for the lowest audio " +
                "latency (scheduler period, idle detection, timer resolution, Audio task priority and the network receive thread). Background audio lowers the audio threads' priority (may crackle under load).",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS", ("default", "Windows default"), ("low-latency", "Low latency"), ("background", "Background audio"))
            .Add(R(LocalMachine, Mm, "SchedulerPeriod"), ("default", Del), ("low-latency", S("1000000")), ("background", Del))
            .Add(R(LocalMachine, Mm, "IdleDetectionCycles"), ("default", Del), ("low-latency", S("1")), ("background", Del))
            .Add(R(LocalMachine, Mm, "SchedulerTimerResolution"), ("default", Del), ("low-latency", S("10000")), ("background", Del))
            .Add(R(LocalMachine, Mm, "MaxThreadsPerProcess"), ("default", Del), ("low-latency", Del), ("background", Del))
            .Add(R(LocalMachine, Mm, "MaxThreadsTotal"), ("default", Del), ("low-latency", Del), ("background", Del))
            .Add(Sz(LocalMachine, Mm + @"\Tasks\Audio", "Scheduling Category"), ("default", S("Medium")), ("low-latency", S("Medium")), ("background", S("Low")))
            .Add(R(LocalMachine, Mm + @"\Tasks\Audio", "Priority"), ("default", S("6")), ("low-latency", S("1")), ("background", Del))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\NDIS\Parameters", "ReceiveWorkerThreadPriority"), ("default", Del), ("low-latency", S("16")), ("background", Del))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "default"; t.Source = Mm + @" (SchedulerPeriod, IdleDetectionCycles, SchedulerTimerResolution, Tasks\Audio); NDIS ReceiveWorkerThreadPriority"; }),

        Choice("csrss-priority", "CSRSS CPU priority", "Priority of the process that handles raw input.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS",
                ("unset", Txt.NotConfiguredOption), ("4", "Realtime"), ("3", "High"), ("6", "Above normal"), ("2", "Normal"), ("5", "Below normal"))
            .Add(R(LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass"),
                ("unset", Del), ("4", S("4")), ("3", S("3")), ("6", S("6")), ("2", S("2")), ("5", S("5")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "unset"; t.Source = Bats + "CSRSS Priority.bat; RunOnce.cmd (commented-out block)"; }),

        Toggle("power-throttling", "Power Throttling",
                "Power limiting for background processes. Disabling it can improve performance at the cost of more power use and heat.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS")
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff"), (On, Target.SetOrAbsent("0")), (Off, S("1")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = Bats + "PowerThrottling.cmd (the toolbox doesn't force a restart)"; }),

        Toggle("thread-dpc", "Threaded DPC", "Threaded DPCs, which can be preempted. Disabling them can improve responsiveness or cause hangs on unstable hardware.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "ThreadDpcEnable", RegistryValueKind.DWord, "1", "0")
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "ThreadDPC.cmd"; }),

        Toggle("interrupt-steering", "Interrupt Steering", "Distribution of interrupts across CPU cores.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "InterruptSteeringDisabled", RegistryValueKind.DWord, "0", "1")
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "InterruptSteering.bat"; }),

        Toggle("xhci-imod", "USB interrupt moderation off (XHCI IMOD)",
                "At every sign-in, a KOS script sets the interrupt moderation interval of every USB (XHCI) controller to 0 through RW, so input " +
                "reaches Windows without being batched. Windows refuses RW's driver while the vulnerable driver blocklist, VBS or memory integrity is on: then it can't be turned on.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Add(R(LocalMachine, @"SOFTWARE\TheKOS", "XhciImod"), (On, Target.SetOrAbsent("1")), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = On; t.Compatibility = Devices.RwDriver.Blocked; t.Source = @"bin\3\XHCI-IMOD-Interval.ps1 (startuptweaks.cmd)"; }),

        Toggle("global-timer-resolution", "Global timer resolution requests",
                "Since Windows 10 2004, a program's timer resolution request applies only to that program. Enabling brings back the old behavior, where one request raises the resolution for every process (Windows 11 and Server 2022 or later).",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "GlobalTimerResolutionRequests", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "GlobalTimerResolutionRequests"; }),

        Toggle("serialize-timer-expiration", "Serialize timer expiration",
                "Whether timer DPCs run serialized. On = serialized (1); off = the Windows default (absent).",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "SerializeTimerExpiration", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "SerializeTimerExpiration"; }),

        Toggle("per-cpu-clock-tick", "Per-CPU clock tick scheduling",
                "Schedules the clock tick per processor. On = enabled (1); off = the Windows default (absent).",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "EnablePerCpuClockTickScheduling", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "EnablePerCpuClockTickScheduling"; }),

        Choice("cache-aware-scheduling", "Cache-aware scheduling",
                "How the scheduler places threads by the processors' caches. Windows default = absent.",
                Pages.Windows, Pages.Performance, "Kernel and memory", ("default", "Windows default"), ("optimized", "Optimized (0x27)"))
            .Add(R(LocalMachine, Kernel, "CacheAwareScheduling"), ("default", Del), ("optimized", S("39")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "default"; t.Source = "CacheAwareScheduling"; }),

        Toggle("skip-tick-override", "Skip tick override",
                "The power manager's skip-tick override. Off = disabled (0); on = the Windows default (absent).",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "SkipTickOverride", RegistryValueKind.DWord, null, "0")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = "SkipTickOverride"; }),

        Toggle("tick-accumulation", "Tick accumulation from accounting periods",
                "Accumulates ticks from the accounting periods. On = enabled (1); off = the Windows default (absent).",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel, "EnableTickAccumulationFromAccountingPeriods", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = "EnableTickAccumulationFromAccountingPeriods"; }),

        Toggle("small-processor-mask", "Small processor mask",
                "Which logical processors Windows treats as little (efficient) cores, read at boot. On = Windows decides from the CPU (absent); off = 0, no processor is " +
                "treated as small. Only matters on hybrid CPUs where Windows gets the core types wrong.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, Kernel + @"\KGroups\00", "SmallProcessorMask", RegistryValueKind.DWord, null, "0")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = @"kernel\KGroups\00 SmallProcessorMask"; }),

        Choice("worker-threads", "Additional kernel worker threads", "AdditionalCriticalWorkerThreads and AdditionalDelayedWorkerThreads. Experimental.",
                Pages.Windows, Pages.Performance, "Kernel and memory", ("0", "Default (0)"), ("1", "1 per core"), ("2", "2 per core"))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Executive", "AdditionalCriticalWorkerThreads"), ("0", Target.SetOrAbsent("0")), ("1", S("1")), ("2", S("2")))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Executive", "AdditionalDelayedWorkerThreads"), ("0", Target.SetOrAbsent("0")), ("1", S("1")), ("2", S("2")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "0"; t.Source = @"Setup\4-Tweaking\Experimental\AdditionalWorkerThreads_final.bat"; }),

        Toggle("large-system-cache", "LargeSystemCache", "Favors the file system cache over process memory. Experimental.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, MemMgmt, "LargeSystemCache", RegistryValueKind.DWord, "1", "0")
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "LargeSystemCache.bat"; }),

        Choice("io-latency-cap", "IoLatencyCap (StorPort)", "Time after which StorPort holds new pending I/O requests. Experimental.",
                Pages.Windows, Pages.Performance, "Kernel and memory",
                ("80", "80 (KOS)"), ("0", "0 (Windows)"), ("20", "20"), ("25", "25"), ("50", "50"), ("500", "500"))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services", "IoLatencyCap"),
                ("80", S("80")), ("0", Target.SetOrAbsent("0")), ("20", S("20")), ("25", S("25")), ("50", S("50")), ("500", S("500")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "80"; t.WindowsDefault = "0"; t.Source = Bats + "IoLatencyCap.bat (values written as in the script)"; }),

        Choice("io-page-lock-limit", "IOPageLockLimit", "Limit of memory locked for I/O. Experimental. (The script's \"based on RAM\" option was left out: it writes the total memory instead of the calculated value.)",
                Pages.Windows, Pages.Performance, "Kernel and memory",
                ("default", Txt.NotConfiguredOption), ("1048576", "1 MB"), ("2097152", "2 MB"), ("4194304", "4 MB"), ("8388608", "8 MB"), ("4294967295", "Maximum"))
            .Add(R(LocalMachine, MemMgmt, "IOPageLockLimit"), ("default", Del), ("1048576", S("1048576")), ("2097152", S("2097152")),
                ("4194304", S("4194304")), ("8388608", S("8388608")), ("4294967295", S("4294967295")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = "default"; t.Source = Bats + "IOPageLockLimit.bat"; }),

        Toggle("intel-ppm", "IntelPPM driver", "Intel processor power management driver. Disabling it is experimental and can cause stutters.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Svc("intelppm", "3", "4")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Compatibility = IntelOnly; t.Source = Bats + "DriverPPM.bat"; }),

        Toggle("amd-ppm", "AmdPPM driver", "AMD processor power management driver. Disabling it is experimental and can cause stutters.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Svc("amdppm", "3", "4")
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Compatibility = AmdOnly; t.Source = Bats + "DriverPPM.bat"; }),

        Toggle("native-nvme", "Native NVMe driver (experimental)",
                "Turns on the new native Windows NVMe driver. It can stop some systems or SSDs from booting or make them unstable; use at your own risk.",
                Pages.Windows, Pages.Performance, "Kernel and memory")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides", "735209102", RegistryValueKind.DWord, "1", null)
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides", "1853569164", RegistryValueKind.DWord, "1", null)
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides", "156965516", RegistryValueKind.DWord, "1", null)
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.SecurityNote = "This setting can stop Windows from starting on some NVMe SSDs. Have a recovery option ready."; t.LessSecureOption = On; t.Source = @"Setup\4-Tweaking\Experimental\native_nvme_drivers\*"; }),

        Choice("win32-priority-separation", "Processor scheduling (Win32PrioritySeparation)",
                "How long a thread runs before Windows switches to another (the quantum), whether that length varies, and how much longer the program in front runs. On Windows 11, 2 picks the same short, variable quanta with a 3:1 boost as 38. Takes effect right away.",
                Pages.Windows, Pages.Performance, "Scheduling and MMCSS",
                ("38", "38 · short, variable, 3:1 (KOS)"), ("42", "42 · short, fixed, 3:1"), ("40", "40 · short, fixed, no boost"), ("24", "24 · long, fixed, no boost"), ("2", "2 (Windows)"))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation"), ("38", S("38")), ("42", S("42")), ("40", S("40")), ("24", S("24")), ("2", S("2")))
            .With(t => { t.KosDefault = "38"; t.WindowsDefault = "2"; t.Source = @"bin\2\TheKOS_reg.reg (Win32PrioritySeparation)"; }),

        Choice("disable-dynamic-tick", "disabledynamictick (BCD)",
                "Turns off the tickless timer: the clock interrupt keeps firing while the processor idles, for steadier timer behavior at a small cost in idle power.",
                Pages.Windows, Pages.Performance, "Timers (BCD)", ("unset", Txt.NotConfiguredOption), ("Yes", "Yes (KOS)"), ("No", "No"))
            .Add(new BcdSetting("disabledynamictick"), ("unset", Del), ("Yes", S("Yes")), ("No", S("No")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "Yes"; t.WindowsDefault = "unset"; t.Source = "RunOnce.cmd (bcdedit /set disabledynamictick yes)"; }),

        Choice("use-platform-clock", "useplatformclock (BCD)",
                "Forces HPET as the performance counter. It is slower to read than the processor's own counter (TSC), which games and Windows read constantly; KOS removes it.",
                Pages.Windows, Pages.Performance, "Timers (BCD)", ("unset", "Not set (KOS)"), ("Yes", "Yes (HPET)"), ("No", "No"))
            .Add(new BcdSetting("useplatformclock"), ("unset", Del), ("Yes", S("Yes")), ("No", S("No")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "unset"; t.WindowsDefault = "unset"; t.Source = "RunOnce.cmd (bcdedit /deletevalue useplatformclock)"; }),

        Choice("use-platform-tick", "useplatformtick (BCD)", "Use the RTC as the clock tick source. Don't change it if HPET is disabled in the BIOS.",
                Pages.Windows, Pages.Performance, "Timers (BCD)", ("unset", Txt.NotConfiguredOption), ("Yes", "Yes (RTC)"), ("No", "No (TSC)"))
            .Add(new BcdSetting("useplatformtick"), ("unset", Del), ("Yes", S("Yes")), ("No", S("No")))
            .With(t => { t.RequiresReboot = true; t.KosDefault = "unset"; t.Source = Bats + "useplatformtick.cmd; RunOnce.cmd"; }),

        Choice("tsc-sync-policy", "tscsyncpolicy (BCD)", "TSC synchronization policy across cores.",
                Pages.Windows, Pages.Performance, "Timers (BCD)", ("unset", Txt.NotConfiguredOption), ("Enhanced", "Enhanced"), ("Legacy", "Legacy"))
            .Add(new BcdSetting("tscsyncpolicy"), ("unset", Del), ("Enhanced", S("Enhanced")), ("Legacy", S("Legacy")))
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "Tscsyncpolicy.bat"; }),

        Toggle("increase-user-va", "IncreaseUserVA 3072 (BCD)", "Raises the user virtual address space to 3072 MB.",
                Pages.Windows, Pages.Performance, "Timers (BCD)")
            .Add(new BcdSetting("increaseuserva"), (On, S("3072")), (Off, Del))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = Off; t.Source = Bats + "Increase VA.bat"; }),

        // ======================= WINDOWS › SECURITY =======================
        Toggle("uac", "User Account Control (UAC)", "Confirmation prompts before administrative changes, and file virtualization (luafv).",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", RegistryValueKind.DWord, "1", "0")
            .Reg(LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "PromptOnSecureDesktop", RegistryValueKind.DWord, "1", "0")
            .Reg(LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", RegistryValueKind.DWord, "5", "0")
            .Svc("luafv", "2", "4")
            .AddSpec(RegistrySetting.ServiceStart("Appinfo"), false, (On, S("3")), (Off, Any))
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.Source = WinFeatures + "LUA Enable-Disable.bat; " + ProfileScript;
                t.SecurityNote = "Without UAC, every program an administrator starts runs with full privileges and no prompt. So does malware.";
            }),

        Toggle("dep", "Data Execution Prevention (DEP)", "Stops code from running in data memory regions (bcdedit nx).",
                Pages.Windows, Pages.Security, "Protections")
            .Add(new BcdSetting("nx"), (On, S("OptIn")), (Off, S("AlwaysOff")))
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.Source = WinFeatures + "DEP-Disable-Enable.bat; " + ProfileScript;
                t.SecurityNote = "Without DEP, memory exploits that Windows would block can run code.";
            }),

        Toggle("cfg", "Control Flow Guard (CFG)",
                "System-wide control-flow protection. The Basic profile uses the Windows default; Advanced disables it.",
                Pages.Windows, Pages.Security, "Protections")
            .Add(new CfgSetting(), (On, S("on")), (Off, S("off")))
            .Script(On, new ScriptRef(WinFeatures + "CFG Enable-Disable.cmd", "39F5326389E05CED10C29696244648F1241BC7C6E024ADAB109D95DBB8F00A12") { Args = "on" })
            .Script(Off, new ScriptRef(WinFeatures + "CFG Enable-Disable.cmd", "39F5326389E05CED10C29696244648F1241BC7C6E024ADAB109D95DBB8F00A12") { Args = "off" })
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.Source = ProfileScript + "; " + WinFeatures + "CFG Enable-Disable.cmd";
                t.SecurityNote = "Without CFG, memory corruption attacks can redirect program execution. Some games and anti-cheats may also stop working.";
            }),

        Toggle("process-mitigations", "Process mitigations",
                "Mitigation masks for dwm.exe, lsass.exe, csrss.exe and seven other Windows processes. The Basic profile removes KOS overrides; Advanced disables them.",
                Pages.Windows, Pages.Security, "Protections")
            .Add(new ProcessMitigationsSetting(), (On, S("on")), (Off, S("off")))
            .Script(On, SecurityScript("process", "on")).Script(Off, SecurityScript("process", "off"))
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.Source = ProfileScript + "; " + WinFeatures + "Security Feature Toggle.ps1";
                t.SecurityNote = "Without these mitigations, memory corruption attacks against Windows processes are easier.";
            }),

        Toggle("spectre-meltdown", "Spectre/Meltdown mitigations",
                "CPU mitigations controlled by FeatureSettingsOverride. Enabling writes the documented values that turn them on (override 0, mask 3). The KOS Advanced profile disables them only on Intel processors.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, MemMgmt, "FeatureSettings", RegistryValueKind.DWord, null, "1")
            .Add(R(LocalMachine, MemMgmt, "FeatureSettingsOverride"), (On, Target.SetOrAbsent("0")), (Off, S("3")))
            .Add(R(LocalMachine, MemMgmt, "FeatureSettingsOverrideMask"), (On, Target.SetOrAbsent("3")), (Off, S("3")))
            .With(t =>
            {
                // Advanced has no default: the profile only turns it off on Intel processors.
                t.RequiresReboot = true; t.WindowsDefault = On; t.ProfileDefaults = (Basic: On, Advanced: null); t.LessSecureOption = Off;
                t.Source = ProfileScript;
                t.SecurityNote = "Without these mitigations, malicious programs can read other processes' memory through side-channel attacks.";
            }),

        Toggle("lsa-protection", "LSA protection",
                "Runs the Local Security Authority (lsass.exe) as a protected process, which blocks credential-dumping tools. Plug-ins that aren't signed for it (some smart card or VPN software) stop loading. A value of 1 (enabled with the UEFI lock) shows as Unknown.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL", RegistryValueKind.DWord, "2", "0")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = "RunAsPPL; 2 = enabled without the UEFI lock, Windows 11 22H2+";
                t.SecurityNote = "Without LSA protection, any program running as administrator can read sign-in credentials from lsass.exe memory.";
            }),

        Toggle("sehop", "Exception chain validation (SEHOP)",
                "Structured Exception Handler Overwrite Protection: blocks exploits that overwrite exception handlers.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, Kernel, "DisableExceptionChainValidation", RegistryValueKind.DWord, "0", "1")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = "DisableExceptionChainValidation";
                t.SecurityNote = "Without SEHOP, a class of memory exploits that Windows would block can take over a program.";
            }),

        Toggle("xfg", "eXtended Flow Guard (XFG)",
                "XFG, the stricter version of Control Flow Guard that also checks the function's type at each indirect call. Off disables it.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, Kernel, "DisableControlFlowGuardXfg", RegistryValueKind.DWord, "0", "1")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = "DisableControlFlowGuardXfg";
                t.SecurityNote = "Without XFG, an attacker who corrupts memory has more places an indirect call can be redirected to.";
            }),

        Toggle("cfg-export-suppression", "CFG export suppression",
                "Control Flow Guard's export suppression: exported functions aren't valid call targets until a program resolves them. Off disables it.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, Kernel, "DisableControlFlowGuardExportSuppression", RegistryValueKind.DWord, "0", "1")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = "DisableControlFlowGuardExportSuppression";
                t.SecurityNote = "Without export suppression, every exported function is a valid target for a redirected call.";
            }),

        Toggle("drive-remapping-mitigation", "Drive remapping mitigation",
                "Stops a program from remapping drive letters to redirect what an elevated process opens. On = the Windows default (absent); off = 0.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, Kernel, "DriveRemappingMitigation", RegistryValueKind.DWord, null, "0")
            .With(t =>
            {
                t.RequiresReboot = true; t.WindowsDefault = On; t.LessSecureOption = Off; t.Source = "DriveRemappingMitigation";
                t.SecurityNote = "Without this mitigation, a program can remap a drive letter so that an elevated process loads its files instead of the real ones.";
            }),

        Toggle("driver-blocklist", "Vulnerable driver blocklist",
                "Blocks drivers Microsoft lists as vulnerable or malicious. Some old hardware and overclocking tools use drivers on the list. " +
                "So does the KOS sign-in tweak that turns off USB interrupt moderation (XHCI IMOD, through RW): while the blocklist is on, that tweak is skipped.",
                Pages.Windows, Pages.Security, "Protections")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable", RegistryValueKind.DWord, "1", "0")
            .With(t =>
            {
                t.RequiresReboot = true; t.WindowsDefault = On; t.LessSecureOption = Off; t.Source = "VulnerableDriverBlocklistEnable";
                t.SecurityNote = "Without the blocklist, malware running as administrator can load a known vulnerable driver to take over the kernel.";
            }),

        Toggle("firewall", "Windows Firewall", "The Firewall (mpssvc) and Base Filtering Engine (BFE) services.",
                Pages.Windows, Pages.Security, "Protections")
            .Svc("mpssvc", "2", "4").Svc("BFE", "2", "4")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = WinFeatures + "Firewall Enable-Disable.bat";
                t.SecurityNote = "Without a firewall, services that listen on the network are exposed. Disabling BFE also affects VPNs and Internet Connection Sharing.";
            }),

        Toggle("firewall-rules", "Windows Firewall rules",
                "Whether the default Windows Firewall rules are kept. Advanced clears the rules while leaving the firewall service running. Only the Windows default rules are compared: rules that apps add later don't change the state.",
                Pages.Windows, Pages.Security, "Protections")
            .Add(new FirewallRulesSetting(), (On, S("on")), (Off, S("off")))
            .Script(On, SecurityScript("firewall", "on")).Script(Off, SecurityScript("firewall", "off"))
            .With(t =>
            {
                t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.ConfirmationOption = On;
                t.ConfirmationNote = "Restoring the default Windows Firewall rules resets the firewall and permanently removes rules created by you or your apps.";
                t.Source = ProfileScript + "; " + WinFeatures + "Security Feature Toggle.ps1";
                t.SecurityNote = "Clearing every firewall rule may expose services and permanently removes custom rules.";
            }),

        Toggle("vbs", "Virtualization-based security (VBS)",
                "Runs parts of Windows in a hypervisor-protected area. Memory integrity and Credential Guard need it, and so do some anti-cheats (for example Vanguard, FACEIT). " +
                "Enabling also turns on the hypervisor, System Guard and Credential Guard (without the UEFI lock, so it can be turned off again from here). The state shown is the configured one; it takes effect after a restart.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Reg(LocalMachine, DgPolicy, "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, "1", "0")
            .Reg(LocalMachine, DgPolicy, "RequirePlatformSecurityFeatures", RegistryValueKind.DWord, null, null)
            .Reg(LocalMachine, DgPolicy, "LsaCfgFlags", RegistryValueKind.DWord, null, null)
            .Reg(LocalMachine, DgPolicy, "ConfigureSystemGuardLaunch", RegistryValueKind.DWord, "1", null)
            .AddSpec(R(LocalMachine, Dg, "EnableVirtualizationBasedSecurity"), false, (On, Any), (Off, S("0")))
            .Reg(LocalMachine, Dg, "RequirePlatformSecurityFeatures", RegistryValueKind.DWord, "1", "0")
            .AddSpec(R(LocalMachine, Dg, "Mandatory"), false, (On, Any), (Off, Target.SetOrAbsent("0")))
            .Add(R(LocalMachine, Dg + @"\Scenarios\SystemGuard", "Enabled"), (On, S("1")), (Off, Target.SetOrAbsent("0")))
            .AddSpec(R(LocalMachine, Dg + @"\Scenarios\CredentialGuard", "Enabled"), false, (On, Any), (Off, Target.SetOrAbsent("0")))
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", RegistryValueKind.DWord, "2", "0")
            .Add(new BcdSetting("vsmlaunchtype"), (On, Target.SetOrAbsent("Auto")), (Off, S("Off")))
            .AddSpec(new BcdSetting("hypervisorlaunchtype"), false, (On, Target.SetOrAbsent("Auto")), (Off, Any))
            .With(t =>
            {
                // Basic has no default: the profile leaves VBS as Windows decided at OOBE.
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: null, Advanced: Off); t.LessSecureOption = Off;
                t.Source = WinFeatures + "HVCI-VBS Enable-Disable.cmd; " + ProfileScript;
                t.SecurityNote = "Without VBS, memory integrity and Credential Guard stop too, and some anti-cheats refuse to run.";
                t.SecurityDetail = () => SystemInfo.AntiCheats.Note(SystemInfo.AntiCheats.Installed(), memoryIntegrity: false);
            }),

        Toggle("hvci", "Memory integrity (HVCI)",
                "Core isolation › Memory integrity: VBS checks every kernel driver before it runs, which blocks vulnerable and unsigned drivers. Works only with VBS on. " +
                "While it runs, Windows blocks the RW driver, so the KOS USB interrupt moderation tweak at sign-in is skipped.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Reg(LocalMachine, DgPolicy, "HypervisorEnforcedCodeIntegrity", RegistryValueKind.DWord, null, null)
            .Reg(LocalMachine, Dg + @"\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", RegistryValueKind.DWord, "1", "0")
            .Reg(LocalMachine, Dg + @"\Scenarios\HypervisorEnforcedCodeIntegrity", "HVCIMATRequired", RegistryValueKind.DWord, "1", "0")
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: null, Advanced: Off); t.LessSecureOption = Off;
                t.Source = WinFeatures + "HVCI-VBS Enable-Disable.cmd; " + ProfileScript;
                t.SecurityNote = "Without memory integrity, malicious or vulnerable drivers can modify the kernel.";
                t.SecurityDetail = () => SystemInfo.AntiCheats.Note(SystemInfo.AntiCheats.Installed(), memoryIntegrity: true);
            }),

        Toggle("hyper-v", "Hyper-V",
                "Everything virtual machines, WSL 2, Docker and Android emulators need: the optional features (Hyper-V, Virtual Machine Platform, Windows Hypervisor Platform), the Hyper-V drivers and services, the virtualization infrastructure device and the hypervisor at boot. " +
                "Turning it off also disables the Hyper-V guest drivers, so don't turn it off inside a Hyper-V virtual machine. The optional features take a few minutes; the state shown is the configured one, which takes effect after a restart.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Add(new OptionalFeatureSetting("VirtualMachinePlatform"), (On, S("1")), (Off, S("0")))
            .Add(new OptionalFeatureSetting("HypervisorPlatform"), (On, S("1")), (Off, S("0")))
            // Off only needs the features: the drivers keep their Windows start types until the script disables them.
            .With(t => { foreach (var (svc, start) in new[] { ("vid", "1"), ("hvservice", "3"), ("HvHost", "3"), ("gencounter", "3"), ("hyperkbd", "3"), ("hypervideo", "3"),
                ("spaceparser", "3"), ("storflt", "3"), ("vmgid", "3"), ("bttflt", "0"), ("vpci", "0") })
                t.AddSpec(RegistrySetting.ServiceStart(svc), true, (On, S(start)), (Off, Any)); })
            .AddSpec(new BcdSetting("hypervisorlaunchtype"), false, (On, Target.SetOrAbsent("Auto")), (Off, Any))
            .Script(On, HyperVScript("on")).Script(Off, HyperVScript("off"))
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = Off; t.Source = WinFeatures + "HyperV Enable-Disable.cmd; RunOnce.cmd (gencounter, storflt, hvservice, HvHost)"; }),

        Choice("ps-execution-policy", "PowerShell script execution policy",
                "Which PowerShell scripts can run: Restricted runs none, RemoteSigned needs a signature on scripts downloaded from the internet, Unrestricted runs all of them (asking before downloaded ones). Set for the machine and for this user, whose setting wins.",
                Pages.Windows, Pages.Security, "Protections", ("Restricted", "Restricted (Windows)"), ("RemoteSigned", "RemoteSigned"), ("Unrestricted", "Unrestricted (KOS)"))
            .Add(Sz(LocalMachine, PsPolicy, "ExecutionPolicy"), ("Restricted", Target.SetOrAbsent("Restricted")), ("RemoteSigned", S("RemoteSigned")), ("Unrestricted", S("Unrestricted")))
            .Add(Sz(CurrentUser, PsPolicy, "ExecutionPolicy"), ("Restricted", Target.SetOrAbsent("Restricted")), ("RemoteSigned", Target.SetOrAbsent("RemoteSigned")), ("Unrestricted", Target.SetOrAbsent("Unrestricted")))
            .With(t =>
            {
                t.KosDefault = "Unrestricted"; t.WindowsDefault = "Restricted"; t.LessSecureOption = "Unrestricted";
                t.Source = @"RunOnce.cmd (Set-ExecutionPolicy); bin\2\TheKOS_reg.reg (ExecutionPolicy)";
                t.SecurityNote = "With Unrestricted, any PowerShell script can run, including one a malicious download leaves behind.";
            }),

        Toggle("hypervisor", "Hypervisor (hypervisorlaunchtype)",
                "Loading of the hypervisor at boot (bcdedit). VBS and Hyper-V need it; turning Hyper-V on sets it too.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Add(new BcdSetting("hypervisorlaunchtype"), (On, S("Auto")), (Off, S("Off")))
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off);
                t.Source = WinFeatures + "HyperV Enable-Disable.cmd; " + ProfileScript;
            }),

        Toggle("dma-remapping", "Driver DMA remapping",
                "Hardware DMA protection declared by drivers. Advanced saves each original value and writes zero; Basic restores the saved values.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Add(new DmaRemappingSetting(), (On, S("on")), (Off, S("off")))
            .Script(On, SecurityScript("dma", "on")).Script(Off, SecurityScript("dma", "off"))
            .With(t =>
            {
                t.RequiresReboot = true; t.ProfileDefaults = (Basic: On, Advanced: Off); t.LessSecureOption = Off;
                t.Source = ProfileScript + "; " + WinFeatures + "Security Feature Toggle.ps1";
                t.SecurityNote = "Without DMA remapping, a malicious or compromised device can access system memory more directly.";
            }),

        Toggle("kernel-shadow-stacks", "Kernel-mode hardware-enforced stack protection",
                "Uses CPU shadow stacks to protect kernel return addresses. Works only with memory integrity (Core isolation) on and an Intel 11th gen / AMD Zen 3 or newer CPU. Some anti-cheats and older drivers are incompatible.",
                Pages.Windows, Pages.Security, "Virtualization")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\KernelShadowStacks", "Enabled", RegistryValueKind.DWord, "1", "0")
            .With(t =>
            {
                t.RequiresReboot = true; t.LessSecureOption = Off; t.Source = "Kernel Shadow Stacks";
                t.SecurityNote = "Without stack protection, kernel exploits that overwrite return addresses aren't stopped by the CPU.";
            }),

        Toggle("login-screen-command-prompt", "Command prompt on sign-in screen (Utilman)",
                "Replaces the accessibility app on the sign-in screen with cmd.exe. Anyone at the computer can open a SYSTEM command prompt before signing in.",
                Pages.Windows, Pages.Security, "Protections")
            .Add(new UtilmanSetting(), (On, S("on")), (Off, S("off")))
            .Script(On, new ScriptRef(WinFeatures + "Utilman Enable-Disable.cmd", "2D3461ACC52C68D24797EFD8D342BAA84F251028D92EFB972ADE29D886EB91B5") { Args = "on", Timeout = TimeSpan.FromMinutes(15) })
            .Script(Off, new ScriptRef(WinFeatures + "Utilman Enable-Disable.cmd", "2D3461ACC52C68D24797EFD8D342BAA84F251028D92EFB972ADE29D886EB91B5") { Args = "off", Timeout = TimeSpan.FromMinutes(15) })
            .With(t =>
            {
                t.KosDefault = Off; t.WindowsDefault = Off; t.LessSecureOption = On;
                t.Source = WinFeatures + "Utilman Enable-Disable.cmd";
                t.SecurityNote = "This allows anyone with physical access to open a SYSTEM command prompt at the sign-in screen, without knowing a password.";
            }),

        // ======================= WINDOWS › CONNECTIVITY =======================
        Toggle("wifi", "Wi-Fi", "WLAN service and the virtual Wi-Fi filter. Enabling also turns on netprofm and NlaSvc. On first boot, KOS enables it on laptops and disables it on desktops.",
                Pages.Windows, Pages.Connectivity, "Wireless")
            .Svc("WlanSvc", "2", "4").Svc("vwififlt", "1", "4", optional: true)
            .AddSpec(RegistrySetting.ServiceStart("netprofm"), false, (On, S("3")), (Off, Any))
            .AddSpec(RegistrySetting.ServiceStart("NlaSvc"), false, (On, S("2")), (Off, Any))
            .Script(On, new ScriptRef(WinFeatures + "WIFI Enable-Disable.bat", "A58AF4BD8D5C0F5DECBEC17823F552FBCF04FE332B730C5F641F9CC4BCB8C26A") { Args = "1" })
            .Script(Off, new ScriptRef(WinFeatures + "WIFI Enable-Disable.bat", "A58AF4BD8D5C0F5DECBEC17823F552FBCF04FE332B730C5F641F9CC4BCB8C26A") { Args = "2" })
            .With(t => { t.RequiresReboot = true; t.Source = WinFeatures + "WIFI Enable-Disable.bat; bin\\2\\DeviceProfile.cmd"; }),

        Toggle("bluetooth", "Bluetooth", "Bluetooth services and drivers (including Bluetooth networking and the Intel Bluetooth driver), the Bluetooth devices and the device cleanup task. On first boot, KOS enables them on laptops and disables them on desktops.",
                Pages.Windows, Pages.Connectivity, "Wireless")
            .SvcList(new[] { "bthserv", "BTAGService", "BluetoothUserService", "BthAvctpSvc", "RFCOMM", "BthEnum", "BthLEEnum", "BthA2dp", "BthPan", "ibtusb",
                "BthHFEnum", "BthMini", "BTHMODEM", "BTHPORT", "BTHUSB", "HidBth", "Microsoft_Bluetooth_AvrcpTransport" }, "3", "4")
            .Script(On, new ScriptRef(WinFeatures + "Bluetooth Enable-Disable.bat", "3C9407249D5713FE68C9DE3EA3D6C6057534EB2468AA0400A63FA7227F61D610") { Args = "1" })
            .Script(Off, new ScriptRef(WinFeatures + "Bluetooth Enable-Disable.bat", "3C9407249D5713FE68C9DE3EA3D6C6057534EB2468AA0400A63FA7227F61D610") { Args = "2" })
            .With(t => { t.RequiresReboot = true; t.Source = WinFeatures + "Bluetooth Enable-Disable.bat; bin\\2\\DeviceProfile.cmd"; }),

        Toggle("gdid", "GDID tracking",
                "Connected Devices Platform and Microsoft account services (CDPSvc, wlidsvc), and domain blocking in the hosts file. With tracking disabled, Microsoft Store sign-in and downloads can fail.",
                Pages.Windows, Pages.Connectivity, "Online services")
            .Add(Sz(LocalMachine, @"SOFTWARE\TheKOS", "GDIDTRACKING"), (On, S("0")), (Off, S("1")))
            .Svc("CDPSvc", "2", "4").Svc("CDPUserSvc", "2", "4", optional: true).Svc("wlidsvc", "3", "4")
            .Add(new HostsGdidSetting(), (On, S("0")), (Off, S("1")))
            .Script(On, new ScriptRef(WinFeatures + "GDID-Tracking.cmd", "BFC5823C5E2990140DF40664E1E64A8DC7D82DA4C84537AAA95F05F50F6D858B") { Args = "2" })
            .Script(Off, new ScriptRef(WinFeatures + "GDID-Tracking.cmd", "BFC5823C5E2990140DF40664E1E64A8DC7D82DA4C84537AAA95F05F50F6D858B") { Args = "1" })
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.Source = WinFeatures + "GDID-Tracking.cmd; RunOnce.cmd"; }),

        Toggle("remote-desktop", "Remote Desktop",
                "Lets other computers connect to this one with Remote Desktop (Pro editions and higher). Also sets the Remote Desktop services to manual (on) or disabled (off) and turns the Remote Desktop firewall rules on or off.",
                Pages.Windows, Pages.Connectivity, "Remote access")
            .Reg(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", RegistryValueKind.DWord, "0", "1")
            .Svc("TermService", "3", "4", optional: true).Svc("SessionEnv", "3", "4", optional: true).Svc("UmRdpService", "3", "4", optional: true)
            .AddSpec(new FirewallRuleSetting("RemoteDesktop-UserMode-In-TCP", "RemoteDesktop-UserMode-In-UDP", "RemoteDesktop-Shadow-In-TCP"), true, (On, S("1")), (Off, S("0")))
            .With(t =>
            {
                t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = Off; t.LessSecureOption = On; t.Source = "RunOnce.cmd (TermService, UmRdpService, SessionEnv)";
                t.SecurityNote = "Remote Desktop accepts sign-ins from the network. Use a strong password and keep it off on untrusted networks.";
            }),

        Toggle("network-throttling", "Network throttling (MMCSS)",
                "Limits how many network packets per millisecond other traffic gets while multimedia plays (NetworkThrottlingIndex 10). Off removes the limit; on some network drivers that raises DPC latency, so test it.",
                Pages.Windows, Pages.Connectivity, "Network")
            .Add(R(LocalMachine, Mm, "NetworkThrottlingIndex"), (On, Target.SetOrAbsent("10")), (Off, S("4294967295")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = "NetworkThrottlingIndex"; }),

        Toggle("nagle", "Nagle's algorithm",
                "Groups small TCP packets before sending them. Off sends them at once (TcpAckFrequency and TCPNoDelay on every network interface), which can lower latency in some games at the cost of a little bandwidth; test it.",
                Pages.Windows, Pages.Connectivity, "Network")
            .Add(new NagleSetting(), (On, S("1")), (Off, S("0")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = Bats + "Nagle Algorithm.bat"; }),

        Toggle("network-power-management", "Network power management",
                "Power management of the Windows connection manager (fDisablePowerManagement policy) and the DNS client's DisableCoalescing value. Off keeps Windows from saving power on network connections. Each adapter's own power options are on the Network page.",
                Pages.Windows, Pages.Connectivity, "Network")
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\Local", "fDisablePowerManagement"), (On, Del), (Off, S("1")))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WcmSvc\GroupPolicy", "fDisablePowerManagement"), (On, Del), (Off, S("1")))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "DisableCoalescing"), (On, Del), (Off, S("1")))
            .With(t => { t.RequiresReboot = true; t.WindowsDefault = On; t.Source = "fDisablePowerManagement; DisableCoalescing"; }),

        Toggle("vpn-services", "VPN services",
                "The Windows VPN stack (Remote Access, IKE and AuthIP, IPsec Policy Agent, SSTP) that the built-in VPN client and some VPN apps need. Enabling also turns on the firewall, DHCP, DNS and network awareness services they depend on; disabling leaves those alone.",
                Pages.Windows, Pages.Connectivity, "Network")
            .SvcList(new[] { "RasMan", "RasAuto", "IKEEXT", "PolicyAgent", "SstpSvc", "RASSSTP" }, "3", "4")
            .With(t => { foreach (var (svc, start) in new[] { ("BFE", "2"), ("mpssvc", "2"), ("Dhcp", "2"), ("Dnscache", "2"), ("NlaSvc", "2"), ("netprofm", "3") })
                t.AddSpec(RegistrySetting.ServiceStart(svc), true, (On, S(start)), (Off, Any)); })
            .With(t => { t.RequiresReboot = true; t.KosDefault = Off; t.WindowsDefault = On; t.Source = "RunOnce.cmd (IKEEXT, SstpSvc, RasMan, RasAuto, PolicyAgent, RASSSTP)"; }),

        // ======================= POWER =======================
        Choice("power-savings", "Power saving profile",
                "Desktop turns off USB/PCI device power saving, PCIe link power management, the disk idle timeout, AHCI link power management, hibernation and Power Throttling, and activates the KOS plan (co.pow). Laptop turns power saving back on and activates the Balanced plan.",
                Pages.Power, null, "KOS profiles", ("desktop", "Desktop"), ("laptop", "Laptop"))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\USBHUB3\Parameters", "DisableLPM"), ("desktop", S("1")), ("laptop", Del))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Services\pci\Parameters", "D3ColdSupported"), ("desktop", S("0")), ("laptop", Del))
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "EnergyEstimationEnabled"), ("desktop", S("0")), ("laptop", S("1")))
            .Add(new PowerSetting(new Guid("501a4d13-42af-4429-9fd1-a8218c268e20"), new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5"), "PCI Express link state power management"),
                ("desktop", S("0")), ("laptop", S("1")))
            .Script("desktop", new ScriptRef(Bats + "Configure Powersavings.bat", "5D506DBB86531AAB77A7F95541DB2543F3CF9C4BCA6ED31BFDBC7EE5A6C22608") { Args = "desktop" })
            .Script("laptop", new ScriptRef(Bats + "Configure Powersavings.bat", "5D506DBB86531AAB77A7F95541DB2543F3CF9C4BCA6ED31BFDBC7EE5A6C22608") { Args = "laptop" })
            .With(t => { t.RequiresReboot = true; t.Source = Bats + "Configure Powersavings.bat; bin\\2\\DeviceProfile.cmd"; }),

        Toggle("processor-idle", "Processor idle states",
                "Lets the CPU enter power-saving states when idle, in the active power plan (plugged in). Disabling keeps the CPU always active (more power use and heat).",
                Pages.Power, null, "KOS profiles")
            .Add(new PowerSetting(new Guid("54533251-82be-4824-96c1-47b60b740d00"), new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad"), "Processor idle disable"),
                (On, S("0")), (Off, S("1")))
            .With(t => { t.Source = @"Setup\4-Tweaking\PowerPlan\enable-idle.bat, disable-idle.bat"; }),

        // Missing policies hold for both options: HibernateEnabled decides (the Desktop/Laptop profile only runs powercfg).
        Toggle("hibernation", "Hibernation",
                "Hibernate, with its hibernation file hiberfil.sys (powercfg /hibernate; enabling makes it the full size Hibernate needs), the AllowHibernate policy and the Hibernate entry in the power menu (ShowHibernateOption policy). Hybrid sleep and fast startup also need it. The Desktop/Laptop profile turns it off or on too.",
                Pages.Power, null, "Sleep and hibernation")
            .Add(R(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "AllowHibernate"), (On, Target.SetOrAbsent("1")), (Off, Target.SetOrAbsent("0")))
            .Add(R(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "ShowHibernateOption"), (On, Target.SetOrAbsent("1")), (Off, Target.SetOrAbsent("0")))
            .Add(new HibernationSetting(), (On, S("1")), (Off, S("0")))
            .With(t => { t.WindowsDefault = On; t.Source = Bats + "Configure Powersavings.bat (powercfg /h)"; }),
    };

    static ScriptRef WuScript(string arg) =>
        new(WinFeatures + "Windows Update Enable-Disable.cmd", "040CD611B3D2505C724F69A04E1CBCD6C040B71C317DC78A27A583AC1AC610FA") { Args = arg };

    static ScriptRef StartMenuScript(string arg) =>
        new(WinFeatures + "Start Menu Enable-Disable.bat", "CC3288D9A1EA660860E15AFB3D169EC523A99AA1860C0D66837F9695B363D039") { Args = arg };

    static ScriptRef SearchScript(string arg) =>
        new(WinFeatures + "Windows Search Enable-Disable.cmd", "5FAE48DCC03CE674C66F80FA23B7B867FC05356E6946AC2B31AAB34B9B6CBA90") { Args = arg };

    static ScriptRef HyperVScript(string arg) =>
        new(WinFeatures + "HyperV Enable-Disable.cmd", "4F922AF38E974A0DCBC375509E8D4326A435983FADD59E9743061A394F5F29F6") { Args = arg, Timeout = TimeSpan.FromMinutes(20) };

    static ScriptRef SerialScript(string arg) =>
        new(WinFeatures + "Serial Ports Enable-Disable.cmd", "92A8492402161C714E492F4346AE7FD5408342F208DB77C8AFE53FDA152813B5") { Args = arg };

    static ScriptRef SecurityScript(string feature, string mode) =>
        new(WinFeatures + "Security Feature Toggle.ps1", "EB27D188B68FDFE5C0D76D6E7F294EDF83A4AB06CCA4DDEBF11852279053F0AF")
        { Args = $"{feature} {mode}", Timeout = TimeSpan.FromMinutes(15) };

    static List<ActionItem> BuildActions() => new()
    {
        new() { Id = "restart-explorer", Name = "Restart Explorer", Description = "Applies interface settings that depend on Explorer.",
            Page = Pages.Tools, Group = "System", ButtonText = Txt.Restart, Run = RestartExplorerAsync, Source = "Alt-Tab-Settings.bat (taskkill explorer)" },
        new() { Id = "reboot-uefi", Name = "Restart to UEFI settings", Description = "Restarts the computer now, straight into the UEFI (BIOS) setup. Save your work first. Needs a UEFI system.",
            Page = Pages.Tools, Group = "System", ButtonText = Txt.Restart, Run = SystemFixes.RestartToFirmwareAsync, RunElevated = true, Source = "shutdown /r /fw /t 0" },
        new() { Id = "cleanup", Name = "KOS cleanup", Description = "Removes devices missing for 7 days, Windows and user logs and temporary files, and the Windows Update download cache.",
            Page = Pages.Tools, Group = "Maintenance", ButtonText = "Clean up",
            Script = new ScriptRef(@"Setup\5-Cleanup\TheKOS Cleanup Script.cmd", "13D41BA7260FCD089CC63FD122D03D87B6F76C392C3436CCA838970D0E613B8C") { Timeout = TimeSpan.FromMinutes(10) },
            Source = @"Setup\5-Cleanup\TheKOS Cleanup Script.cmd" },
        new() { Id = "clear-eventlogs", Name = "Clear event logs", Description = "Clears every classic Event Viewer log; what they recorded can't be used for troubleshooting afterwards.",
            Page = Pages.Tools, Group = "Maintenance", ButtonText = "Clear",
            Run = EventLogs.ClearAllAsync, RunElevated = true,
            Source = @"Setup\5-Cleanup\Clear EventLogs.ps1 (converted to C#: ClearEventLog for each log under Services\EventLog)" },
        new() { Id = "fix-vhd", Name = "Fix Disk Management/VHD", Description = "Turns vds, defragsvc, vdrvroot and the virtual drive enumerator back on.",
            Page = Pages.Tools, Group = "Fixes", ButtonText = "Fix", RequiresReboot = true,
            Script = new ScriptRef(Fixes + "Fix VHD-Disk Management.bat", "9B6E2E19DCB3EC1737EB186D066192568509DBFB0B8AD665446406AF5BF75CAA"),
            Source = Fixes + "Fix VHD-Disk Management.bat" },
        new() { Id = "fix-cdrom", Name = "Fix CD/DVD drive", Description = "Turns the cdrom driver back on.",
            Page = Pages.Tools, Group = "Fixes", ButtonText = "Fix", RequiresReboot = true,
            Script = new ScriptRef(Fixes + "Fix CD-Disk.bat", "CB9DCA9679DCBF8A688496888726A964BD21F8CC3DE1D9B61D346CB230ABCB87"),
            Source = Fixes + "Fix CD-Disk.bat" },
        new() { Id = "network-reset", Name = "Reset network", Description = "For slow or broken internet after network tweaks: removes the power-saving and wake overrides from every network adapter (each driver goes back to its default, undoing the Network page's changes) and the network power policies, resets Winsock and TCP/IP, renews the IP address and flushes the DNS and ARP caches. The connection drops for a moment. Firewall rules and Nagle's setting stay.",
            Page = Pages.Tools, Group = "Fixes", ButtonText = "Reset", RequiresReboot = true,
            Run = SystemFixes.ResetNetworkAsync, RunElevated = true, Source = "netsh winsock reset, netsh int ip reset, ipconfig /release /renew /flushdns, arp -d" },
        new() { Id = "fix-time-sync", Name = "Fix time sync", Description = "Registers the Windows Time service again, keeps the server chosen in Time server (NTP) and syncs the clock now.",
            Page = Pages.Tools, Group = "Fixes", ButtonText = "Fix",
            Run = SystemFixes.FixTimeSyncAsync, RunElevated = true, Source = "w32tm /unregister, /register, /config /manualpeerlist, /resync" },
        new() { Id = "vbs-uefi-unlock", Name = "Clear the VBS UEFI lock",
            Description = "When VBS or Credential Guard was turned on \"with UEFI lock\", the setting is also stored in the firmware and Windows keeps it on even after the registry turns it off. " +
                "This schedules Microsoft's SecConfig.efi for the next restart: at boot it asks you to confirm (press the key it shows), then clears the lock. Turn VBS off and restart first; use this only if VBS still runs.",
            Page = Pages.Windows, Tab = Pages.Security, Group = "Virtualization", ButtonText = "Schedule", RequiresReboot = true,
            Run = SystemFixes.ScheduleVbsUnlockAsync, RunElevated = true, Source = "SecConfig.efi with DISABLE-LSA-ISO,DISABLE-VBS (Microsoft's steps for VBS with UEFI lock)" },
        new() { Id = "volume-mixer", Name = "Volume mixer", Description = "Per-app volume and output device (Settings › System › Sound › Volume mixer).",
            Page = Pages.Tools, Group = "Sound", ButtonText = Txt.Open, Run = _ => OpenShell("ms-settings:apps-volume"), Source = "ms-settings:apps-volume" },
        new() { Id = "sound-control-panel", Name = "Sound control panel", Description = "Playback and recording devices, default device, format, enhancements and exclusive mode.",
            Page = Pages.Tools, Group = "Sound", ButtonText = Txt.Open, Run = _ => OpenShell(Path.Combine(Environment.SystemDirectory, "control.exe"), "mmsys.cpl"), Source = "mmsys.cpl" },
        new() { Id = "open-setup", Name = "KOS setup scripts", Description = "Opens the Setup folder with the original scripts.",
            Page = Pages.Tools, Group = "Folders", ButtonText = Txt.Open, Run = _ => OpenFolder(@"%ProgramData%\TheKOS\Setup"), Source = @"ProgramData\TheKOS\Setup" },
        new() { Id = "open-postsetup-logs", Name = "Post-setup logs", Description = "Opens the logs written by the KOS RunOnce.",
            Page = Pages.Tools, Group = "Folders", ButtonText = Txt.Open, Run = _ => OpenFolder(@"%ProgramData%\TheKOS\Logs\PostSetup"), Source = "RunOnce.cmd" },
    };

    static Task<string> OpenShell(string target, string args = "")
    {
        Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true })?.Dispose();
        return Task.FromResult(Txt.Opened);
    }

    static Task<string> OpenFolder(string path)
    {
        var full = Environment.ExpandEnvironmentVariables(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(Txt.FolderNotFound(full));
        Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(full)) { UseShellExecute = true });
        return Task.FromResult(Txt.FolderOpened);
    }

    /// <summary>Closes the session's Explorer; Windows restarts it (AutoRestartShell). Only start it manually if it does not come back.</summary>
    static async Task<string> RestartExplorerAsync(CancellationToken ct)
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
            using (p) { try { p.Kill(); } catch (Exception ex) { Persistence.Log.Warn($"explorer: {ex.Message}"); } }
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (Process.GetProcessesByName("explorer").Any(p => p.SessionId == session)) return Txt.ExplorerRestarted;
        }
        if (Elevation.IsAdmin) return Txt.ExplorerNotBack;
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        return Txt.ExplorerRestarted;
    }

    // ---------- fluent helpers ----------
    static Tweak With(this Tweak t, Action<Tweak> configure) { configure(t); return t; }

    static Tweak SvcList(this Tweak t, IEnumerable<string> services, string on, string off)
    {
        foreach (var s in services) t.Svc(s, on, off, optional: true);
        return t;
    }

    /// <summary>Image File Execution Options redirect that stops each exe from starting: set in <paramref name="blocked"/>, absent in the other options.</summary>
    static Tweak BlockExes(this Tweak t, IEnumerable<string> exes, string debugger, string blocked)
    {
        foreach (var exe in exes)
            t.Add(Sz(LocalMachine, $@"{Ifeo}\{exe}", "Debugger"), t.Options.Select(o => (o.Key, o.Key == blocked ? S(debugger) : Del)).ToArray());
        return t;
    }
}
