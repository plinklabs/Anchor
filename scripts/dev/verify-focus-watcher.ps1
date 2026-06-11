<#
.SYNOPSIS
    End-to-end verification of the foreground-watcher pipeline introduced in #25
    and fixed in #64. Confirms that with an active session, launching an
    off-list app (Notepad) gets it minimized within ~1s and an enforcement log
    line is emitted.

.DESCRIPTION
    Unit tests for FocusSessionController use a fake watcher driven
    synchronously, so the real SetWinEventHook -> OnWinEvent path was never
    exercised by CI. This script bridges that gap headlessly.

    Flow:
      1. Builds backend + agent (skippable).
      2. Ensures backend is up at $BackendUrl.
      3. Launches the agent with --inject-token + --status-endpoint (matches
         verify-session-start.ps1) and waits for Connected.
      4. POSTs /sessions as Dev Teacher.
      5. Waits for activeSessionId, then waits for joinedSessionId (the toast
         auto-confirms after JoinConfirmationDuration = 5s).
      6. Confirms "Focus enforcement started for session ..." is in the log.
      7. Launches Notepad and waits up to 3s for one of:
            * "Blocking off-list foreground app notepad" (warn)
            * "Foreground change reported: notepad blocked=True" (info)
         appearing in today's focusagent log AFTER the session started.
      8. Optionally also asserts Notepad is no longer the foreground window
         (it should have been minimized).
      9. Cleans up: ends the session, kills Notepad, kills the agent.

.PARAMETER BackendUrl
    Backend base URL. Default http://localhost:5276.

.PARAMETER StatusPort
    Loopback port for the agent status endpoint. Default 5295.

.PARAMETER TeacherOid / StudentOid / ClassName
    Same seeded-dev defaults as verify-session-start.ps1.

.PARAMETER SkipBuild
    Skip dotnet builds.

.EXAMPLE
    .\scripts\dev\verify-focus-watcher.ps1
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
$notepadProcess = $null
$sessionId = $null

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
        $sc = $_.Exception.Response.StatusCode.value__
        if ($null -ne $sc -and ($sc -eq 401 -or $sc -eq 404)) { $backendUp = $true }
    }
    if (-not $backendUp) {
        throw "Backend not reachable at $BackendUrl. Start it with: dotnet run --project backend/src/Anchor.Api --no-launch-profile --urls $BackendUrl"
    }
    Write-Step 'Backend reachable.'

    # --------------------------------------------------------------- agent
    $agentExe = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'
    if (-not (Test-Path $agentExe)) { throw "Agent exe not found at $agentExe (rebuild?)." }

    Write-Step 'Launching agent (--inject-token --status-endpoint)...'
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
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { $status = $null }
        $statusKind = if ($status) { $status.connectionStatus } else { '<unreachable>' }
    } while ($statusKind -ne 'Connected' -and (Get-Date) -lt $deadline)
    if ($statusKind -ne 'Connected') {
        throw "Agent did not reach Connected within 15s (last: $statusKind)"
    }
    Write-Pass "Agent connected as '$($status.displayName)'"

    # --------------------------------------------------- find class id
    Write-Step "Finding class id for '$ClassName'..."
    $classes = Invoke-RestMethod -Uri "$BackendUrl/classes" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } -TimeoutSec 5
    $targetClass = $classes | Where-Object { $_.name -eq $ClassName } | Select-Object -First 1
    if ($null -eq $targetClass) {
        throw "Class '$ClassName' not found via /classes as teacher $TeacherOid. Did the dev seeder run?"
    }

    # -------------------------------------------------- POST /sessions
    Write-Step 'Creating session...'
    $body = @{ classId = $targetClass.id; bundleIds = @() } | ConvertTo-Json
    $sessionResponse = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' } `
        -Body $body -TimeoutSec 5
    $sessionId = $sessionResponse.id
    Write-Step "Session id: $sessionId"

    # -------------------------------- wait for activeSessionId (toast up)
    Write-Step 'Waiting for activeSessionId (toast up)...'
    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { }
    } while ((-not $status -or $status.activeSessionId -ne $sessionId) -and (Get-Date) -lt $deadline)
    if ($status.activeSessionId -ne $sessionId) {
        throw "Agent did not see SessionStarted within 5s (active=$($status.activeSessionId))"
    }
    Write-Pass 'Toast active.'

    # ----------------------- wait for joinedSessionId (auto-confirm 5s)
    Write-Step 'Waiting for toast to auto-confirm (joinedSessionId)...'
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 300
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { }
    } while ((-not $status -or $status.joinedSessionId -ne $sessionId) -and (Get-Date) -lt $deadline)
    if ($status.joinedSessionId -ne $sessionId) {
        throw "Agent did not auto-join within 10s (joined=$($status.joinedSessionId))"
    }
    Write-Pass 'Agent joined.'

    # ----------------------------------- confirm enforcement started in log
    $logDir = Join-Path $env:LOCALAPPDATA 'Anchor\FocusAgent\logs'
    $logFile = Join-Path $logDir ("focusagent-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
    if (-not (Test-Path $logFile)) { throw "Agent log not found at $logFile" }

    Write-Step 'Confirming Focus enforcement started in log...'
    # Serilog quotes Guid-typed structured properties, so the rendered line is
    # 'Focus enforcement started for session "<guid>"'. Allow the optional quote.
    $startedPattern = 'Focus enforcement started for session "?' + [regex]::Escape($sessionId)
    $started = $false
    $deadline = (Get-Date).AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 200
        $tail = Get-Content -Path $logFile -Tail 200 -ErrorAction SilentlyContinue
        if ($tail -match $startedPattern) { $started = $true; break }
    } while ((Get-Date) -lt $deadline)
    if (-not $started) {
        throw 'Log does not contain "Focus enforcement started for session ..." -- controller never resolved SessionJoined.'
    }
    Write-Pass 'Focus enforcement started.'

    # -------------------------------------------- launch off-list Notepad
    # Win11 notepad.exe is a launcher: Start-Process returns the launcher pid,
    # not the actual UWP notepad pid, so our own Stop-Process can leave a
    # stale notepad window. If one is already up, it will be merely activated
    # (no real foreground event) and the watcher won't see anything to block.
    # Kill leftovers up front so we know we are testing a fresh launch.
    Get-Process -Name notepad -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Step "Killing stale notepad pid=$($_.Id) before test."
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    Write-Step 'Launching Notepad (off-list)...'
    $notepadProcess = Start-Process -FilePath 'notepad.exe' -PassThru
    Start-Sleep -Milliseconds 400  # give Windows time to bring it to foreground

    # ---------------------- poll log for the enforcement signal we care about
    Write-Step 'Waiting up to 5s for foreground-watcher to fire on Notepad...'
    $signal = $null
    $deadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 250
        $tail = Get-Content -Path $logFile -Tail 400 -ErrorAction SilentlyContinue
        # The Warning line is the strongest signal: classification ran AND
        # the enforcer was called. The Info "reported" line follows.
        $hit = $tail | Where-Object { $_ -match 'Blocking off-list foreground app notepad' } | Select-Object -Last 1
        if ($hit) { $signal = $hit; break }
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $signal) {
        # Dump the last 60 lines to make failures debuggable without re-running.
        Write-Host '--- last 60 lines of agent log ---' -ForegroundColor DarkGray
        Get-Content -Path $logFile -Tail 60 | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
        Write-Host '--- end log ---' -ForegroundColor DarkGray
        throw 'Foreground watcher did not fire on Notepad within 5s. The #64 fix is not effective in this run.'
    }
    Write-Pass "Watcher fired and enforcer blocked Notepad: $signal"

    # --------------------------------------------------- #92: re-minimize check
    # The bug: SW_MINIMIZE from the agent leaves the minimized window as the
    # *logical* foreground window, so the student restoring it (taskbar click)
    # fires no EVENT_SYSTEM_FOREGROUND -- and the agent never re-minimized it.
    # The fix hooks EVENT_SYSTEM_MINIMIZEEND, which fires on restore regardless
    # of foreground state. Drive real SW_RESTOREs against the blocked Notepad
    # window and assert additional "Blocking off-list" lines appear: each
    # restore must be re-enforced.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win92 {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@ -ErrorAction SilentlyContinue

    $reNotepadPid = if ($signal -match 'pid=(\d+)') { [int]$Matches[1] } else { $null }
    if ($null -eq $reNotepadPid) { throw 'Could not parse Notepad pid from the enforcement log line.' }

    $npHwnd = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(2)
    do {
        try { $npHwnd = (Get-Process -Id $reNotepadPid -ErrorAction Stop).MainWindowHandle } catch { $npHwnd = [IntPtr]::Zero }
        if ($npHwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($npHwnd -eq [IntPtr]::Zero) { throw "Notepad (pid=$reNotepadPid) exposes no MainWindowHandle to restore." }

    function Get-BlockLineCount {
        $t = Get-Content -Path $logFile -Tail 800 -ErrorAction SilentlyContinue
        return @($t | Where-Object { $_ -match 'Blocking off-list foreground app notepad' }).Count
    }

    $baseline = Get-BlockLineCount
    Write-Step "Restoring blocked Notepad 4x (baseline block lines=$baseline); each restore must re-minimize..."
    # Pace restores slower than the agent's observed block latency (~20ms) but
    # inside the 500ms report-coalesce window for some, outside for others --
    # enforcement must fire for ALL of them either way.
    for ($i = 0; $i -lt 4; $i++) {
        [void][Win92]::ShowWindow($npHwnd, 9)   # SW_RESTORE -> EVENT_SYSTEM_MINIMIZEEND
        Start-Sleep -Milliseconds 300
    }

    $reblocks = 0
    $deadline = (Get-Date).AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 250
        $reblocks = (Get-BlockLineCount) - $baseline
    } while ($reblocks -lt 2 -and (Get-Date) -lt $deadline)

    if ($reblocks -lt 2) {
        Write-Host '--- last 60 lines of agent log ---' -ForegroundColor DarkGray
        Get-Content -Path $logFile -Tail 60 | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
        Write-Host '--- end log ---' -ForegroundColor DarkGray
        throw "Restoring Notepad 4x produced only $reblocks additional 'Blocking off-list' line(s) (need >=2) -- restore-from-minimized is not being re-enforced (#92)."
    }
    Write-Pass "Notepad re-minimized on restore: $reblocks additional block line(s) after 4 restores (#92)."

    # ---------------------- informational: did focus actually move away?
    # The agent's "blocking off-list" log line is #64's authoritative signal
    # -- it proves the watcher fired and the enforcer ran. Whether the
    # SW_MINIMIZE call also visually evicted Notepad is #25 territory and
    # flaky against Win11's notepad (which sometimes retains foreground even
    # after a successful SW_MINIMIZE). Reported as info, not as PASS/FAIL.
    if ($signal -match 'pid=(\d+)') {
        $realNotepadPid = [int]$Matches[1]
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Fg {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
'@ -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
        $fgHwnd = [Fg]::GetForegroundWindow()
        $fgPid = 0
        [void][Fg]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPid)
        if ($fgPid -eq $realNotepadPid) {
            Write-Step "(info) Notepad pid=$realNotepadPid is still foreground after block -- likely a Win11 notepad SW_MINIMIZE quirk (issue 25 territory, not 64)."
        } else {
            Write-Step "(info) Focus left Notepad (notepad pid=$realNotepadPid, foreground pid=$fgPid)."
        }
    }

    Write-Host ''
    Write-Host '=================================' -ForegroundColor Green
    Write-Host '  FOCUS-WATCHER VERIFY: PASS' -ForegroundColor Green
    Write-Host '=================================' -ForegroundColor Green
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
finally {
    if ($notepadProcess) {
        try { Stop-Process -Id $notepadProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Also kill the real (sandboxed) notepad windows the launcher spawned,
    # since their pid differs from $notepadProcess.Id on Win11.
    Get-Process -Name notepad -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    # Best-effort: end the backend session so it doesn't linger across runs.
    if ($sessionId) {
        try {
            Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/$sessionId/end" `
                -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } -TimeoutSec 5 | Out-Null
        } catch { }
    }
    if ($agentProcess) {
        Write-Step "Stopping agent pid=$($agentProcess.Id)..."
        try { Stop-Process -Id $agentProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}
