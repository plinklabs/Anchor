<#
.SYNOPSIS
    End-to-end verification of the manual join-by-code path (#34), headless.

.DESCRIPTION
    Verifies the chain that #34 introduces:
      1. Build backend + agent if needed.
      2. Confirm backend is reachable at the dev port (assumes it's already
         running; start it manually beforehand if not).
      3. Launch the agent in headless mode impersonating the seeded
         OUTSIDER student (Dev Outsider, OID 33333333...). The outsider is
         deliberately NOT enrolled in any class, so the roster-based
         SessionStart push will skip them — the only way they can get into
         a session is by typing the code.
      4. Wait for the agent to reach Connected via /status polling.
      5. POST /sessions as Dev Teacher to start a session for class 3A.
         Capture the join code from the response.
      6. POST /sessions/join-by-code with the outsider's impersonation
         header and the captured code. Expect 200.
      7. Poll the agent's /status until activeSessionId matches — proves
         the SessionStarted broadcast made it through.
      8. Exercise the error paths headlessly: unknown code -> 404,
         repeated wrong codes -> 429.
      9. PASS/FAIL summary, exit code matches.

    Total wall time: ~15s on a warm build. Cleans up the agent process on
    exit; leaves the backend running.

.PARAMETER BackendUrl
    Default http://localhost:5276 (matches launchSettings).

.PARAMETER StatusPort
    Loopback port for the agent's status endpoint. Default 5296 (one
    above verify-session-start's default so the two can run back-to-back
    without socket contention).

.PARAMETER TeacherOid
    Seeded Dev Teacher OID. Default 11111111-1111-1111-1111-111111111111.

.PARAMETER OutsiderOid
    Seeded Dev Outsider OID — student NOT enrolled in any class. Default
    33333333-3333-3333-3333-333333333333.

.PARAMETER ClassName
    Class to start the session for. Default "3A".

.PARAMETER SkipBuild
    Skip the dotnet builds.
#>

[CmdletBinding()]
param(
    [string]$BackendUrl = 'http://localhost:5276',
    [int]$StatusPort = 5296,
    [string]$TeacherOid = '11111111-1111-1111-1111-111111111111',
    [string]$OutsiderOid = '33333333-3333-3333-3333-333333333333',
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
    if (-not $SkipBuild) {
        Write-Step 'Building backend...'
        & dotnet build (Join-Path $repoRoot 'backend\Anchor.sln') --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

        Write-Step 'Building agent (x64)...'
        & dotnet build (Join-Path $repoRoot 'agent\FocusAgent.sln') -p:Platform=x64 --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
    }

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
        throw "Backend not reachable at $BackendUrl. Start it: ``dotnet run --project backend/src/Anchor.Api --no-launch-profile --urls $BackendUrl``"
    }

    # The agent reads Dev:ImpersonateOid from appsettings.Development.json.
    # Override it to the outsider for this verify run, restore on cleanup.
    $devSettingsPath = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\appsettings.Development.json'
    $originalOid = $null
    if (Test-Path $devSettingsPath) {
        $existing = Get-Content $devSettingsPath -Raw | ConvertFrom-Json
        $originalOid = $existing.Dev.ImpersonateOid
        if ($originalOid -ne $OutsiderOid) {
            Write-Step "Rewriting agent Dev:ImpersonateOid -> $OutsiderOid (was '$originalOid')"
            if (-not $existing.PSObject.Properties['Dev']) { $existing | Add-Member -NotePropertyName Dev -NotePropertyValue ([pscustomobject]@{}) }
            $existing.Dev.ImpersonateOid = $OutsiderOid
            ($existing | ConvertTo-Json -Depth 8) | Set-Content -Path $devSettingsPath -Encoding utf8
        }
    } else {
        throw "Agent appsettings.Development.json not deployed at $devSettingsPath."
    }

    Write-Step "Launching agent impersonating outsider ($OutsiderOid), status on port $StatusPort..."
    $agentExe = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'
    if (-not (Test-Path $agentExe)) { throw "Agent exe not found at $agentExe." }

    $agentProcess = Start-Process -FilePath $agentExe `
        -ArgumentList @('--inject-token', '--status-endpoint', $StatusPort) `
        -PassThru
    Write-Step "Agent pid=$($agentProcess.Id)"

    Write-Step 'Waiting for agent to reach Connected...'
    $statusUrl = "http://127.0.0.1:$StatusPort/status"
    $deadline = (Get-Date).AddSeconds(20)
    $status = $null
    do {
        Start-Sleep -Milliseconds 400
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { $status = $null }
        $kind = if ($status) { $status.connectionStatus } else { '<unreachable>' }
    } while ($kind -ne 'Connected' -and (Get-Date) -lt $deadline)

    if ($kind -ne 'Connected') {
        $lastErr = if ($status) { $status.lastError } else { '<none>' }
        throw "Agent did not reach Connected within 20s (last: $kind, error: $lastErr). Did SeedAsync run with the outsider entry?"
    }
    Write-Pass "Agent connected as '$($status.displayName)'"

    # ---- start a session as Dev Teacher -----------------------------------
    Write-Step "Looking up class '$ClassName'..."
    $classes = Invoke-RestMethod -Uri "$BackendUrl/classes" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } -TimeoutSec 5
    $targetClass = $classes | Where-Object { $_.name -eq $ClassName } | Select-Object -First 1
    if ($null -eq $targetClass) { throw "Class '$ClassName' not found." }

    Write-Step 'Starting session via POST /sessions (teacher)...'
    $body = @{ classId = $targetClass.id; bundleIds = @() } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' } `
        -Body $body -TimeoutSec 5
    Write-Pass "Session $($session.id) created, joinCode=$($session.joinCode)"

    # Sanity: the outsider should NOT have received the roster-based push,
    # so activeSessionId should still be null right now.
    $statusBefore = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2
    if ($statusBefore.activeSessionId -eq $session.id) {
        throw 'Outsider received the roster-based SessionStart push. They should not be enrolled — check the seed.'
    }

    # ---- join-by-code as the outsider -------------------------------------
    Write-Step 'POSTing /sessions/join-by-code as outsider...'
    $joinBody = @{ code = $session.joinCode } | ConvertTo-Json
    $joinResp = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/join-by-code" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $OutsiderOid; 'Content-Type' = 'application/json' } `
        -Body $joinBody -TimeoutSec 5
    if ($joinResp.sessionId -ne $session.id) {
        throw "join-by-code returned sessionId=$($joinResp.sessionId), expected $($session.id)"
    }
    Write-Pass 'join-by-code returned 200 with the expected sessionId.'

    Write-Step 'Polling agent /status for activeSessionId...'
    $deadline = (Get-Date).AddSeconds(5)
    $seen = $false
    do {
        Start-Sleep -Milliseconds 200
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { }
        if ($status -and $status.activeSessionId -eq $session.id) { $seen = $true; break }
    } while ((Get-Date) -lt $deadline)
    if (-not $seen) {
        $lastActive = if ($status) { $status.activeSessionId } else { '<none>' }
        throw "Agent did not see SessionStarted after manual join within 5s. activeSessionId=$lastActive"
    }
    Write-Pass 'Agent picked up SessionStarted via the manual join path.'

    # ---- error paths ------------------------------------------------------
    Write-Step '404 path: POSTing an unknown code...'
    try {
        Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/join-by-code" `
            -Headers @{ 'X-Dev-Impersonate-Oid' = $OutsiderOid; 'Content-Type' = 'application/json' } `
            -Body (@{ code = '000000' } | ConvertTo-Json) -TimeoutSec 5 | Out-Null
        throw 'Unknown code unexpectedly returned 2xx.'
    } catch {
        $sc = $_.Exception.Response.StatusCode.value__
        if ($sc -ne 404) { throw "Unknown code returned $sc, expected 404." }
        Write-Pass '404 returned for unknown code.'
    }

    Write-Step '429 path: spamming wrong codes until rate-limited...'
    # Use a fresh OID (the outsider was just reset by the successful join, so
    # we burn its budget anew). The limiter counts failures only.
    for ($i = 0; $i -lt 5; $i++) {
        try {
            Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/join-by-code" `
                -Headers @{ 'X-Dev-Impersonate-Oid' = $OutsiderOid; 'Content-Type' = 'application/json' } `
                -Body (@{ code = '999999' } | ConvertTo-Json) -TimeoutSec 5 | Out-Null
        } catch {
            # 404 expected — swallowing.
        }
    }
    try {
        Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/join-by-code" `
            -Headers @{ 'X-Dev-Impersonate-Oid' = $OutsiderOid; 'Content-Type' = 'application/json' } `
            -Body (@{ code = '999999' } | ConvertTo-Json) -TimeoutSec 5 | Out-Null
        throw 'Burst attempt unexpectedly returned 2xx.'
    } catch {
        $sc = $_.Exception.Response.StatusCode.value__
        if ($sc -ne 429) { throw "Burst attempt returned $sc, expected 429." }
        Write-Pass '429 returned after burst.'
    }

    Write-Host ''
    Write-Host '=================================' -ForegroundColor Green
    Write-Host '  JOIN-BY-CODE VERIFY: PASS' -ForegroundColor Green
    Write-Host '=================================' -ForegroundColor Green
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
finally {
    if ($agentProcess) {
        try { Stop-Process -Id $agentProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Restore the agent's original impersonation OID so subsequent dev runs
    # aren't surprised by it staying flipped to the outsider.
    if ($null -ne $originalOid -and $originalOid -ne $OutsiderOid -and (Test-Path $devSettingsPath)) {
        try {
            $cfg = Get-Content $devSettingsPath -Raw | ConvertFrom-Json
            $cfg.Dev.ImpersonateOid = $originalOid
            ($cfg | ConvertTo-Json -Depth 8) | Set-Content -Path $devSettingsPath -Encoding utf8
        } catch { }
    }
}
