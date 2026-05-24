<#
.SYNOPSIS
    End-to-end verification of the heartbeat-ping feature (#32), fully headless.

.DESCRIPTION
    Drives the backend through the full heartbeat lifecycle without an agent
    UI or dashboard:

      1. (Optional) Patch backend appsettings.Development.json so the
         heartbeat interval, timeout, and scan are fast enough to verify in
         ~15s instead of the ~30s the 10s production interval would need.
      2. Build backend + the HeartbeatVerifier console tool if needed.
      3. Confirm the backend is reachable (does NOT start it — you do that
         in another terminal so its logs are visible).
      4. Run HeartbeatVerifier, which:
            - POSTs /sessions as the seeded Dev Teacher,
            - opens a SignalR connection as the seeded Dev Student,
            - JoinSession, sends 3 Heartbeats, asserts no transition events,
            - stops pinging, waits past the timeout, asserts exactly one
              HeartbeatLost,
            - resumes pinging, asserts exactly one AgentReconnected,
            - POSTs /sessions/{id}/end.
      5. Restore appsettings.Development.json to its original content.

    Total wall time: roughly 15-20 seconds on a warm build.

.PARAMETER BackendUrl
    Backend base URL. Default http://localhost:5276.

.PARAMETER IntervalSeconds
    Test-only heartbeat interval. The verifier waits (2 x interval) + scan
    + slop for stale detection; default 2 keeps the run short. Production
    leaves this at 10s in appsettings.json.

.PARAMETER SkipBuild
    Skip dotnet builds.

.PARAMETER SkipConfigPatch
    Do NOT patch backend appsettings.Development.json. Useful if you are
    pointing at a backend you already configured.

.EXAMPLE
    .\scripts\dev\verify-heartbeat.ps1
    # Patches config, runs, restores config.

.EXAMPLE
    .\scripts\dev\verify-heartbeat.ps1 -SkipBuild -IntervalSeconds 1
    # Fastest possible iteration.
#>

[CmdletBinding()]
param(
    [string]$BackendUrl = 'http://localhost:5276',
    [int]$IntervalSeconds = 2,
    [string]$TeacherOid = '11111111-1111-1111-1111-111111111111',
    [string]$StudentOid = '22222222-2222-2222-2222-222222222222',
    [string]$ClassName = '3A',
    [switch]$SkipBuild,
    [switch]$SkipConfigPatch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function Write-Step($msg) { Write-Host "[verify] $msg" -ForegroundColor Cyan }
function Write-Pass($msg) { Write-Host "[PASS]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "[FAIL]   $msg" -ForegroundColor Red }

$devSettingsPath = Join-Path $repoRoot 'backend\src\Anchor.Api\appsettings.Development.json'
$originalDevSettings = $null
$patched = $false

try {
    # ----------------------------------------------------- config patching
    if (-not $SkipConfigPatch) {
        Write-Step "Patching $($devSettingsPath | Split-Path -Leaf) for fast heartbeat scan..."
        if (Test-Path $devSettingsPath) {
            $originalDevSettings = Get-Content $devSettingsPath -Raw
        }
        $patch = @{
            Heartbeat = @{
                IntervalSeconds    = $IntervalSeconds
                TimeoutMultiplier  = 2
                ScanIntervalSeconds = 1
            }
        }
        $existingJson = if ($originalDevSettings) { $originalDevSettings | ConvertFrom-Json -AsHashtable } else { @{} }
        $existingJson['Heartbeat'] = $patch['Heartbeat']
        ($existingJson | ConvertTo-Json -Depth 10) | Out-File -FilePath $devSettingsPath -Encoding utf8
        $patched = $true
        Write-Step 'Restart the backend if it is currently running so the patched config is picked up.'
    }

    # ---------------------------------------------------------------- build
    if (-not $SkipBuild) {
        Write-Step 'Building backend...'
        & dotnet build (Join-Path $repoRoot 'backend\Anchor.sln') --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

        Write-Step 'Building HeartbeatVerifier...'
        & dotnet build (Join-Path $repoRoot 'tools\dev\HeartbeatVerifier\HeartbeatVerifier.csproj') --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'HeartbeatVerifier build failed.' }
    }

    # -------------------------------------------------------------- backend
    Write-Step "Checking backend at $BackendUrl ..."
    $backendUp = $false
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $BackendUrl -TimeoutSec 2 -ErrorAction Stop | Out-Null
        $backendUp = $true
    } catch {
        $sc = $_.Exception.Response.StatusCode.value__
        if ($null -ne $sc -and ($sc -eq 401 -or $sc -eq 404)) { $backendUp = $true }
    }
    if (-not $backendUp) {
        throw "Backend not reachable at $BackendUrl. Start it first with: ``dotnet run --project backend/src/Anchor.Api --no-launch-profile --urls $BackendUrl``"
    }
    Write-Step 'Backend reachable.'

    # ----------------------------------------------------------- run verify
    Write-Step "Running HeartbeatVerifier (interval=${IntervalSeconds}s, expect ~$([int]($IntervalSeconds * 2 + 4))s of waits)..."
    & dotnet run --project (Join-Path $repoRoot 'tools\dev\HeartbeatVerifier\HeartbeatVerifier.csproj') --no-build -- `
        --backend $BackendUrl `
        --interval-seconds $IntervalSeconds `
        --teacher-oid $TeacherOid `
        --student-oid $StudentOid `
        --class-name $ClassName
    $verifierCode = $LASTEXITCODE
    if ($verifierCode -ne 0) {
        Write-Fail "HeartbeatVerifier exited $verifierCode"
        exit $verifierCode
    }

    Write-Pass 'Heartbeat end-to-end verification complete.'
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
finally {
    if ($patched) {
        if ($null -ne $originalDevSettings) {
            $originalDevSettings | Out-File -FilePath $devSettingsPath -Encoding utf8
        } else {
            Remove-Item -Force -ErrorAction SilentlyContinue $devSettingsPath
        }
        Write-Step "Restored $($devSettingsPath | Split-Path -Leaf)."
    }
}
