<#
.SYNOPSIS
    End-to-end verification of the agent's SessionStarted -> toast flow,
    fully headless. No WAM picker, no Flutter dashboard, no human input.

.DESCRIPTION
    Verifies the chain that #41 originally broke:
      1. Build backend + agent if needed.
      2. Start backend in Development on the agreed dev port (5276) if not
         already running.
      3. Launch the agent in headless mode (--inject-token bypasses WAM,
         --status-endpoint exposes JSON state on loopback).
      4. Wait for the agent to reach Connected by polling the status endpoint.
      5. POST /sessions as the seeded Dev Teacher using only the
         X-Dev-Impersonate-Oid header (#44 unlocked this).
      6. Poll the agent's status endpoint until activeSessionId is non-null,
         which is the moment the agent's coordinator processed SessionStarted
         and pushed the toast onto the dispatcher.
      7. Print PASS/FAIL and exit with matching code.

    Total wall time: typically 5-10 seconds on a warm build, 15-30 seconds
    cold. Cleans up the agent process on exit; leaves the backend running
    unless it was started by this script.

.PARAMETER BackendUrl
    Backend base URL. Default http://localhost:5276 (matches dashboard +
    backend launchSettings defaults).

.PARAMETER StatusPort
    Loopback port for the agent's status endpoint. Default 5295.

.PARAMETER TeacherOid
    Seeded Dev Teacher OID. Default 11111111-1111-1111-1111-111111111111
    (matches DevDataSeeder).

.PARAMETER StudentOid
    Seeded Dev Student OID for the agent to impersonate. Default
    22222222-2222-2222-2222-222222222222 (matches DevDataSeeder).

.PARAMETER ClassName
    Seeded class name to start a session for. Default "3A" (matches DevDataSeeder).

.PARAMETER SkipBuild
    Skip the dotnet builds. Useful when iterating and only the script changed.

.EXAMPLE
    .\scripts\dev\verify-session-start.ps1
    # Builds if needed, starts backend if needed, runs the full verify cycle.

.EXAMPLE
    .\scripts\dev\verify-session-start.ps1 -SkipBuild
    # Same but trusts the existing build artifacts.
#>

[CmdletBinding()]
param(
    [string]$BackendUrl = 'http://localhost:5276',
    [int]$StatusPort = 5295,
    [string]$TeacherOid = '11111111-1111-1111-1111-111111111111',
    [string]$StudentOid = '22222222-2222-2222-2222-222222222222',
    [string]$ClassName = '3A',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function Write-Step($msg) { Write-Host "[verify] $msg" -ForegroundColor Cyan }
function Write-Pass($msg) { Write-Host "[PASS]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "[FAIL]   $msg" -ForegroundColor Red }

$agentProcess = $null

try {
    # ---------------------------------------------------------------- build
    if (-not $SkipBuild) {
        Write-Step 'Building backend...'
        & dotnet build (Join-Path $repoRoot 'backend\Anchor.sln') --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

        Write-Step 'Building agent (x64)...'
        & dotnet build (Join-Path $repoRoot 'agent\FocusAgent.sln') -p:Platform=x64 --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
    }

    # -------------------------------------------------------------- backend
    Write-Step "Checking backend at $BackendUrl ..."
    $backendUp = $false
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $BackendUrl -TimeoutSec 2 -ErrorAction Stop | Out-Null
        $backendUp = $true
    } catch {
        # Treat 401/404 from a running backend the same as reachable.
        $sc = $_.Exception.Response.StatusCode.value__
        if ($null -ne $sc -and ($sc -eq 401 -or $sc -eq 404)) { $backendUp = $true }
    }
    if (-not $backendUp) {
        throw "Backend not reachable at $BackendUrl. Start it first with: ``dotnet run --project backend/src/Anchor.Api --no-launch-profile --urls $BackendUrl``"
    }
    Write-Step 'Backend reachable.'

    # --------------------------------------------------------------- agent
    Write-Step 'Launching agent (--inject-token, --status-endpoint $StatusPort)...'
    $agentExe = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'
    if (-not (Test-Path $agentExe)) { throw "Agent exe not found at $agentExe (rebuild?)." }

    # Agent reads Dev:ImpersonateOid from appsettings.Development.json. We must
    # ensure that file points at the student OID we want it to impersonate.
    $devSettingsPath = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\appsettings.Development.json'
    if (Test-Path $devSettingsPath) {
        $existing = Get-Content $devSettingsPath -Raw | ConvertFrom-Json
        $oidNow = $existing.Dev.ImpersonateOid
        if ($oidNow -ne $StudentOid) {
            Write-Step "Note: agent's Dev:ImpersonateOid is '$oidNow', verify uses '$StudentOid' (env: -StudentOid to override)."
        }
    } else {
        Write-Step 'agent appsettings.Development.json not deployed; --inject-token will throw without it.'
    }

    $agentProcess = Start-Process -FilePath $agentExe `
        -ArgumentList @('--inject-token', '--status-endpoint', $StatusPort) `
        -PassThru
    Write-Step "Agent pid=$($agentProcess.Id)"

    # ------------------------------------------------- wait for Connected
    Write-Step 'Waiting for agent to reach Connected...'
    $statusUrl = "http://127.0.0.1:$StatusPort/status"
    $deadline = (Get-Date).AddSeconds(15)
    $status = $null
    do {
        Start-Sleep -Milliseconds 400
        try {
            $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2
        } catch { $status = $null }
        $statusKind = if ($status) { $status.connectionStatus } else { '<unreachable>' }
    } while ($statusKind -ne 'Connected' -and (Get-Date) -lt $deadline)

    if ($statusKind -ne 'Connected') {
        $lastErr = if ($status) { $status.lastError } else { '<none>' }
        throw "Agent did not reach Connected within 15s (last status: $statusKind, error: $lastErr)"
    }
    Write-Pass "Agent connected as '$($status.displayName)'"

    # --------------------------------------------------- find class id
    Write-Step "Finding class id for '$ClassName' via /classes..."
    $classes = Invoke-RestMethod -Uri "$BackendUrl/classes" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } `
        -TimeoutSec 5
    $targetClass = $classes | Where-Object { $_.name -eq $ClassName } | Select-Object -First 1
    if ($null -eq $targetClass) {
        throw "Class '$ClassName' not found via /classes as teacher $TeacherOid. Did the dev seeder run?"
    }
    Write-Step "Class id: $($targetClass.id)"

    # -------------------------------------------------- POST /sessions
    Write-Step 'POSTing /sessions as Dev Teacher (impersonation header only)...'
    $body = @{ classId = $targetClass.id; mode = 'Strict'; bundleIds = @() } | ConvertTo-Json
    $sessionResponse = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' } `
        -Body $body `
        -TimeoutSec 5
    Write-Pass "Session created: $($sessionResponse.id)"

    # -------------------------------- wait for agent to see SessionStarted
    Write-Step 'Polling agent /status for activeSessionId...'
    $deadline = (Get-Date).AddSeconds(5)
    $seen = $false
    do {
        Start-Sleep -Milliseconds 200
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { }
        if ($status -and $status.activeSessionId -eq $sessionResponse.id) { $seen = $true; break }
    } while ((Get-Date) -lt $deadline)

    if (-not $seen) {
        $lastActive = if ($status -and $status.activeSessionId) { $status.activeSessionId } else { '<none>' }
        Write-Fail "Agent did not see SessionStarted within 5s. Last activeSessionId: $lastActive"
        exit 1
    }
    Write-Pass "Agent received SessionStarted and toast is active for session $($status.activeSessionId)."

    # -------------------------------------------------- summary
    Write-Host ''
    Write-Host '=================================' -ForegroundColor Green
    Write-Host '  END-TO-END VERIFY: PASS' -ForegroundColor Green
    Write-Host '=================================' -ForegroundColor Green
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
finally {
    if ($agentProcess) {
        Write-Step "Stopping agent pid=$($agentProcess.Id)..."
        try { Stop-Process -Id $agentProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}
