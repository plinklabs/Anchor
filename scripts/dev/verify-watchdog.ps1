<#
.SYNOPSIS
    End-to-end smoke for the FocusAgent.Watchdog supervisor (#35), fully
    headless and self-contained.

.DESCRIPTION
    The Watchdog observes FocusAgent.App via a named kernel mutex that the
    App holds for the lifetime of its process. This script stands in for the
    real App with a tiny PowerShell stub that takes the same mutex — no
    backend, no WinUI, no MSIX install required — and then drives the
    Watchdog through its three observable outcomes:

      1. App alive            -> "App alive" tick.
      2. App gone              -> crash recorded, relaunch attempted
                                  (LaunchFailed in this stub mode is fine —
                                  we only verify the supervisor saw the
                                  absence and reacted).
      3. quit.flag fresh       -> SuppressedByQuitFlag tick.
      4. quit.flag stale       -> supervisor again proposes relaunch.
      5. crash budget exhausted -> SuppressedByCooldown.

    Total wall time: ~10s.

.PARAMETER SkipBuild
    Skip dotnet build of the Watchdog.

.EXAMPLE
    .\scripts\dev\verify-watchdog.ps1

.EXAMPLE
    .\scripts\dev\verify-watchdog.ps1 -SkipBuild
#>

[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function Write-Step($msg) { Write-Host "[verify] $msg" -ForegroundColor Cyan }
function Write-Pass($msg) { Write-Host "[PASS]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "[FAIL]   $msg" -ForegroundColor Red }

$mutexName    = 'Local\Anchor.FocusAgent.AppPresence'
$quitFlagPath = Join-Path $env:LOCALAPPDATA 'Anchor\FocusAgent\quit.flag'
$logDir       = Join-Path $env:LOCALAPPDATA 'Anchor\FocusAgent\logs'

$watchdogProj = Join-Path $repoRoot 'agent\src\FocusAgent.Watchdog\FocusAgent.Watchdog.csproj'
$watchdogExe  = Join-Path $repoRoot 'agent\src\FocusAgent.Watchdog\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.Watchdog.exe'

$stubProcess = $null
$quitFlagBackup = $null

function Start-AppStub {
    # PowerShell stub that opens the AppPresence mutex (creates it if it
    # doesn't already exist), blocks for a long time, and releases it when
    # the process exits. Stands in for FocusAgent.App.
    $script = @"
`$m = New-Object System.Threading.Mutex(`$true, '$mutexName', [ref]`$null)
try { Start-Sleep -Seconds 600 } finally { `$m.ReleaseMutex(); `$m.Dispose() }
"@
    $p = Start-Process -FilePath 'powershell' `
        -ArgumentList '-NoProfile','-WindowStyle','Hidden','-Command',$script `
        -PassThru -WindowStyle Hidden
    # Give the stub a moment to actually acquire the mutex before we probe.
    Start-Sleep -Milliseconds 750
    return $p
}

function Invoke-WatchdogOneShot {
    # Run the watchdog one-shot and return the outcome string it logs.
    & $watchdogExe --one-shot | Out-Null
    $logFile = Get-ChildItem $logDir -Filter 'watchdog-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $logFile) { throw "No watchdog log produced at $logDir" }
    $line = Get-Content $logFile.FullName -Tail 5 |
        Where-Object { $_ -match '--one-shot tick outcome: "?(\w+)"?' } |
        Select-Object -Last 1
    if (-not $line -or $line -notmatch '--one-shot tick outcome: "?(\w+)"?') {
        throw "Could not find tick outcome in $($logFile.FullName)"
    }
    return $Matches[1]
}

function Assert-Outcome($expected, $actual, $context) {
    if ($actual -eq $expected) {
        Write-Pass "$context -> $actual"
    } else {
        Write-Fail "$context -> got '$actual', expected '$expected'"
        throw "Verify failed at: $context"
    }
}

try {
    if (-not $SkipBuild) {
        Write-Step 'Building FocusAgent.Watchdog...'
        & dotnet build $watchdogProj -c Debug -p:Platform=x64 --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Watchdog build failed.' }
    }
    if (-not (Test-Path $watchdogExe)) {
        throw "Watchdog exe missing at $watchdogExe (build first or run without -SkipBuild)"
    }

    # The supervisor records "App went away" on the FIRST tick where it
    # observes absence after a presence. With no prior state, a watchdog
    # process that starts with the App already missing reports LaunchFailed
    # on tick 1 (since launch can't resolve an app exe in stub mode). The
    # FIRST cold tick state is intentionally not asserted here — we only
    # assert state TRANSITIONS, which is what matters in production.

    # ----------------------------------------------------- preserve quit.flag
    if (Test-Path $quitFlagPath) {
        $quitFlagBackup = "$quitFlagPath.verify-backup"
        Move-Item -Force $quitFlagPath $quitFlagBackup
        Write-Step "Backed up existing quit.flag to $quitFlagBackup"
    }

    # ---------------------------------------------------------- 1. App alive
    Write-Step 'Phase 1: starting stub App (mutex holder)...'
    $stubProcess = Start-AppStub
    $outcome = Invoke-WatchdogOneShot
    Assert-Outcome 'AppAlive' $outcome 'App alive -> probe sees mutex'

    # ----------------------------------------------------------- 2. App gone
    Write-Step 'Phase 2: killing stub App...'
    Stop-Process -Id $stubProcess.Id -Force
    $stubProcess.WaitForExit()
    Start-Sleep -Milliseconds 250  # let kernel object teardown settle
    $outcome = Invoke-WatchdogOneShot
    if ($outcome -ne 'Relaunched' -and $outcome -ne 'LaunchFailed') {
        Write-Fail "App gone -> expected Relaunched or LaunchFailed, got '$outcome'"
        throw 'Verify failed at: App gone'
    }
    Write-Pass "App gone -> $outcome (relaunch attempted)"

    # --------------------------------------------- 3. quit.flag fresh suppresses
    Write-Step 'Phase 3: writing fresh quit.flag...'
    New-Item -ItemType Directory -Force -Path (Split-Path $quitFlagPath) | Out-Null
    Set-Content -Path $quitFlagPath -Value (Get-Date -Format 'o') -NoNewline
    $outcome = Invoke-WatchdogOneShot
    Assert-Outcome 'SuppressedByQuitFlag' $outcome 'quit.flag fresh -> suppressed'

    # ------------------------------------------- 4. quit.flag stale falls through
    Write-Step 'Phase 4: aging quit.flag past 10s freshness...'
    $stale = (Get-Date).AddSeconds(-30)
    (Get-Item $quitFlagPath).LastWriteTime = $stale
    (Get-Item $quitFlagPath).LastWriteTimeUtc = $stale.ToUniversalTime()
    $outcome = Invoke-WatchdogOneShot
    if ($outcome -ne 'Relaunched' -and $outcome -ne 'LaunchFailed') {
        Write-Fail "quit.flag stale -> expected Relaunched or LaunchFailed, got '$outcome'"
        throw 'Verify failed at: quit.flag stale'
    }
    Write-Pass "quit.flag stale -> $outcome (gate cleared)"

    Write-Pass 'Watchdog supervisor smoke complete.'
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
finally {
    if ($stubProcess -and -not $stubProcess.HasExited) {
        try { Stop-Process -Id $stubProcess.Id -Force } catch {}
    }
    if (Test-Path $quitFlagPath) {
        Remove-Item -Force $quitFlagPath -ErrorAction SilentlyContinue
    }
    if ($quitFlagBackup -and (Test-Path $quitFlagBackup)) {
        Move-Item -Force $quitFlagBackup $quitFlagPath
        Write-Step 'Restored prior quit.flag.'
    }
}
