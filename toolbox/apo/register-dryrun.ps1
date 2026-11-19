# Dry run of registering KosEqApo.dll on one audio endpoint: prints every key and value it would create or change (before ->
# after) and what undoing it restores. Reads only; writes nothing. Run as administrator to read the endpoint keys' ACLs.
#   .\register-dryrun.ps1 -Endpoint 3718cd90-ba40-40ae-9dfc-93593f73d916 [-Flow Render|Capture] [-Dll "C:\Program Files\KOS\toolbox\KosEqApo.dll"]
param(
    [Parameter(Mandatory)][string]$Endpoint,
    [ValidateSet('Render', 'Capture')][string]$Flow = 'Render',
    [string]$Dll = "$env:ProgramFiles\KOS\toolbox\KosEqApo.dll"
)
$ErrorActionPreference = 'Stop'
$clsid = '{610C4861-EB61-417D-A653-45B03D493123}'
$iidApo = '{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}' # IID_IAudioProcessingObject
$Endpoint = $Endpoint.Trim('{', '}').ToLowerInvariant()
$changes = [System.Collections.Generic.List[object]]::new()

function Current([string]$key, [string]$name) {
    $path = "Registry::$key"
    if (-not (Test-Path $path)) { return '<key absent>' }
    $item = Get-Item $path
    if ($null -eq $item.GetValue($name, $null)) { return '<absent>' }
    $kind = $item.GetValueKind($name)
    $value = $item.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
    if ($value -is [array]) { $value = ($value -join ' | ') }
    "$value ($kind)"
}
function Plan([string]$key, [string]$name, [string]$kind, $value) {
    $shown = if ($value -is [array]) { $value -join ' | ' } else { $value }
    $changes.Add([pscustomobject]@{ Key = $key; Name = $(if ($name) { $name } else { '(Default)' }); Before = (Current $key $name); After = "$shown ($kind)" })
}

# 1. COM class: audiodg creates the APO through it.
$com = "HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\$clsid"
Plan $com '' 'String' 'KOS Equalizer'
Plan "$com\InprocServer32" '' 'String' $Dll
Plan "$com\InprocServer32" 'ThreadingModel' 'String' 'Both'

# 2. APO registration (what RegisterAPO writes).
$apo = "HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\$clsid"
foreach ($v in @(
        @('FriendlyName', 'String', 'KOS Equalizer'), @('Copyright', 'String', 'KOS'), @('MajorVersion', 'DWord', 1), @('MinorVersion', 'DWord', 0),
        @('Flags', 'DWord', 0xF), @('MinInputConnections', 'DWord', 1), @('MaxInputConnections', 'DWord', 1), @('MinOutputConnections', 'DWord', 1),
        @('MaxOutputConnections', 'DWord', 1), @('MaxInstances', 'DWord', 0xFFFFFFFF), @('NumAPOInterfaces', 'DWord', 1), @('APOInterface0', 'String', $iidApo))) {
    Plan $apo $v[0] $v[1] $v[2]
}

# 3. The endpoint's effects: KOS Equalizer as an endpoint effect (after the mix), next to any the driver already has.
$fx = "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$Flow\{$Endpoint}\FxProperties"
$efx = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'         # PKEY_FX_EndpointEffectClsid
$compositeEfx = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15' # PKEY_CompositeFX_EndpointEffectClsid (several effects)
$existing = @()
if (Test-Path "Registry::$fx") {
    $item = Get-Item "Registry::$fx"
    $composite = $item.GetValue($compositeEfx, $null)
    $single = $item.GetValue($efx, $null)
    if ($composite) { $existing = @($composite) } elseif ($single) { $existing = @($single) }
}
$list = @($existing | Where-Object { $_ -ne $clsid }) + $clsid
Plan $fx $compositeEfx 'MultiString' $list

# 4. Unsigned APOs only load with the protected audio path off.
$audio = 'HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio'
Plan $audio 'DisableProtectedAudioDG' 'DWord' 1

$changes | Format-Table -Wrap -AutoSize | Out-String -Width 220 | Write-Output

"Endpoint key:"
if (Test-Path "Registry::$fx") {
    $acl = Get-Acl "Registry::$fx"
    "  owner $($acl.Owner)"
    $acl.Access | ForEach-Object { "  $($_.IdentityReference): $($_.RegistryRights) ($($_.AccessControlType))" }
} else { "  $fx doesn't exist: creating it needs CreateSubKey on its parent (owned by SYSTEM)." }
""
"DLL: $Dll"
if (Test-Path $Dll) { (Get-Acl $Dll).Access | ForEach-Object { "  $($_.IdentityReference): $($_.FileSystemRights)" } } else { "  not there yet" }
""
"Undo: every value above goes back to its 'Before' (absent values and keys are deleted), then the audio service restarts."
"Nothing was written."
