<#
.SYNOPSIS
    Registers (or unregisters) the Anchor witness native-messaging host for Edge
    in HKCU — no admin required (#146 part 1).

.DESCRIPTION
    The agent-as-witness tamper detection needs Edge to launch
    `anchor-witness-host.exe` when the extension calls
    `chrome.runtime.connectNative("net.anchor.witness")`. Edge finds the host via
    a per-user registry key under
    `HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\net.anchor.witness`
    whose default value points at a host-manifest JSON. The manifest's `path`
    must be the ABSOLUTE path to the host exe, and `allowed_origins` must name the
    pinned extension ID (akkfdaclmpfcnjalcifkcbhgjnnopman, see extension/README.md).

    This script builds the host (unless -SkipBuild), writes the manifest with the
    real exe path next to the exe, and sets the registry key. Re-run it after a
    rebuild only if the exe path changed (it won't for a normal Debug build).

    Production installs will do the equivalent from the agent installer; this is
    the dev-machine shortcut.

.PARAMETER Unregister
    Remove the registry key (leaves the built exe/manifest in place).

.PARAMETER Configuration
    Build configuration of the host exe to point at. Default: Debug.

.PARAMETER SkipBuild
    Don't build the host first; just (re)write the manifest + registry key.

.EXAMPLE
    pwsh scripts/dev/register-witness-host.ps1
    pwsh scripts/dev/register-witness-host.ps1 -Unregister
#>

param(
    [switch]$Unregister,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$HostName = 'net.anchor.witness'
$RegKey = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
$RepoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$HostProject = Join-Path $RepoRoot 'agent\src\FocusAgent.WitnessHost\FocusAgent.WitnessHost.csproj'

if ($Unregister) {
    if (Test-Path $RegKey) {
        Remove-Item $RegKey -Force
        Write-Host "Unregistered $HostName (removed $RegKey)."
    } else {
        Write-Host "$HostName was not registered (no $RegKey)."
    }
    return
}

if (-not $SkipBuild) {
    Write-Host "Building witness host ($Configuration)..."
    dotnet build $HostProject -c $Configuration --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Host build failed (exit $LASTEXITCODE)." }
}

$ExePath = Join-Path $RepoRoot "agent\src\FocusAgent.WitnessHost\bin\$Configuration\net10.0\anchor-witness-host.exe"
if (-not (Test-Path $ExePath)) {
    throw "Host exe not found at $ExePath. Build it first (omit -SkipBuild)."
}
$ExePath = (Resolve-Path $ExePath).Path

# Fill the committed template with the absolute exe path and write the manifest
# next to the exe, where it won't be clobbered by a `git clean` of the repo.
$Template = Get-Content (Join-Path $RepoRoot 'agent\src\FocusAgent.WitnessHost\net.anchor.witness.template.json') -Raw
$manifest = $Template -replace 'REPLACED_AT_REGISTRATION_WITH_ABSOLUTE_EXE_PATH', ($ExePath -replace '\\', '\\')
$ManifestPath = Join-Path (Split-Path $ExePath -Parent) "$HostName.json"
Set-Content -Path $ManifestPath -Value $manifest -Encoding utf8

New-Item -Path $RegKey -Force | Out-Null
Set-ItemProperty -Path $RegKey -Name '(default)' -Value $ManifestPath

Write-Host "Registered $HostName"
Write-Host "  exe:      $ExePath"
Write-Host "  manifest: $ManifestPath"
Write-Host "  regkey:   $RegKey -> (default) = $ManifestPath"
Write-Host ""
Write-Host "Restart Edge so it picks up the new native-messaging host."
