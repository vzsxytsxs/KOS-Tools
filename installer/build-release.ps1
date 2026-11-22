# Builds a KOS release into release\: one zip per part, kos-release.ini (each part's file, SHA-256, size and content id) and
# KOS-Setup.exe (Inno Setup 6.7, installer\KOS-Setup.iss). The apps come from their publish folders: run the /publish skill
# first. The KOS scripts and tools come from the KOS-W11 repository beside this one (src\ProgramData\TheKOS).
# The content id is a SHA-256 over every file's path and SHA-256: the installer downloads a part again only when it changed
# (a zip's own hash changes with every build).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-release.ps1 [-Version 2026.11.22] [-KosW11 <path>]
# kos-release.ini is signed with the private key (-SigningKey; never in the repository). -TestBuild: an installer that also
# takes an http or local /SOURCE (VM tests); never publish it.
param([string]$Version = (Get-Date -Format 'yyyy.M.d'), [string]$KosW11 = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'KOS-W11'),
    [string]$SigningKey = (Join-Path $env:USERPROFILE 'Documents\KOS-private\keys\kos-release.isprivatekey'), [switch]$TestBuild)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'release'
$theKos = Join-Path $KosW11 'src\ProgramData\TheKOS'
if (-not (Test-Path $theKos)) { throw "KOS-W11 not found: $theKos" }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found.' }

# Key, display name, source folder, zip name; Base = the folder itself goes in the zip (ProgramData\TheKOS\<folder>).
$parts = @(
    @{ Key = 'toolbox'; Name = 'KOS Toolbox'; Src = Join-Path $root 'toolbox\publish\win-x64'; Zip = 'KosToolbox.zip' },
    @{ Key = 'scripts'; Name = 'KOS scripts'; Src = Join-Path $theKos 'Setup'; Zip = 'TheKOS-Setup.zip'; Base = $true },
    @{ Key = 'tools'; Name = 'KOS tools'; Src = Join-Path $theKos 'tools'; Zip = 'TheKOS-tools.zip'; Base = $true },
    @{ Key = 'governor'; Name = 'KOS Governor'; Src = Join-Path $root 'governor\publish\win-x64'; Zip = 'KosGovernor.zip' },
    @{ Key = 'engine'; Name = 'KOS Engine'; Src = Join-Path $root 'engine\publish\win-x64'; Zip = 'KosEngine.zip' },
    @{ Key = 'firewall'; Name = 'KOS Firewall'; Src = Join-Path $root 'firewall\publish\win-x64'; Zip = 'KosFirewall.zip' },
    @{ Key = 'equalizer'; Name = 'KOS Equalizer'; Src = Join-Path $root 'equalizer\publish\win-x64'; Zip = 'KosEqualizer.zip' }
)

function Get-ContentId([string]$dir) {
    $lines = Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($dir.Length).TrimStart('\').ToLowerInvariant() + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($lines -join "`n")) | ForEach-Object { $_.ToString('x2') })
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory $out | Out-Null
$ini = @('[release]', "version=$Version", '')
foreach ($p in $parts) {
    if (-not (Test-Path $p.Src)) { throw "$($p.Name): $($p.Src) is missing (publish first)." }
    # Debug symbols of the referenced Core projects come along with the publish: not shipped.
    if (-not $p.Base) { Get-ChildItem $p.Src -Filter *.pdb | Remove-Item }
    $zip = Join-Path $out $p.Zip
    [IO.Compression.ZipFile]::CreateFromDirectory($p.Src, $zip, [IO.Compression.CompressionLevel]::Optimal, [bool]$p.Base)
    $file = Get-Item $zip
    $ini += "[$($p.Key)]", "name=$($p.Name)", "file=$($p.Zip)", "sha256=$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())",
        "size=$($file.Length)", "id=$(Get-ContentId $p.Src)"
    if ($p.Base) { $ini += "folder=$(Split-Path $p.Src -Leaf)" }
    $ini += ''
    Write-Output ("{0,-10} {1,12:N0} bytes  {2}" -f $p.Key, $file.Length, $p.Zip)
}
Set-Content -Path (Join-Path $out 'kos-release.ini') -Value $ini -Encoding ASCII
# The installer takes only a manifest signed with this key (its public half: installer\kos-release.ispublickey).
& (Join-Path (Split-Path $iscc) 'ISSigTool.exe') "--key-file=$SigningKey" sign (Join-Path $out 'kos-release.ini')
if ($LASTEXITCODE -ne 0) { throw "Signing kos-release.ini failed ($LASTEXITCODE)." }

$defines = @("/DSetupVersion=$Version") + $(if ($TestBuild) { @('/DTestBuild') } else { @() })
& $iscc /Q @defines (Join-Path $PSScriptRoot 'KOS-Setup.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Output
