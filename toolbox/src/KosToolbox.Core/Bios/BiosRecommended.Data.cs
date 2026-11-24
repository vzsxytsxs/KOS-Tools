namespace KosToolbox.Core.Bios;

public static partial class BiosRecommended
{
    /// <summary>
    /// The list. One line per setting (or per group with the same value): <c>vendor | value | names | note</c>.
    /// Vendor: A = AMD, I = Intel, * = both. Value: off, on, auto, =Option text (several separated by ";", the first the BIOS offers wins,
    /// "~" before one means "contains"), #number, maxgen (the fastest GenN of a list). "?" after the value = optional (the Recommended
    /// button leaves it out because of the drawback in the note); "&gt;value" = the value on Zen 4 and later (Ryzen 7000/8000/9000).
    /// Names: every spelling boards use, compared without case, punctuation, "(R)"/"(TM)" and plurals; a trailing "*" = starts with.
    /// Sources: the KOS BIOS lists (AMD, Intel and the hidden-settings scripts; their raw option numbers are not used: they belong to one
    /// BIOS, so the value is found by its meaning). Where the lists disagree, the readable list wins. A raw "1" that only the hidden-settings
    /// script has is optional: on a real board several of those settings number Enabled as 00, so what the script meant isn't certain.
    /// </summary>
    internal const string Table = """
        # ===================== both vendors =====================
        *|off|SR-IOV Support; SR-IOV; ACPI Standby State; BME DMA Mitigation; ASPM; Native ASPM; USB S5 Wakeup Support; Option ROM Messages; BIOS Hot-Plug Support; RGB Light
        *|off|ASPM Mode Control; CPU PCIE ASPM Mode Control; ASPM Control for CPU PCIe
        *|off|Spread Spectrum; Clock Spread Spectrum; SB Clock Spread Spectrum; FCH Spread Spectrum; PCIe Spread Spectrum Clocking; PCIe Spread Spectrum; FIVR Spread Spectrum; RFI Spread Spectrum; IOTG Spread Spectrum
        *|on?|Extended Tag; Extended Synch|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.

        # ===================== AMD =====================
        # Chipset and board
        A|off?|Promontory Presence|Only for boards without a chipset: on a board with one, the devices behind the chipset (SATA, USB, M.2, slots) can stop working.
        A|off|ECO Mode; Network Stack; Network Stack Configuration; Data Link Feature; Data Link Feature Exchange; ASM Controller; ASMedia Controller
        A|=Enabled;Auto|SoC/Uncore OC Mode; Soc/Uncore Mode
        A|off|LCLK DPM; LCLK DPM Enhanced PCIe Detection; FEATURE FCLK DPM; FCLK DPM
        A|off|Memory Clear; Power Down Mode; Power Down Enable; DRAM Power Down; LN2 Mode; Memory MBIST; MBIST
        A|on|DFE Read Training; FFE Read Training
        A|auto|Gear-Down Mode

        # C-states: off on the older Ryzen, left on Auto from Zen 4 (turning them off lowers boost and performance there)
        A|off>auto|Global C-State Control; ACPI _CST C1 Declaration; DF Cstates; CC1

        # Power and boost
        A|=1;Enabled|APBDIS; DF APBDIS
        A|=P0;0?|Fixed SOC Pstate|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        A|=Typical Current Idle|Power Supply Idle Control
        A|off|CPPC; CPPC Preferred Cores; Core Performance Boost; AMD Core Performance Boost; OPCache Control
        A|off|AMD Cool&Quiet function; AMD Cool & Quiet; AMD Cool'n'Quiet; Cool'n'Quiet; PSS Support; IOMMU
        A|#100?|Package Power Limit; PPT Limit; PPT Limit [W]; Package Power Limit PPT|Limits the processor to 100 W (the value of the list); use your own number if the processor needs more.

        # Prefetchers and fabric (on)
        A|on|L1 Stream HW Prefetcher; L2 Stream HW Prefetcher
        # Only in the hidden-settings script, as a raw option number (1): the BIOS it came from may number the options the other way round
        # (a real board had Disabled as 01 for settings like these), so they are optional.
        A|on?|L1 Stride Prefetcher; L1 Region Prefetcher; L1 Burst Prefetch Mode; L2 Up/Down Prefetcher; CPPC Dynamic Preferred Cores|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        A|on?|System probe filter; Periodic Directory Rinse; DRAM map inversion; PCIe Ten Bit Tag Support; Indirect Branch Prediction Speculation|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        A|on?|Disable DF to external downstream IP SyncFloodPropagation; Disable DF sync flood propagation|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        A|on?|Disable Memory Error Injection; NBIO RAS Global Control; Legacy USB Support|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.

        # Optional: a drawback the note explains
        A|off?|SVM Mode|Turns off virtualization: virtual machines, WSL, Windows Sandbox and Core isolation stop working.
        A|off?|Pluton Security Processor|Turns off the Microsoft Pluton security processor.
        A|off?|Thunderbolt Support|Turns off Thunderbolt/USB4 ports.
        A|off?|DRAM ECC Enable; ECC; DRAM UECC Retry|Turns off error correction on boards with ECC memory.

        # Hidden settings (off)
        A|off|Port 60/64 Emulation; S3/Modern Standby Support; XHCI Hand-off; Serial(COM) Port; Serial Port; Parallel Port; TSME; SmartShift Enable; STAPM Boost
        A|off|Debug Port Table; VGA Palette Snoop; ASPM Support; Aggresive SATA Device Sleep Port 0; Aggresive SATA Device Sleep Port 1; AB Clock Gating
        A|off|I2C 0 D3 Support; I2C 1 D3 Support; I2C 2 D3 Support; I2C 3 D3 Support; I2C 4 D3 Support; I2C 5 D3 Support
        A|off|UART 0 D3 Support; UART 1 D3 Support; UART 2 D3 Support; UART 3 D3 Support; SATA D3 Support; EHCI D3 Support; XHCI D3 Support; SD D3 Support
        A|off|Chipset Power Saving Features; _OSC For PCI0; USB Phy Power Down; SB C1E Support; Remote Display Feature; Turn On LED in S5; ACPI Sleep State
        A|off|ACP Power Gating; ACP CLock Gating; Wake on PME; D3 Cold Support; ACPI D3 Support; SMEE; SEV Control
        A|off|Platform First Error Handling; SMU and PSP Debug; SMU and PSP Debug Mode; Core Watchdog Timer Enable; Freeze DF module queues on error
        A|off|CC6 memory region encryption; DRAM scrub time; Poison scrubber control; Redirect scrubber control; GMI encryption control; xGMI encryption control
        A|off|ACS Enable; PCIe ARI Support; PCIe ARI Enumeration; Enable AER Cap; CV test; Loopback Mode; SRIS; PCIe loopback Mode
        A|off|NBIO RAS Control; NBIO SyncFlood Generation; System Hub Watchdog Timer; NBIO Poison Consumption; Sata RAS Support; USB ecc SMI Enable; ALink RAS Support
        A|off|eMMC Boot; Onboard PCIE LAN PXE ROM; PM L1 SS; Data Scramble; DMAr Support; Modern Standby Type; PSPP Policy; MCA error thresh enable; 1TB remap
        A|off|MCA FruText; Log Transparent Errors; PCIe All Port ECRC; Advanced Error Reporting (AER); NPU Deep Sleep Enable; DRTM Support; AIM-T Support; SecureBio Support
        A|off|PCIe Power Management Features; Device Sleep for AHCI Port 0; Device Sleep for AHCI Port 1; Device Sleep for AHCI Port 2; Device Sleep for AHCI Port 3
        A|off|SMART Self Test; PPIN Opt-in; Data Poisoning; DRAM Post Package Repair; RCD Parity; Write CRC Enable; APP Center Download & Install
        A|off|Chipset SATA Port Hot Plug; Wake on LAN; MSI Driver Utility Installer
        A|off|ASPM Mode(Dev#1/Func#1); ASPM Mode(Dev#1/Func#2); ASPM Mode(Dev#2/Func#1); ASPM Mode(Dev#2/Func#2)
        A|off|ASPM Mode Control(Device4); ASPM Mode Control(Device5); ASPM Mode Control(Device6); ASPM Mode Control(Device7)

        # ===================== Intel =====================
        I|off|CPU EIST Function; EIST; Enhanced Intel SpeedStep Technology; Intel SpeedStep Technology; Intel SpeedStep; SpeedStep; Enhanced SpeedStep
        I|off|Race To Halt (RTH); Race To Halt; RTH; Energy Efficient Turbo (EET); Energy Efficient Turbo; EET; Energy Efficient P-state; Energy Performance Gain
        I|off|Voltage Optimization; HDC Control; C6DRAM; PECI
        I|off|Intel C-State; C-States; CPU C-State
        I|off|VT-d; Intel VT-D Tech; Intel VT-d Technology; Intel Virtualization Technology for Directed I/O; VT for Directed I/O; VT-d Support
        I|off|IGD VTD Enable; IPU VTD Enable; IOP VTD Enable; Intel Trusted Execution Technology
        I|off|Software Guard Extensions (SGX); SW Guard Extensions (SGX); SGX; Intel SGX
        I|on|Port 60/64 Emulation; Port 60/40 Emulation
        I|on|Hardware Prefetcher; CPU Hardware Prefetcher; Adjacent Cache Line Prefetch; Adjacent Cache Prefetch
        I|on|PCI Delay Optimization; MSI Enabled; AVX; AVX Support; AVX3; AVX3 Support
        I|=1 GHz;1GHz;1000MHz|FCLK Frequency
        I|=Round Robin|Interrupt Redirection Mode Selection
        I|=RUN Loop|AP threads Idle Manner
        I|=~248|PCI Latency Timer; PCI-X Latency Timer
        I|=4096 Bytes;~4096|Maximum Payload; Minimum Payload; Maximum Payload Size; Max Payload Size
        I|maxgen|PEG0 - Max Link Speed; PEG1 - Max Link Speed; PEG2 - Max Link Speed; PEG3 - Max Link Speed; PEG Max Link Speed
        I|off|PEG0 - ASPM; PEG1 - ASPM; PEG2 - ASPM; PEG3 - ASPM; PEG ASPM; DMI Link ASPM Control; DMI ASPM; DMI Gen3 ASPM
        I|=Ignore|VGA Detection
        I|off|ErP Ready; ErP; Chassis Intrusion; Chassis Intrusion Detection; Machine Check; Machine Check Exception; Machine Check Enable
        I|off|Enable ACPI Configuration; Enable ACPI Auto Configuration; PEP Settings; PEP*

        # Optional: a drawback the note explains
        I|off?|Internal Graphics; Integrated Graphics|Turns off the processor's graphics: use it only with a graphics card, because the board's video outputs stop working.
        I|off?|Intel Platform Trust Technology; Intel PTT; PTT; Platform Trust Technology|Turns off the firmware TPM: Windows 11 and BitLocker need a TPM.
        I|off?|Security Device Support|Can cause problems on Windows 11 (it turns off the TPM).
        I|off?|Intel Speed Shift Technology; Speed Shift Technology; Hardware P-States; HWP|Use it only with the CPU frequency set manually: Speed Shift hands the frequency to the processor.
        I|off?|Thunderbolt Boot Support; Wake From Thunderbolt(TM) Devices; Thunderbolt Usb Support; Windows 10 Thunderbolt support|Turns off Thunderbolt: its ports and docks stop working.
        I|off?|PS2 Keyboard and Mouse|Turns off the PS/2 port: a PS/2 keyboard or mouse stops working.
        I|off?|ECC Support; In-Band ECC Support|Turns off error correction on boards with ECC memory.

        # Hidden settings (off)
        I|off|PUIS Enable; EC Low Power Mode; Intel Ready Mode Technology; Intel RMT State; Tcc Offset Time Window; Bi-directional PROCHOT#; ACPI T-States; Enable xdpclock
        I|off|WDT Enable; BCLK Aware Adaptive Voltage; Ring Down Bin; PET Progress; WatchDog; USB3 Type-C UFP2DFP Kernel/Platform Debug Support; CrashLog Feature
        I|off|Legacy UART; Serial IO UART; Trace Hub; MRC Serial Debug Messages; Serial Debug Messages; CPU Wakeup Timer; Enable Hibernation
        I|off|ARI Forwarding; LTR Mechanism Enable; Clock Power Management; Ipv4 PXE Support; Ipv6 PXE Support; Launch PXE OpROM policy; Enable RH Prevention
        I|off|Enable/Disable IED (Intel Enhanced Debug); CHAP Device (B0:D7:F0); Thermal Device (B0:D4:F0); GNA Device (B0:D8:F0); ALS Support; VDD Enable; PAVP Enable
        I|off|Enable Display Audio Link in Pre-OS; RC6(Render Standby); PEG0 Hotplug; PEG1 Hotplug; PEG2 Hotplug
        I|off|LAN Wake From DeepSx; Wake on LAN Enable; SLP_LAN# Low on DC Power; DeepSx Power Policies; DeepSx Wake on WLAN and BT Enable; Foxville I225 Wake on LAN Support
        I|off|Wake on WLAN and BT Enable; PS_ON Enable; Enhance Port 80h LPC Decoding; PCH Cross Throttling; IOAPIC 24-119 Entries; Enable 8254 Clock Gate
        I|off|USB2 PHY Sus Well Power Gating; Intel Rapid Recovery Technology; Smart Response Technology; Intel(R) Optane(TM) Memory
        I|off|System Acceleration with Intel(R) Optane(TM) Memory; CPU Attached Storage; Modern StandBy; LAN Option ROM; USB Standby Power at S4/S5
        I|off|PCI Express Clock Gating; PCI Express Power Gating; Power Gating; Vmax Stress
        I|off|LPM S0i3.4; LPM S0i3.3; LPM S0i3.2; LPM S0i3.1; LPM S0i3.0; LPM S0i2.2; LPM S0i2.1; LPM S0i2.0
        I|off|CPU PkgC10 Long Sleep; Tcc Offset Lock Enable; Power Loss Notification Feature; Enable DCI ModPHY Power Gate; USB DbC Enable Mode
        I|off|CPU Run Control; CPU Run Control Lock; USB Overcurrent Override for DbC; Cpu CrashLog (Device 10); OS IDLE Mode; S0ix Auto Demotion
        I|off|Hybrid Storage Detection and Configuration Mode; FIVR Dynamic PM; Row Hammer Mode; Row Hammer Solution; KT Device; IPU Device (B0:D5:F0)
        I|off|Intel Speed Shift Technology Interrupt Control; HwP Autonomous Per Core P State; HwP Autonomous EPP Grouping; Per Core P State OS control mode
        I|off|Enhanced Thermal Velocity Boost; TVB Voltage Optimizations; Thermal Velocity Boost; INT3400 Device; Enable Remote Platform Erase Feature; ITBT RTD3
        I|off|WoV (Wake on Voice); Bluetooth Sideband; BT Intel HFP; BT Intel A2DP; CFG Lock
        # Raw option number (1) in the script only; a real board numbers Enabled as 00 for several of these, so they are optional.
        I|on?|JTAG C10 Power Gate; Three Strike Counter; Disable DSX ACPRESENT PullDown; Legacy IO Low Latency; Disable Gen2 Pll Shutdown and L1 Controller Power gating|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        I|on?|Intel Turbo Boost Max Technology 3.0; Hardware Autonomous Width; Hardware Autonomous Speed; Disable Fast PKG C State Ramp for IA Domain|From the hidden-settings script only (a raw option number): what it meant isn't confirmed, so Recommended leaves it out.
        """;
}
