<#
.SYNOPSIS
    Runs the agent's `--verify-ds-theme` self-test and reports whether the
    design-system WinUI binding is correctly wired into the agent (#164 / AF3).

.DESCRIPTION
    The self-test boots the real WinUI app (so App.xaml's merged
    PlinkResources.xaml must actually resolve), asserts a foundation brush and
    the ink/on-ink family resolve, the bundled font resource resolves, and
    Anchor's per-product accent override won over the binding's neutral default,
    then writes ds-theme-result.txt next to the exe and exits 0 (pass) / 1
    (fail). This proves the *agent-side wiring*; the binding's own `--smoke`
    (in agent/external/plink-design-system) proves the fonts physically load.

    Headless: no WAM, hub, backend, or screenshot needed — it's the brush/font
    resource resolution that's under test, and that's gradable from the exit
    code alone.
#>

param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$slnPath  = Join-Path $repoRoot 'agent\FocusAgent.sln'
$exePath  = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'
$resultPath = Join-Path (Split-Path $exePath) 'ds-theme-result.txt'

if (-not $SkipBuild) {
    Write-Host "Building $slnPath ..."
    & dotnet build $slnPath -p:Platform=x64 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
}

if (-not (Test-Path $exePath)) { throw "Agent exe not found at: $exePath" }

# Single-instance gating is skipped for this self-test, but kill strays anyway
# so we read our own run's result file.
Get-Process -Name 'FocusAgent.App' -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-Item $resultPath -ErrorAction SilentlyContinue

Write-Host "Launching agent: $exePath --verify-ds-theme"
$proc = Start-Process -FilePath $exePath -ArgumentList '--verify-ds-theme' -PassThru -Wait

Write-Host ""
if (Test-Path $resultPath) {
    Get-Content $resultPath | Write-Host
} else {
    Write-Host "WARNING: ds-theme-result.txt not written."
}

Write-Host ""
Write-Host "Exit code: $($proc.ExitCode)"
exit $proc.ExitCode
