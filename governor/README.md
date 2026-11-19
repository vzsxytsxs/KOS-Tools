# KOS Governor

Process rules in the style of Process Lasso. It used to be the toolbox's Processes page; now it is a separate program.

## How it runs

- **Two executables.** `KosGovernor.exe` is the interface (WPF). `KosGovernorEngine.exe` is the engine: no window, with a tray icon, the global gamemode hotkey and the rules. The engine is compiled with Native AOT (no JIT, no WPF runtime), because it runs all day. The interface starts the engine if it isn't running. Closing the window does not turn the rules off; *Exit* in the tray menu does.
  - `KosGovernor.exe --engine` still works: sign-in tasks created before the split start it, and it hands over to `KosGovernorEngine.exe`. Without the engine exe next to it (the interface run from its own build folder), the interface runs the engine itself.
  - Both read the same files through source-generated JSON (`GovernorJson`, and `KosJson` in the toolbox Core for the fan curve and GPU tuning), so the AOT engine needs no reflection. A test checks the format matches what the reflection-based serializer wrote.
- **Administrator.** The manifest asks for administrator, because changing elevated processes and services requires it. The engine enables SeDebug and SeIncreaseBasePriority, needed for high I/O priority and Realtime.
- **Engine.**
  - Every interval (default 3 s, in *General*; old configurations with 2 s move to 3 s) it reads the process list in a single call (`NtQuerySystemInformation`).
  - It applies each program's rule once per start. It also reapplies to the open programs when the configuration changes or the gamemode switches.
  - Thread rules are checked every interval on the threads that haven't matched yet, because the description may appear after the thread is created.
  - In an interval with no new programs, it uses less than 1 ms.
- **Start with Windows.** Creates a `KOS Governor` scheduled task at sign-in that starts `KosGovernorEngine.exe`, highest level, through XML: no 72 h limit, starts on battery and runs at normal priority. It only works with the program in *Program Files*, because the task runs the `.exe` as administrator without UAC.

## Features

- **Default rules** (`DefaultRules.cs`)
  - Priority boost off for shell, system, launcher and overlay processes: `explorer`, `dwm`, `svchost`, `audiodg`, `ctfmon`, `SearchHost`, Steam, EA app, Discord, NVIDIA, MSI Afterburner, Gaming Services and others.
  - `dwm.exe`: the *DWM Kernel Sensor Thread* and *DWM Master Input Thread* threads at Time Critical priority.
  - A new configuration starts with them. A configuration from an earlier version gets only the ones whose program has no rule yet (migration to version 3); existing rules aren't changed.
- **Window**
  - Sidebar: *Processes*, *Rules*, *Gamemode*, *CPU masks*, *RTSS* and *Settings*, each with a count (processes, rules of the active profile, On/Off, masks); at the bottom, the engine's state (running in the tray, starts with Windows, or the sign-in problem).
  - Above the engine card: the profile button (switch, new, duplicate, rename, delete) and the gamemode switch (the hotkey in its tooltip).
  - The status bar shows CPU, processes, threads, handles and memory, and the result of each action.
- **Processes**
  - Live list with icon, PID, CPU priority, I/O, memory, threads, status, boost, eco, affinity and CPU sets.
  - Optional columns (Settings): *CPU %* (share of the whole machine since the last refresh), *Memory* (private working set), *GPU %* (busiest GPU engine of the process) and *VRAM* (dedicated GPU memory), the last two from the GPU performance counters and read only while one of them is shown or the details panel is open.
  - Search by name, PID or service. Clicking a column title sorts.
  - Each svchost expands to show the services it hosts.
  - Selecting a process opens the **details panel** at the bottom (X or Esc closes it; another selection switches it; with several selected it applies to all of them, like the menu):
    - *Now*: CPU %, memory, GPU % and threads (summed over the selection) and the program's path.
    - *Priority*: CPU (Realtime asks for confirmation), I/O and memory priority (1–5). A click applies it now (*Apply Now*); the filled button is what the process reports after the next refresh.
    - *Processors*: *Affinity* or *CPU sets* (affinity when the process's affinity is limited), one button per logical processor; the last one can't be turned off.
    - *Rule*: *Apply every time <exe> starts* is on when the active profile has a rule for it (Rules or the gamemode rules). Turning it on saves the priorities the process reports (and its processors when limited); while it's on, every change in the panel is also saved to the rule. Turning it off removes the rule, asking first only when the rule has settings the panel doesn't show (action, boost, efficiency, delay, children, extras, service or thread rules). *Only while gamemode is on* moves the rule to the gamemode (*When it turns on*) rules and back; refused when the other list already has one for that program. *See it in Rules* opens the view that has it.
- **Process menu**
  - Each setting has *Apply Now* (only now) and *Always* (creates or changes the rule in the active profile).
  - Settings: CPU Priority (Realtime asks for confirmation), I/O Priority, Memory Priority, Priority Boost, Efficiency Mode (EcoQoS: On/Off/Auto), GPU Priority, CPU Affinity and CPU Sets (All, the CPU presets below, masks or *Custom…*).
  - *GPU Priority*: the process's GPU scheduling priority class (`D3DKMTSetProcessSchedulingPriorityClass`: Idle, Below normal, Normal, Above normal, High, Realtime; Realtime asks for confirmation). Windows has no call to read it back, so the menu shows no current value and a rule applies it again at every start.
  - CPU presets, offered only when the CPU has them (read from `GetSystemCpuSetInformation`: core, efficiency class and `LastLevelCacheIndex`; the L3 size of each cache from `GetLogicalProcessorInformationEx`):
    - *SMT / HT off*: one logical processor per core.
    - *P-cores*, *E-cores* and *LP E-cores* (E-cores outside the P-cores' cache, as on Meteor Lake and Lunar Lake).
    - *CCD 0*, *CCD 1*…: the cores that share an L3 (CCXs on Ryzen 3000), when there's more than one and the CPU isn't hybrid.
    - *3D V-Cache CCD* and *Frequency CCD*: when the CCDs report different L3 sizes (Ryzen X3D with two CCDs), the larger one and the rest.
    - *All but core 0*: the first core gets most interrupts and DPCs.
  - The *Custom…* picker groups the cores by type (P-cores, E-cores, LP E-cores) or by CCD, marking the 3D V-Cache one, and its *Preset* menu has the same presets.
  - Suspend/Resume.
  - Threads, Open File Location and Properties.
  - *Windows Settings*: network priority, fullscreen optimizations off and high-performance GPU for the program's path, ticked when on (read when the menu opens). A click switches one and reads it back; *All programs…* opens the Windows settings window (below).
  - Terminate, with confirmation. Critical Windows processes are refused.
  - On a service row, *Always* creates the service rule (+S).
- **Threads** (double-click)
  - Columns: TID, description, priority, base, I/O, memory, ideal processor, status, boost and start module.
  - Search by description and by module.
  - Same settings menu, with Ideal Processor and CPU Sets *None* (goes back to the process's own).
  - *Always* creates a +T rule that matches by description (or by module, if the thread has no description).
- **Rules**
  - Per-`.exe` rules with an action (*Apply settings*, *Suspend* or *Terminate*) and the settings.
  - *Delay* (0–600 s): the rule applies that long after the program started, for programs that reset their own priority or affinity while loading. Checked every interval, so it applies up to one interval later. The rules list shows it as "(after N s)".
  - *Also the programs it opens* (Apply settings rules): programs opened by this one (a launcher's game, for example), and the ones those open, get the same process settings once per start, unless they have a rule of their own. A program opened by a parent that closed before the next engine tick (3 s by default) isn't covered.
  - *+ Add From Open Programs*: the open programs with their icon, games first; the ticked ones get the chosen cores. Games get them as CPU sets (Windows can still use the other cores when it has to, and anti-cheats accept it better), other programs as affinity. An existing rule keeps everything else.
  - *Power Plan*: switches to that plan while the program runs and back to the previous one when its last copy closes. Gamemode's own plan wins while the gamemode is on.
  - *CPU Limiter*: when the program uses more than the chosen share of the whole CPU for that long, it loses that many processors (the highest-numbered) for a while, then gets them back. Never leaves it with fewer than one.
  - *Screen Mode*: the main screen's resolution and refresh rate while the program runs (a stretched resolution for a game, for example), set as a temporary mode; the saved mode comes back when it closes, and at engine start after a crash.
  - Power plans and screen modes switched by rules are written to `rule-state.json` before each change and undone when the engine exits or starts again.
  - *GPU priority* (process and service rules): the GPU scheduling priority class, as in the process menu.
  - **+S**: rule for the process that hosts a service.
  - **+T**: thread rule by description and/or module, with `*` as a wildcard.
  - On the Gamemode view the same two are in a rule's *···* menu (*Add service rule*, *Add thread rule*, *Delete*).
- **CPU masks**: named processor groups, used for affinity and CPU sets. Renaming updates the rules that use the mask.
- **Gamemode**
  - The view: a header with the on/off switch (the hotkey beside it) and a status line from the state and the automatic setting; three cards (*Turning on*: automatic gamemode, the off delay, opening the programs, the hotkey; *Timer resolution*; *Games*); then three tabs: *While it is on* (the Windows changes below, two columns, each description in full in its tooltip), *When it turns on* and *When it turns off* (the rules as a table, *What changes* as chips, *Edit* and a *···* menu, with *Start a program* and *+ Add process rule* beside the tabs).
  - Global hotkey (default Ctrl+Alt+G); the tray menu and the switch in the sidebar toggle it too.
  - *Timer resolution*: *Off*, *While gamemode is on* or *Always*, at the chosen value (0.5 ms by default; slightly higher values are offered too, since on some PCs they make Sleep(1) more precise). The engine holds the request (NtSetTimerResolution) and tells games to keep theirs while minimized. Since Windows 10 2004 a request only counts for the process that made it unless *Global timer resolution requests* is on (toolbox setting, needs a restart); the tab warns when it's off.
  - *When it turns on*: rules that apply while the gamemode is on, on top of the profile's, and programs to open.
  - *When it turns off*: rules applied once when it turns off, and programs to open.
  - Programs open with Explorer's token, as if opened from the Start menu.
- **Automatic gamemode** (Gamemode view, on by default)
  - The engine turns the gamemode on when a game from the list is open in the user's session and turns it off after the last one closes and the delay passes (default 15 s, 0 to 120), so launchers that reopen the game, or a crash followed by a relaunch, don't keep toggling it on and off. Alt-tab doesn't turn it off.
  - Built-in list (`KosGovernor.Core\Resources\games.json`, in the format of CapFrameX's process list: `Name` without `.exe`, `DisplayName`, `IsBlacklisted`): 531 games and 429 programs that never count (launchers, browsers, tools). Launchers don't turn the gamemode on: opening Steam or the Riot Client to buy a game turns nothing on. Compared with the original list, `LolClient`, `WoWSLauncher`, `WizardWarsLauncher` and `GameLauncher` became "never a game"; Riot Client, League Client, Ubisoft Connect, GOG Galaxy, Rockstar, Rare, Heroic, itch, Overwolf, Wargaming and others were added as "never a game"; League of Legends, Deadlock, osu!, Diablo IV and GTA V Enhanced were added as games.
  - The user's changes live in `config.json` (`AutoGamemode.Games`, same format) and apply on top of the built-in list: right-click the process › *Gamemode* › *Add as a game*, *Never a game* or *Use the built-in list*; or *+ Add game* on the *Games* card (program name). Only what differs from the built-in list is stored. *Show list* opens the list in effect (built-in + changes) with search by program or game name, *All*, *Games*, *Never a game* and *Your changes* filters, sortable columns and the same Gamemode menu on right-click.
  - Automatic mode only turns off what it turned on itself: turned on by hand (hotkey, tray, button) it stays on; turned off by hand with a game open it doesn't turn back on until no game is open.
  - The *When it turns on*/*When it turns off* programs open on an automatic switch only with *Also open this profile's programs* checked (per profile). The rules apply in both cases.
  - The log says who turned it on or off (e.g. `automatic: Counter Strike 2 (cs2.exe) started`); the tray tooltip shows the game.
- **Windows settings for games** (Gamemode › *Windows settings*, next to *Show list*; or right-click a process › *Windows Settings*). They replace the KOS script `Auto DSCP & FSE.bat`, deleted from KOS-W11, and don't depend on gamemode: Windows applies them whenever the program runs.
  - *Network priority*: a QoS policy that marks the program's traffic DSCP 46 (`HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\KOS <file>`), by file name, so it survives the game moving folders. Turning it on also sets *Do not use NLA* (`Tcpip\QoS`), without which Windows applies QoS policies only on domain networks. Windows marks the packets through the QoS Packet Scheduler, so that binding must be on for the adapter (toolbox › Network); routers that honor DSCP send the traffic first. Turning it off deletes the KOS policies (`KOS *`) for that program name; one from Group Policy or another program stays and still reads as on. The value *Do not use NLA* had before KOS first set it is kept beside it and put back when the last KOS policy goes.
  - *Fullscreen optimizations off*: the compatibility flags `DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE` (the high-DPI override too) in `HKCU\...\AppCompatFlags\Layers`.
  - *High-performance GPU*: `GpuPreference=2;` in `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, the choice in Settings › Display › Graphics.
  - The last two are per user and keyed by the program's full path; other compatibility flags and DirectX pairs in the same values are kept. They take effect the next time the program starts.
  - The window lists every program with any of the three, merged by path (a policy by file name marks every listed path with that name; a policy alone shows as *Any folder*). *+ Add program…* picks an `.exe`; each box applies at once and shows what Windows reads back (a box that doesn't read back returns to what it was, with the reason in the status line); *Remove* turns all three off.
- **Windows changes while the gamemode is on** (Gamemode › *While it is on*, per profile, each with its own switch; they apply to both manual and automatic gamemode)
  - *Power plan*: activates the chosen plan (suggested default: High performance) and goes back to the previous one.
  - *Stop Windows Update*: stops `wuauserv`, `UsoSvc` and `DoSvc`; *Stop Windows Search indexing*: stops `WSearch`. It doesn't change the startup type; if Windows turns one back on, the engine stops it again every interval (logged once). When the gamemode turns off, it starts again only the ones that were running before.
  - *Turn off transparency effects*: `EnableTransparency` = 0 in `HKCU\...\Themes\Personalize` and an `ImmersiveColorSet` notice so windows apply it right away.
  - *Disable processor idle states (C-states)*: "Processor idle disable" = 1 (AC and DC) on the active plan, after switching the plan if the plan switch is on.
  - *Efficiency mode for background programs* (on by default): every interval, programs behind the game go into efficiency mode (EcoQoS: lower clocks, efficient cores), once per start, and back to automatic when the gamemode turns off. Left alone: games and the programs they open, the program in front (one that comes to the front goes back to automatic and stays there), programs in the Windows folder or without a path, other sessions, programs above normal priority, programs that chose their own mode, programs with a rule (add a rule to keep one out) and voice/capture programs (Discord, OBS, Streamlabs, Voicemeeter, TeamSpeak).
  - *Game cores*: games from the list without a rule of their own get the chosen cores (a CPU preset such as *3D V-Cache CCD* or *P-cores*, or a mask) as CPU sets while the gamemode is on, once per start; when it turns off, games still open go back to all cores. Off by default.
  - *Clean memory*: once, after the chosen wait once gamemode is on (60 s by default, so the game finishes loading). Empties the working set of open programs, except those in the Windows folder (explorer, winlogon, dwm, svchost… get slow after being emptied), processes without a path (protected), the game with the programs it opened and its launcher, anti-cheats and the governor itself; then clears the standby list. The log shows how many were emptied and the free memory before and after.
  - *Clear the standby list*: while gamemode is on, when the standby list passes the chosen size and free memory is below the chosen amount (1024 MB each by default), at most once a minute. Nothing else is emptied.
  - *Lower busy background programs*: a program behind the game that uses more than 15 % of the CPU for 6 s goes to Below Normal priority, and back when it stays under 5 % for 15 s or the gamemode turns off. Same exclusions as efficiency mode.
  - *Keep the PC awake*: no sleep and no screen off while gamemode is on (for games played with a controller).
  - *Wi-Fi low latency*: turns off background scans (ping spikes every minute or so) and turns on the driver's media streaming mode on the connected Wi-Fi adapters; both last while the adapter stays connected and are set back when gamemode turns off.
  - *Desktop compositor in MMCSS*: `DwmEnableMMCSS(TRUE)` while gamemode is on: DWM runs in the multimedia scheduling class, for steadier frame pacing in windowed and borderless games. Windows ties it to the engine process, so an engine crash undoes it too.
  - *Thread director* (off by default): while a listed game runs (the busiest one in the user's session without a rule that sets its affinity or CPU sets):
    - The game runs on the game cores: CPU sets it already has (its own or *Game cores*), else the 3D V-Cache CCD, the P-cores on a hybrid CPU, or all cores.
    - Every 5 s the engine reads each game thread's CPU cycles (`QueryThreadCycleTime`) and CPU time. The busiest ones (1–4, 2 by default) that use at least 40 % of a core get a physical core of their own (`SetThreadSelectedCpuSets` with both SMT siblings, and that core as ideal processor); the rest of the game's CPU sets leave those cores out, so its other threads stay off them. A thread keeps its core while it uses at least 20 % of one; a busier newcomer waits for a free core. At least two cores always stay for the rest of the game, so a CPU with fewer than three game cores gets no isolated threads.
    - Left alone: worker and pool threads (description containing *worker*, *pool*, *taskgraph* or *job*), threads the game pinned itself (CPU sets of their own or an affinity narrower than the process's) and threads matched by a thread rule.
    - The isolated cores are the ones with the least interrupt work in the last trace (below); without one, any but the core of CPU 0, which takes most interrupts by default.
    - *Move other programs off the game's cores* (on): other programs get the remaining cores as default CPU sets once per start (the other CCD or the E-cores; on a CPU with one kind of core, all but the isolated ones). Left as they are: the game and what it opened, programs with a rule, kernel and session processes (System, Registry, Memory Compression, smss, csrss, wininit, services, lsass, winlogon), dwm, fontdrvhost, audiodg, anti-cheats, voice and capture programs, and programs with CPU sets or an affinity of their own.
    - *Lower other programs' Realtime priority and time-critical threads* (on): other programs at Realtime priority drop to High, and threads at Time Critical to Highest, once per start. Left alone: the game and what it opened, programs with a rule, Windows' own (Windows folder, or no readable path) and programs that use MMCSS (`avrt.dll` loaded).
    - *Avoid the processors with the most interrupt work* (on): outside gamemode, at most once a minute, a 2 s kernel trace of DPC and ISR routines (session `KOS Governor Interrupts`, system logger mode, the same decoding as the toolbox's latency measurement) adds up the time per processor and per driver; the log line when the director starts names the busiest processors and drivers.
    - Every change is journaled in `gamemode-state.json` (threads with their previous ideal processor, programs with their previous CPU sets, lowered priorities) and undone when the game closes, another game becomes the busiest, the switch is turned off, gamemode turns off, the engine exits or, after a crash, when it starts again; only what is still as the director left it. Each step goes to the log.
  - *Low-latency audio*: keeps a stream open on the default playback device at the driver's smallest shared-mode period, which makes the whole shared audio engine use it (10 ms otherwise). Needs a driver that supports it (most do with the standard Windows driver).
  - The previous value is written to `%ProgramData%\TheKOS\Governor\gamemode-state.json` before each change. It is undone when the gamemode turns off, when the engine exits (*Exit*) and, after a crash or shutdown, when the engine starts again. It only undoes what is still as the gamemode left it (e.g. if you changed the plan during the game, your change stays). Changing the switches takes effect the next time the gamemode turns on.
- **RTSS** (RivaTuner Statistics Server, per profile, while gamemode is on; the governor never installs or starts RTSS, and RTSS hooks into the game to draw and limit, which some anti-cheats refuse)
  - *On-screen display* (off by default): every second the engine writes the chosen items to a slot of its own in RTSS's shared memory (`RTSSSharedMemoryV2`, owner *KOS Governor*): FPS and frame time (RTSS's own `<FR>`/`<FT>` tags), a frame time graph (an embedded graph object that RTSS fills from the game's frames, `<OBJ>`, shared memory v2.12 or newer), GPU load, temperature, clock, power and VRAM (NVIDIA, NVML), CPU load and memory. All but the graph are on by default; changes show within a few seconds. The slot is freed when gamemode turns off, the engine exits, or at the next engine start after a crash. Without RTSS running it does nothing (logged once).
  - *Frame limit* (empty = RTSS's own setting, or 10 to 1000 FPS typed in the box) and *Limiter mode* (RTSS's own, async, front edge sync, back edge sync, NVIDIA Reflex: profile property `SyncLimiter` 0–3): written to RTSS's global profile through `RTSSHooks64.dll` (from RTSS's install folder under Program Files, owned by SYSTEM/Administrators), read back after writing, and the running games are told to reload it. The previous values go to `gamemode-state.json` first and are put back when gamemode turns off (or at the next start after a crash), each only if it is still the one the governor set. A game's own RTSS profile with settings of its own wins over the global one.
  - Not offered: passive/active waiting and the Reflex sleep call, which `RTSSHooks64.dll`'s profile functions don't expose (only the profile file has them); scanline sync (`ScanlineSync`), which needs a scanline chosen per display; and 1% low (`<FR10L>`), which RTSS only fills in its benchmark mode.
- **Settings**: visible columns of the two lists, list and engine intervals, start with Windows, start/stop the engine, and *Import*/*Export* of a profile.
- **GPU fan curve**
  - Configured on the toolbox's GPU page. The engine runs the curve by reading `fan-curve.json` in the governor folder, with the same NVML controller as the toolbox (`KosGovernor.Core` references `KosToolbox.Core`).
  - It rereads the file when it changes and reports the state in `fan-curve-state.json`.
  - If the curve stops (card not ready at sign-in, write error), it retries every 10 s.
  - Video driver restarted or TDR (event 4101 from `Display` in the System log, or NVML/NVAPI losing the session): reopens the driver libraries, restarts the curve right away and reapplies the overclock saved with the toolbox's *Apply at sign-in* (`gpu-tuning.json`) every 5 s for up to 1 minute.
  - When the engine exits, the fan goes back to automatic.
- **GPU profiles for gamemode**
  - Chosen on the toolbox's GPU › Overclock tab (*Gamemode* card): a profile for when gamemode turns on and, optionally, one for when it turns off. Nothing chosen: the card isn't touched.
  - The toolbox copies the profile's values into `gamemode-gpu.json`. On gamemode on, the engine saves what the card has to `gamemode-gpu-state.json` and writes the profile (revalidated against the card's limits). On gamemode off it writes the "off" profile or, without one, puts back what the card had.
  - A profile with a fan curve runs that curve instead of the toolbox's while it's on; one with an automatic or fixed fan stops the curve while it's on.
  - Writing the profile that started and never finished (the PC crashed while writing it) pauses the profile until it's chosen again. A gamemode that never ended (engine killed, PC crash) is ended at the next engine start. After a driver reset during gamemode, the engine reapplies the sign-in tuning, not the gamemode profile.
- **Mouse settings for gamemode**
  - Chosen on the toolbox's Peripherals page (*Gamemode profile*: DPI, polling rate, light off or an effect, the dock's ring, the R82HE's rapid trigger, a DualShock 4's light bar, per device, in `gamemode-peripheral.json`). On gamemode on, the engine reads each listed device (by VID and PID), saves what it has to `gamemode-peripheral-state.json` and writes the profile: the DPI live only (Razer's no-store byte; Logitech host mode), the light's live brightness (0 = a mouse logo or the dock ring off) and live effect, never onboard flash on a mouse or the dock; every write is read back by the toolbox's driver. A value that couldn't be read before is not written, so it can always be put back. A device whose every setting is stored in flash (the Attack Shark R82HE keyboard, `StoredOnly`) gets its polling rate, lighting (an effect lights it at 100 % if its brightness was 0) and rapid trigger written to flash, only what differs; its whole stored lighting and switch columns are saved first and written back as they were, so per-key actuation, snap tap and the other key modes come back exactly. A DualShock 4's light bar can't be read: it gets the profile's color (or off) and, on gamemode off, the color the toolbox saved with the profile as the one it last set; nothing is read back. On gamemode off it writes back what the device had; a device that isn't there keeps the state for the next gamemode off. Same crash rule as the GPU profiles: a write that never finished suspends the settings until they are chosen again.
- **Device batteries**
  - On its own background thread, every 5 minutes (every minute while a device is asleep), the engine reads the batteries of the devices the toolbox's drivers support (getters only) and adds them to the tray tooltip (`Viper Ultimate 72 %+`, `+` = charging). At 15 % or less, not charging, a balloon, at most once an hour per device.
  - Each device with a battery also gets its own tray icon (`TrayBatteryIcon`, drawn in code at the tray size): a ring that empties clockwise from the top, the device's outline inside (mouse, headset, keyboard, controller), amber at 15 % or less when not charging, a yellow bolt while charging; its tooltip gives the name and percent. The icon id comes from the VID and PID, so Windows remembers whether it's shown on the taskbar (new icons start in the overflow: drag them out once). A device asleep keeps its last reading; an unplugged one loses its icon. *Show in the tray* (on by default, in the device's Power card on the toolbox's Peripherals page; `TrayHidden` in `gamemode-peripheral.json`) turns a device's icon off; its tooltip line and low-battery balloon stay.
  - *Mouse battery on the ring* (turned on on the toolbox's Peripherals page, `DockBattery` in `gamemode-peripheral.json`): while a Razer Mouse Dock is connected the loop runs every minute and sets the ring's live color from the Razer mouse's battery (red at 0 %, yellow at 50 %, green at 100 %; breathing while charging), only when the color changes, read back by the driver; nothing is stored in the dock. A mouse that is asleep leaves the last color. Saving on the toolbox wakes the loop at once.
- **Gamemode session report**
  - On by default; Settings › Engine › *Record a session report during gamemode* turns it off (turned off during a session, that session is dropped).
  - While gamemode is on, the engine samples about once a second: CPU load, clock while busy, the "% Performance Limit" counter (held below full speed by heat, a power limit or the power plan), DPC and interrupt time; lowest available memory, peak commit and pages read back from disk; and, on an NVIDIA card, load, clock under load, temperature (hotspot and memory when reported), power, VRAM and the driver's clock limit reasons (power, thermal, hardware slowdown, power brake) under load.
  - It also watches what interfered: programs outside the game (and its launcher and what they opened) using more than 1 % of the CPU on average, the game losing focus and to which program, devices that disappeared (audio, USB, HID, network, Bluetooth, monitors, cameras, controllers) and wired/Wi-Fi links that went down. At the end it adds the System log's driver resets, hardware errors and crashes from the session.
  - When gamemode turns off (or the engine exits), the report goes to `%ProgramData%\TheKOS\Governor\sessions.json` (newest 20) and a notification shows the one-line summary; the toolbox shows the reports on Hardware › Stability.
  - With PawnIO installed (toolbox › Setup › Tools; an optional signed driver), the CPU section also has the package temperature (average and peak), package power from the RAPL energy counter and, on Intel, how often the CPU was thermal throttling or held by its package power limit. Intel reads the package thermal status against TjMax through PawnIO's IntelMSR module; AMD Zen (family 17h–1Ah) reads Tctl from SMN 0x59800 through AMDFamily17, under the `Global\Access_PCI` mutex other monitoring tools share. The two signed modules (PawnIO.Modules 0.2.11, LGPL-2.1) are embedded in `KosToolbox.Core` (`SystemInfo\PawnIO`, with their license); PawnIOLib.dll is loaded from PawnIO's own folder after checking it belongs to SYSTEM/Administrators. Without PawnIO the report says how to get it.
- **Profiles**
  - Profile menu: switch, new, duplicate, rename and delete. The tray also switches profiles.
  - *Export* saves the profile and the masks it uses.
  - *Import* validates everything first and shows which programs the profile opens or terminates.

## Files

- **Configuration:** `%ProgramData%\TheKOS\Governor\config.json`.
  - The folder is created writable only by administrators (the engine applies the rules as administrator).
  - A folder that is a link, or that isn't owned by SYSTEM/Administrators, is refused.
  - Every change rereads the file, validates and saves right away. The engine rereads on its own.
- **Gamemode session reports:** `%ProgramData%\TheKOS\Governor\sessions.json`, the newest 20.
- **Log:** `%ProgramData%\TheKOS\Logs\Governor\governor-YYYYMMDD.log`, kept for 30 days.

## Known limits

- Only processor group 0 (up to 64 logical processors).
- Windows doesn't report the Efficiency Mode state of another process, so the Eco column shows `?`. Applying works.
- Protected processes (anti-cheat, PPL) may refuse changes; this goes to the log. A game whose anti-cheat refuses thread access gets no isolated threads (logged once).
- The engine checks the list once per interval, with no kernel events. A program runs for up to one interval with its original values before the rule applies.

## Build

```bash
dotnet build KosGovernor.sln
```

```bash
dotnet test tests/KosGovernor.Core.Tests
```

Publishing the engine (`dotnet publish src/KosGovernor.Engine -p:PublishProfile=win-x64`) compiles it with Native AOT, which needs the MSVC linker and the Windows SDK: Visual Studio 2022 Build Tools with the *Desktop development with C++* workload. A regular build doesn't.
