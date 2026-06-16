<#
.SYNOPSIS
    Regenerates the curated student-facing agent screenshots embedded on the
    Anchor website (#251), writing the named PNG set into `website/assets/`.

.DESCRIPTION
    The agent's join toast, app-block overlay, main window, and tray menu are
    pure WinUI 3 / DirectComposition surfaces — they can't be rendered to a file
    headlessly the way the Flutter dashboard can. So this drives the very same
    `--show-test-*` self-tests the visual-enforcement e2e uses (real surfaces,
    presentable demo content from FocusAgent.App.SelfTestDemoContent — "Ms Rivera",
    class PLINK-3B, a readable allowlist — no backend, no auth, no secrets), finds
    each surface's HWND, BitBlts its rect, and saves a fixed, named PNG into
    `website/assets/`.

    It runs the opt-in `WebsiteScreenshots` generator in the integration suite,
    gated on ANCHOR_WEBSITE_SHOTS=1 so a routine test run never overwrites the
    committed images. See website/assets/README.md.

    Produces (1:1 with the agent surfaces on the website):
      website/assets/agent-join-toast.png
      website/assets/agent-block-overlay.png
      website/assets/agent-main-window.png
      website/assets/agent-tray-menu.png

    The browser-side shots (the extension block page + popup) are reused from
    extension/store-listing/ and are NOT regenerated here.

.PARAMETER SkipBuild
    Skip the agent build (reuse the existing x64 Debug exe).
#>

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Resolve-Path "$PSScriptRoot\..\.."
$agentProj  = Join-Path $repoRoot 'agent\src\FocusAgent.App\FocusAgent.App.csproj'
$testProj   = Join-Path $repoRoot 'agent\tests\FocusAgent.IntegrationTests\FocusAgent.IntegrationTests.csproj'
$assetsDir  = Join-Path $repoRoot 'website\assets'

if (-not $SkipBuild) {
    Write-Host "Building agent (x64 Debug) ..."
    & dotnet build $agentProj -p:Platform=x64 -c Debug --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Agent build failed (exit $LASTEXITCODE)" }
}

# Kill any stray agent so the only surfaces we capture are the self-tests'.
Get-Process -Name 'FocusAgent.App' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Generating website screenshots into $assetsDir ..."
$env:ANCHOR_WEBSITE_SHOTS = '1'
try {
    & dotnet test $testProj --filter 'Category=WebsiteScreenshots' --nologo -v:q
    $testExit = $LASTEXITCODE
}
finally {
    Remove-Item Env:\ANCHOR_WEBSITE_SHOTS -ErrorAction SilentlyContinue
}
if ($testExit -ne 0) { throw "Screenshot generation failed (exit $testExit)" }

Write-Host ""
Write-Host "WEBSITE SCREENSHOTS GENERATED"
foreach ($name in 'agent-join-toast', 'agent-block-overlay', 'agent-main-window', 'agent-tray-menu') {
    $p = Join-Path $assetsDir "$name.png"
    if (Test-Path $p) {
        $size = (Get-Item $p).Length
        Write-Host ("  {0,-26} {1,8:N0} bytes" -f "$name.png", $size)
    } else {
        Write-Warning "  $name.png was not produced"
    }
}
