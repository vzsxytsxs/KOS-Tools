<#
.SYNOPSIS
    Test for removing Defender in Safe Mode (KOS Toolbox, KOS state). Run it ONLY in a throwaway VM with a stock Windows 11
    (-ThrowawayVm): it changes the boot configuration, turns Defender's services off and deletes two of its files.
    Windows Sandbox can't answer it (tried): it has no boot entry, so no Safe Mode, and Defender is off there.

.DESCRIPTION
    Answers, before the toolbox does it for real:
      1. With Tamper Protection on, can an administrator take WinDefend out of the Safe Mode list (SafeBoot\Minimal)?
      2. Does a service of ours, listed in SafeBoot\Minimal, start in Safe Mode without anyone signing in?
      3. In Safe Mode, as SYSTEM: is Defender running, and can its services be set to Start=4 and its files deleted?

    Step 1 (no switch, elevated PowerShell): logs Tamper Protection, takes WinDefend out of SafeBoot\Minimal, registers the
            test service (and a RunOnce fallback, in case the service never starts), sets Safe Mode and restarts.
    Step 2 (the service runs it in Safe Mode, as SYSTEM): first turns Safe Mode off again, then logs and tries the changes, then
            restarts normally.
    Step 3 (-Report, after Windows is back): prints the log and the state now.

    Everything is logged to C:\KosSafeModeTest\log.txt.
#>
param([switch]$InSafeMode, [switch]$Fallback, [switch]$Report, [switch]$ThrowawayVm)

$ErrorActionPreference = 'Continue'
$Work = 'C:\KosSafeModeTest'
$Script = Join-Path $Work 'Test-DefenderSafeMode.ps1'
$LogFile = Join-Path $Work 'log.txt'
$Service = 'KosSafeModeTest'
$Minimal = 'HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal'
$RunOnce = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
$Defender = 'WinDefend', 'WdFilter', 'WdBoot', 'WdNisSvc', 'WdNisDrv', 'Sense', 'SecurityHealthService', 'wscsvc', 'webthreatdefsvc'
$Files = "$env:SystemRoot\System32\SecurityHealthSystray.exe", "$env:SystemRoot\System32\drivers\WdFilter.sys"
$PowerShell = Join-Path $PSHOME 'powershell.exe'

function Log([string]$Text) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text
    Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
    Write-Host $line
}

function Log-State {
    foreach ($s in $Defender) {
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$s"
        $start = if (Test-Path $key) { (Get-ItemProperty $key -Name Start -ErrorAction SilentlyContinue).Start } else { 'no key' }
        $status = (Get-Service $s -ErrorAction SilentlyContinue).Status
        Log "  $s : Start=$start, status=$status"
    }
    Log "  MsMpEng running: $([bool](Get-Process MsMpEng -ErrorAction SilentlyContinue))"
    Log "  Defender entries in SafeBoot\Minimal: $((Get-ChildItem $Minimal | Where-Object { $_.PSChildName -match 'Defend|^Wd|Sense|SecurityHealth|wscsvc|webthreat' } | ForEach-Object PSChildName) -join ', ')"
    $filters = (& fltmc.exe filters 2>&1 | Select-String 'Wd') -join ' / '
    Log "  Wd* minifilters loaded: $(if ($filters) { $filters } else { 'none' })"
    foreach ($f in $Files) { Log "  $f exists: $(Test-Path -LiteralPath $f)" }
}

function Undo-SafeMode {
    & bcdedit.exe /deletevalue '{current}' safeboot *> $null
    Log "bcdedit /deletevalue safeboot: exit $LASTEXITCODE (0 = removed; 1 = it wasn't set)"
    Remove-ItemProperty -Path $RunOnce -Name "*$Service" -ErrorAction SilentlyContinue
    Remove-Item -Path "$Minimal\$Service" -Recurse -ErrorAction SilentlyContinue
    & sc.exe delete $Service *> $null
    Log "test service removed (sc delete exit $LASTEXITCODE)"
}

# ---------- Step 3: report ----------
if ($Report) {
    if (Test-Path $LogFile) { Get-Content -LiteralPath $LogFile } else { Write-Host 'No log: step 1 never ran.' }
    Write-Host ''
    Write-Host '--- now (normal mode) ---'
    $LogFile = Join-Path $Work 'report.txt'
    Log "Safe Mode still set: $([bool](& bcdedit.exe /enum '{current}' | Select-String 'safeboot'))"
    Log-State
    try { $mp = Get-MpComputerStatus -ErrorAction Stop; Log "  Tamper Protection: $($mp.IsTamperProtected), real-time: $($mp.RealTimeProtectionEnabled)" }
    catch { Log "  Get-MpComputerStatus failed: $($_.Exception.Message)" }
    exit 0
}

# ---------- Step 2: in Safe Mode, started by the test service (or the RunOnce fallback at sign-in) ----------
if ($InSafeMode -or $Fallback) {
    try {
        Log "=== Step 2 ($(if ($Fallback) { 'RunOnce FALLBACK at sign-in: the service did NOT run' } else { 'test service' })) ==="
        Undo-SafeMode   # first, so a failure below never leaves Windows starting in Safe Mode
        Log "account: $([Security.Principal.WindowsIdentity]::GetCurrent().Name), SAFEBOOT_OPTION=$env:SAFEBOOT_OPTION"
        Log 'Defender in Safe Mode, before any change:'
        Log-State

        Log 'Setting Start=4 on the Defender services (reg.exe, as this account):'
        foreach ($s in $Defender) {
            if (-not (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$s")) { continue }
            $out = & reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\$s" /v Start /t REG_DWORD /d 4 /f 2>&1
            Log "  $s : reg exit $LASTEXITCODE $out"
        }

        Log 'Deleting two Defender files (takeown + icacls + delete):'
        foreach ($f in $Files) {
            if (-not (Test-Path -LiteralPath $f)) { Log "  $f : not there"; continue }
            & takeown.exe /f $f /a *> $null
            $takeown = $LASTEXITCODE
            & icacls.exe $f /grant '*S-1-5-32-544:F' *> $null
            $icacls = $LASTEXITCODE
            try { Remove-Item -LiteralPath $f -Force -ErrorAction Stop; $deleted = 'deleted' } catch { $deleted = "not deleted: $($_.Exception.Message)" }
            Log "  $f : takeown $takeown, icacls $icacls, $deleted"
        }

        Log 'After the changes:'
        Log-State
    }
    catch { Log "Step 2 error: $($_.Exception.Message)" }
    finally {
        Undo-SafeMode
        Log 'Restarting in normal mode in 10 s. After signing in, run this script with -Report.'
        & shutdown.exe /r /t 10 /c 'KOS Safe Mode test: back to normal mode'
    }
    exit 0
}

# ---------- Step 1: prepare and restart into Safe Mode ----------
if (-not $ThrowawayVm) {
    Write-Host 'This test changes the boot configuration and turns Defender off: run it only in a throwaway VM, with -ThrowawayVm.' -ForegroundColor Red
    exit 1
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run it from an elevated PowerShell.' -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Path $Work -Force | Out-Null
# Safe Mode may not see the mapped folder: the copy on C: is the one that runs there.
if ($PSCommandPath -ne $Script) { Copy-Item -LiteralPath $PSCommandPath -Destination $Script -Force }
Log "=== Step 1 (Windows $([Environment]::OSVersion.Version), $env:COMPUTERNAME) ==="
try { $mp = Get-MpComputerStatus -ErrorAction Stop; Log "Tamper Protection: $($mp.IsTamperProtected), mode: $($mp.AMRunningMode), real-time: $($mp.RealTimeProtectionEnabled)" }
catch { Log "Get-MpComputerStatus failed: $($_.Exception.Message)" }
Log 'Defender now:'
Log-State

Log 'Question 1: taking WinDefend out of SafeBoot\Minimal'
if (Test-Path "$Minimal\WinDefend") {
    & reg.exe export "HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\WinDefend" (Join-Path $Work 'Minimal-WinDefend.reg') /y *> $null
    try { Remove-Item -Path "$Minimal\WinDefend" -Recurse -ErrorAction Stop; Log '  removed' }
    catch { Log "  NOT removed: $($_.Exception.Message)" }
    Log "  still there: $(Test-Path "$Minimal\WinDefend")"
}
else { Log '  WinDefend is not in SafeBoot\Minimal here' }

Log 'Question 2: registering the test service for Safe Mode'
$bin = "`"$env:SystemRoot\System32\cmd.exe`" /c start `"`" /b `"$PowerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$Script`" -InSafeMode"
# New-Service, not sc.exe: PowerShell 5.1 doesn't escape the quotes inside a native command's argument.
try { New-Service -Name $Service -BinaryPathName $bin -StartupType Automatic -ErrorAction Stop | Out-Null; Log '  service created (LocalSystem, automatic)' }
catch { Log "  service NOT created: $($_.Exception.Message)"; exit 1 }
New-Item -Path "$Minimal\$Service" -Force | Out-Null
Set-Item -Path "$Minimal\$Service" -Value 'Service'
# The * makes RunOnce run in Safe Mode too: if the service never starts, signing in still logs it and undoes Safe Mode.
Set-ItemProperty -Path $RunOnce -Name "*$Service" -Value "`"$PowerShell`" -NoProfile -ExecutionPolicy Bypass -File `"$Script`" -Fallback"

& bcdedit.exe /set '{current}' safeboot minimal *> $null
if ($LASTEXITCODE -ne 0) {
    Log "bcdedit /set safeboot minimal failed (exit $LASTEXITCODE): Safe Mode isn't available here. Undoing."
    Undo-SafeMode
    exit 1
}
Log 'Safe Mode set. Restarting in 15 s: do nothing until Windows is back in normal mode (it restarts twice), then run this script with -Report.'
& shutdown.exe /r /t 15 /c 'KOS Safe Mode test: restarting into Safe Mode'
