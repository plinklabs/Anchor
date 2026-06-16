#requires -Version 5.1
<#
.SYNOPSIS
    Build the agent unpackaged, bake in the per-deployment config, and produce a
    Velopack release in ./artifacts/velopack (#209).

.DESCRIPTION
    This is the local-runnable core of the tag-triggered release pipeline
    (agent-release.yml). The workflow installs the `vpk` tool and the .NET SDK,
    sets the per-deployment config env vars from CI `vars.*`/`secrets.*`, then
    calls this script; running it locally with the same env vars reproduces the
    exact release artifacts, which is the point — the workflow stays a thin
    wrapper and the build logic is reviewable/testable here.

    Steps:
      1. Read the single version source (<VersionPrefix> in
         agent/Directory.Build.props, #208) — the same number that stamps the
         assemblies becomes the Velopack package version.
      2. `dotnet publish` FocusAgent.App self-contained for win-x64 in Release
         (Release => the agent runs in the Production environment, so it loads
         appsettings.Production.json — see agent/README.md).
      3. Publish the witness host (anchor-witness-host.exe) into the same folder
         so the native-messaging path works in the installed agent.
      4. Substitute the #{...}# placeholders in the PUBLISHED
         appsettings.Production.json (never the committed template) from env vars
         via substitute-config.ps1.
      5. `vpk pack` the publish folder into ./artifacts/velopack, with the app
         icon, a success/conclusion page, and the portable bundle suppressed
         (#247). Setup.exe runs the freshly-installed agent automatically — that
         is Velopack's default post-install behaviour, so no extra flag is
         needed for the "launch after install" acceptance criterion.

    Release artifacts (#247): the output is a Setup.exe, the RELEASES feed, and
    the full/delta nupkg. The nupkg + RELEASES are deliberately KEPT — they are
    the Velopack auto-update feed the installed agent's UpdateManager downloads
    (#224); dropping them would break delta auto-update. Only the standalone
    portable .zip is suppressed (`--noPortable`), since the agent is installed,
    not run portably, and it only adds noise to the release page.

    Auto-update wiring (the agent's UpdateManager pointed at the GitHub Releases
    feed) and re-homing auto-start to an HKCU Run key are tracked as separate
    follow-up issues — see the PR for #209. This script + workflow deliver the
    build/pack/publish half so a tag produces an installable, config-correct
    agent.

.PARAMETER Version
    Override the package version. Defaults to <VersionPrefix> from
    Directory.Build.props. The workflow passes the tag's version so a mismatch
    between the tag and the committed version fails loudly rather than shipping a
    surprising number.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.PARAMETER SkipPack
    Do everything up to and including config substitution but skip `vpk pack`.
    Lets the build + substitution run on a box without the `vpk` tool (and is how
    CI can validate the publish/substitution legs separately from packaging).
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Runtime = 'win-x64',
    [switch] $SkipPack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentDir  = Split-Path -Parent $scriptDir
$propsPath = Join-Path $agentDir 'Directory.Build.props'
$appProj   = Join-Path $agentDir 'src/FocusAgent.App/FocusAgent.App.csproj'
$witProj   = Join-Path $agentDir 'src/FocusAgent.WitnessHost/FocusAgent.WitnessHost.csproj'
$dsProj    = Join-Path $agentDir 'external/plink-design-system/bindings/winui/src/PlinkDesignSystem/PlinkDesignSystem.csproj'
$publishDir   = Join-Path $agentDir 'artifacts/publish'
$velopackDir  = Join-Path $agentDir 'artifacts/velopack'

function Get-VersionFromProps {
    [xml] $doc = Get-Content -LiteralPath $propsPath -Raw
    $prefix = $doc.Project.PropertyGroup.VersionPrefix
    if (-not $prefix) { $prefix = $doc.Project.PropertyGroup.Version }
    if (-not $prefix) {
        throw "Directory.Build.props declares no <VersionPrefix>/<Version>."
    }
    return ([string] $prefix).Trim()
}

if (-not $Version) {
    $Version = Get-VersionFromProps
}
else {
    $declared = Get-VersionFromProps
    if ($Version -ne $declared) {
        throw "Requested version '$Version' does not match the committed " +
              "single version source '$declared' in Directory.Build.props. " +
              "Bump <VersionPrefix> and tag to match (see agent/README.md)."
    }
}

Write-Host "== Packaging agent v$Version ($Runtime) =="

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

# Build the design-system WinUI binding for the SAME RID first. A self-contained
# WinUI publish reads the referenced library's RID-specific PlinkDesignSystem.pri
# (…/win-x64/PlinkDesignSystem.pri); if the dependency was only built without a
# RID that file is absent and the app publish fails with PRI175/PRI252. Building
# it explicitly here makes the publish order-independent (the agent-e2e Debug
# build sidesteps this only because it doesn't publish self-contained per-RID).
dotnet build $dsProj -c Release -r $Runtime -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet build (design-system) failed ($LASTEXITCODE)." }

# Self-contained: the target machines (school Windows boxes) can't be assumed to
# have the .NET runtime, and Velopack ships the whole folder. WinUI already
# self-contains the Windows App SDK (WindowsAppSDKSelfContained=true).
dotnet publish $appProj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsPackageType=None `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (app) failed ($LASTEXITCODE)." }

# Witness host into the SAME folder so the installed agent can launch
# anchor-witness-host.exe next to itself (native-messaging path, #146/#204).
dotnet publish $witProj `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (witness host) failed ($LASTEXITCODE)." }

# Bake the per-deployment backend/Entra config into the PUBLISHED copy.
$publishedProdConfig = Join-Path $publishDir 'appsettings.Production.json'
& (Join-Path $scriptDir 'substitute-config.ps1') -Path $publishedProdConfig

if ($SkipPack) {
    Write-Host "-SkipPack set: build + config substitution done, skipping vpk pack."
    return
}

# vpk pack -> ./artifacts/velopack (a Setup.exe + the delta/full nupkg + the
# RELEASES feed the agent's UpdateManager reads). The workflow then uploads this
# folder to the GitHub Release for the tag.
#
# --icon       gives the Setup.exe, Start-menu shortcut, and uninstall entry the
#              Anchor icon instead of a blank default (#247). Reuses the app's
#              committed TrayIcon.ico — the same mark the running agent shows.
# --noPortable drops the standalone portable .zip; the agent is installed, never
#              run portably, so the zip is just release-page noise (#247). The
#              nupkg/RELEASES auto-update feed is intentionally NOT dropped.
# --instConclusion gives Setup a final success page so the install isn't a silent
#              no-op (#247); Velopack already auto-launches the agent afterwards.
$mainExe = 'FocusAgent.App.exe'
$iconPath = Join-Path $agentDir 'src/FocusAgent.App/Assets/TrayIcon.ico'
$conclusionPath = Join-Path $scriptDir 'release-assets/install-conclusion.txt'
if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "App icon not found for vpk pack --icon: $iconPath"
}
if (-not (Test-Path -LiteralPath $conclusionPath)) {
    throw "Installer conclusion text not found for vpk pack --instConclusion: $conclusionPath"
}
New-Item -ItemType Directory -Force -Path $velopackDir | Out-Null

vpk pack `
    --packId 'Anchor.Agent' `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe $mainExe `
    --packTitle 'Anchor Focus Agent' `
    --packAuthors 'Plink Labs' `
    --icon $iconPath `
    --instConclusion $conclusionPath `
    --noPortable `
    --outputDir $velopackDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }

Write-Host "== Velopack release written to $velopackDir =="
