<#
.SYNOPSIS
    One-command dev loop for the FocusAgent (#131) — the agent-side analog of
    `npm run dev:extension`.

.DESCRIPTION
    Boots a seeded backend if one isn't already running, launches the real agent
    headless (--inject-token bypasses WAM, --status-endpoint exposes JSON state
    on loopback) impersonating the seeded Dev Student, and drops you into a tiny
    REST console to start / amend / end a session and inspect the agent's live
    /status. No dashboard, no WAM picker, no Visual Studio.

    Mirrors extension/e2e/dev-launcher.ts. Uses the project default dev port
    (5276) so it talks to the same backend the dashboard and a normal agent run
    would — reusing one already running if there is one.

      ┌─ menu ─────────────────────────────────────────────────────────┐
      │  s  start a session (Notepad (dev) bundle → notepad allowed)    │
      │  a  amend → drop all bundles (notepad goes off-list)            │
      │  m  amend → restore the Notepad (dev) bundle                    │
      │  e  end the current session                                     │
      │  t  print the agent's /status snapshot                          │
      │  q  quit (stops the agent, and the backend if we started it)    │
      └─────────────────────────────────────────────────────────────────┘

    With a session running and the Notepad bundle attached, open Notepad: the
    agent's /status allowedApps includes "notepad". Press `a` and it drops out.

.PARAMETER BackendUrl
    Backend base URL. Default http://localhost:5276.

.PARAMETER StatusPort
    Loopback port for the agent's status endpoint. Default 5295.

.PARAMETER AutoJoin
    Launch the agent with --auto-join (skips the confirmation toast and joins
    immediately). Off by default so you can watch the real toast.

.PARAMETER SkipBuild
    Skip the dotnet builds and trust existing artifacts.

.EXAMPLE
    .\scripts\dev\dev-agent.ps1
    # Builds, boots the backend if needed, launches the agent, opens the console.
#>

[CmdletBinding()]
param(
    [string]$BackendUrl = 'http://localhost:5276',
    [int]$StatusPort = 5295,
    [string]$TeacherOid = '11111111-1111-1111-1111-111111111111',
    [string]$StudentOid = '22222222-2222-2222-2222-222222222222',
    [string]$ClassName = '3A',
    [string]$AppBundleName = 'Notepad (dev)',
    [switch]$AutoJoin,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function Write-Dev($msg) { Write-Host "[dev] $msg" -ForegroundColor Cyan }

$teacherHeaders = @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' }
$statusUrl = "http://127.0.0.1:$StatusPort/status"

$backendProcess = $null
$agentProcess = $null

function Test-BackendUp {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $BackendUrl -TimeoutSec 2 -ErrorAction Stop | Out-Null
        return $true
    } catch {
        # A running backend answering 401/404 still counts as reachable.
        $sc = $_.Exception.Response.StatusCode.value__
        return ($null -ne $sc -and ($sc -eq 401 -or $sc -eq 404))
    }
}

try {
    # ---------------------------------------------------------------- build
    if (-not $SkipBuild) {
        Write-Dev 'Building backend...'
        & dotnet build (Join-Path $repoRoot 'backend\Anchor.sln') --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }

        Write-Dev 'Building agent (x64)...'
        & dotnet build (Join-Path $repoRoot 'agent\src\FocusAgent.App\FocusAgent.App.csproj') -p:Platform=x64 --nologo -v:q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Agent build failed.' }
    }

    # -------------------------------------------------------------- backend
    if (Test-BackendUp) {
        Write-Dev "Reusing backend already running at $BackendUrl."
    } else {
        Write-Dev "Starting backend at $BackendUrl (Development, seeded)..."
        # Set before Start-Process so the spawned backend inherits it.
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $backendProcess = Start-Process -FilePath 'dotnet' `
            -ArgumentList @('run', '--project', (Join-Path $repoRoot 'backend\src\Anchor.Api'),
                '--no-launch-profile', '--urls', $BackendUrl) `
            -PassThru
        $deadline = (Get-Date).AddSeconds(180)
        while (-not (Test-BackendUp) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 1000 }
        if (-not (Test-BackendUp)) { throw 'Backend did not become reachable within 180s.' }
        Write-Dev 'Backend reachable.'
    }

    # resolve class + bundle ids up front so the menu actions are snappy
    $classes = Invoke-RestMethod -Uri "$BackendUrl/classes" -Headers $teacherHeaders -TimeoutSec 5
    $class = $classes | Where-Object { $_.name -eq $ClassName } | Select-Object -First 1
    if ($null -eq $class) { throw "Class '$ClassName' not found. Did the dev seeder run?" }
    $bundles = Invoke-RestMethod -Uri "$BackendUrl/bundles" -Headers $teacherHeaders -TimeoutSec 5
    $bundle = $bundles | Where-Object { $_.name -eq $AppBundleName } | Select-Object -First 1
    if ($null -eq $bundle) { throw "Bundle '$AppBundleName' not found." }
    Write-Dev "Resolved class '$ClassName' and bundle '$AppBundleName'."

    # --------------------------------------------------------------- agent
    $agentExe = Join-Path $repoRoot 'agent\src\FocusAgent.App\bin\x64\Debug\net10.0-windows10.0.19041.0\FocusAgent.App.exe'
    if (-not (Test-Path $agentExe)) { throw "Agent exe not found at $agentExe (build first, or drop -SkipBuild)." }

    # The agent reads Dev:ImpersonateOid from config; --inject-token also layers
    # environment variables, so we can pick the student without rewriting a file.
    $env:Dev__ImpersonateOid = $StudentOid
    $env:Backend__BaseUrl = $BackendUrl
    $agentArgs = @('--inject-token', '--status-endpoint', $StatusPort)
    if ($AutoJoin) { $agentArgs += '--auto-join' }
    Write-Dev "Launching agent (impersonating Dev Student, status on $StatusPort)..."
    $agentProcess = Start-Process -FilePath $agentExe -ArgumentList $agentArgs -PassThru

    Write-Dev 'Waiting for agent to reach Connected...'
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 400
        try { $status = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2 } catch { $status = $null }
    } while (($null -eq $status -or $status.connectionStatus -ne 'Connected') -and (Get-Date) -lt $deadline)
    if ($null -eq $status -or $status.connectionStatus -ne 'Connected') {
        throw 'Agent did not reach Connected within 20s.'
    }
    Write-Dev "Agent connected as '$($status.displayName)'."

    $sessionId = $null

    function Show-Menu {
        Write-Host ''
        Write-Host '  ┌─ Anchor agent dev console ──────────────────────────────┐' -ForegroundColor DarkCyan
        Write-Host '  │  s  start a session (Notepad (dev) bundle)              │' -ForegroundColor DarkCyan
        Write-Host '  │  a  amend → drop all bundles (notepad off-list)         │' -ForegroundColor DarkCyan
        Write-Host '  │  m  amend → restore the Notepad (dev) bundle            │' -ForegroundColor DarkCyan
        Write-Host '  │  e  end the current session                             │' -ForegroundColor DarkCyan
        Write-Host '  │  t  print the agent /status snapshot                    │' -ForegroundColor DarkCyan
        Write-Host '  │  q  quit                                                │' -ForegroundColor DarkCyan
        Write-Host '  └──────────────────────────────────────────────────────────┘' -ForegroundColor DarkCyan
    }

    Show-Menu
    while ($true) {
        $cmd = (Read-Host 'agent>').Trim().ToLowerInvariant()
        try {
            switch ($cmd) {
                's' {
                    $body = @{ classId = $class.id; bundleIds = @($bundle.id) } | ConvertTo-Json
                    $s = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions" -Headers $teacherHeaders -Body $body -TimeoutSec 5
                    $sessionId = $s.id
                    Write-Dev "Session started: $($s.id) (join code $($s.joinCode))."
                }
                'a' {
                    if (-not $sessionId) { Write-Dev 'No active session — press s first.'; break }
                    $body = @{ bundleIds = @() } | ConvertTo-Json
                    Invoke-RestMethod -Method Put -Uri "$BackendUrl/sessions/$sessionId/bundles" -Headers $teacherHeaders -Body $body -TimeoutSec 5 | Out-Null
                    Write-Dev 'Dropped all bundles — notepad should leave allowedApps.'
                }
                'm' {
                    if (-not $sessionId) { Write-Dev 'No active session — press s first.'; break }
                    $body = @{ bundleIds = @($bundle.id) } | ConvertTo-Json
                    Invoke-RestMethod -Method Put -Uri "$BackendUrl/sessions/$sessionId/bundles" -Headers $teacherHeaders -Body $body -TimeoutSec 5 | Out-Null
                    Write-Dev 'Restored the Notepad (dev) bundle.'
                }
                'e' {
                    if (-not $sessionId) { Write-Dev 'No active session — press s first.'; break }
                    Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/$sessionId/end" -Headers $teacherHeaders -TimeoutSec 5 | Out-Null
                    Write-Dev "Session $sessionId ended."
                    $sessionId = $null
                }
                't' {
                    try {
                        $snap = Invoke-RestMethod -Uri $statusUrl -TimeoutSec 2
                        $snap | ConvertTo-Json -Depth 4 | Write-Host
                    } catch { Write-Dev 'Status endpoint unreachable.' }
                }
                'q' { return }
                default { if ($cmd) { Write-Dev "Unknown command '$cmd'." }; Show-Menu }
            }
        } catch {
            Write-Dev "Error: $($_.Exception.Message)"
        }
    }
}
finally {
    if ($agentProcess) {
        Write-Dev "Stopping agent pid=$($agentProcess.Id)..."
        try { Stop-Process -Id $agentProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($backendProcess) {
        Write-Dev 'Stopping backend we started...'
        try { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item Env:\Dev__ImpersonateOid -ErrorAction SilentlyContinue
    Remove-Item Env:\Backend__BaseUrl -ErrorAction SilentlyContinue
}
