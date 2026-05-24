<#
.SYNOPSIS
    Smoke-tests the teacher-facing unblock REST endpoints (#73) against a
    running backend, using only the dev impersonation header for auth.

.DESCRIPTION
    Mirrors verify-session-start.ps1's shape but exercises:
      1. POST /sessions as Dev Teacher (so we have a session id to act on).
      2. GET /sessions/{id}/unblock-requests as Dev Teacher (expect [] -- no
         events yet without a SignalR client; the xUnit suite covers the
         populated case end-to-end against the real DB).
      3. POST /sessions/{id}/unblock for the seeded Dev Student (expect 200
         + the grant row materialised on the response body).
      4. POST /sessions/{id}/unblock again with the same args (expect 200
         and identical GrantedAt -- the grant must be idempotent on PK).
      5. POST /sessions/{id}/end to leave the dev DB tidy.

    Doesn't touch the extension or the dashboard; the rest of the request
    flow (SignalR ReportEvent -> UnblockRequested broadcast -> AllowlistAmended
    push) is exercised by the xUnit integration suite. If this script
    PASSes the REST surface is wired correctly.

.PARAMETER BackendUrl
    Default http://localhost:5276.
.PARAMETER TeacherOid
    Default 11111111-1111-1111-1111-111111111111 (seeded Dev Teacher).
.PARAMETER StudentOid
    Default 22222222-2222-2222-2222-222222222222 (seeded Dev Student).
.PARAMETER ClassName
    Default "3A".
.PARAMETER Host
    Host to approve. Default "reddit.com".
#>

[CmdletBinding()]
param(
    [string]$BackendUrl = 'http://localhost:5276',
    [string]$TeacherOid = '11111111-1111-1111-1111-111111111111',
    [string]$StudentOid = '22222222-2222-2222-2222-222222222222',
    [string]$ClassName = '3A',
    [string]$HostName = 'reddit.com'
)

$ErrorActionPreference = 'Stop'

function Write-Step($msg) { Write-Host "[verify] $msg" -ForegroundColor Cyan }
function Write-Pass($msg) { Write-Host "[PASS]   $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "[FAIL]   $msg" -ForegroundColor Red }

try {
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

    # --------------------------------------------------- find class id
    Write-Step "Finding class id for '$ClassName' via /classes..."
    $classes = Invoke-RestMethod -Uri "$BackendUrl/classes" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } `
        -TimeoutSec 5
    $targetClass = $classes | Where-Object { $_.name -eq $ClassName } | Select-Object -First 1
    if ($null -eq $targetClass) {
        throw "Class '$ClassName' not found. Did the dev seeder run?"
    }
    Write-Step "Class id: $($targetClass.id)"

    # -------------------------------------------------- POST /sessions
    Write-Step 'Creating session as Dev Teacher...'
    $body = @{ classId = $targetClass.id; mode = 'Strict'; bundleIds = @() } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' } `
        -Body $body `
        -TimeoutSec 5
    Write-Pass "Session created: $($session.id)"

    $teacherHeaders = @{ 'X-Dev-Impersonate-Oid' = $TeacherOid; 'Content-Type' = 'application/json' }

    # ----- look up the student's internal User.Id via session detail.
    # POST /unblock expects the participant's User.Id, not the EntraOid -- the
    # two only match in fake-auth test fixtures, not in the real schema.
    Write-Step 'GET /sessions/{id} to resolve student internal Id...'
    $detail = Invoke-RestMethod -Uri "$BackendUrl/sessions/$($session.id)" `
        -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } -TimeoutSec 5
    $student = $detail.participants | Where-Object { $_.displayName -eq 'Dev Student' } | Select-Object -First 1
    if ($null -eq $student) {
        throw "No 'Dev Student' participant on the new session. Roster broken?"
    }
    $studentInternalId = $student.userId
    Write-Step "Student internal id: $studentInternalId"

    try {
        # ------------------------------------ GET unblock-requests (empty)
        Write-Step 'GET /sessions/{id}/unblock-requests (expect empty)...'
        $pending = Invoke-RestMethod -Uri "$BackendUrl/sessions/$($session.id)/unblock-requests" `
            -Headers $teacherHeaders -TimeoutSec 5
        if ($null -ne $pending -and @($pending).Count -ne 0) {
            throw "Expected empty pending list, got $(@($pending).Count) entries."
        }
        Write-Pass 'Pending list is empty as expected.'

        # ------------------------------------------- POST /unblock (first)
        Write-Step "POST /sessions/{id}/unblock for student $studentInternalId + host '$HostName'..."
        $unblockBody = @{ userId = $studentInternalId; host = $HostName } | ConvertTo-Json
        $first = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/$($session.id)/unblock" `
            -Headers $teacherHeaders -Body $unblockBody -TimeoutSec 5
        if ($first.userId -ne $studentInternalId) { throw "Response userId mismatch: got $($first.userId)" }
        if ($first.host -ne $HostName) { throw "Response host mismatch: got $($first.host)" }
        Write-Pass "Grant returned with host '$($first.host)' at $($first.grantedAt)."

        # --------------------------------------- POST /unblock (idempotent)
        Write-Step 'POSTing the same grant again (expect identical GrantedAt)...'
        $second = Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/$($session.id)/unblock" `
            -Headers $teacherHeaders -Body $unblockBody -TimeoutSec 5
        if ($second.grantedAt -ne $first.grantedAt) {
            throw "Idempotency broken: second call returned GrantedAt=$($second.grantedAt), expected $($first.grantedAt)."
        }
        Write-Pass 'Second POST is idempotent (same GrantedAt).'
    }
    finally {
        # Leave the dev DB tidy. Best-effort -- if this fails, the test PASSed.
        try {
            Invoke-RestMethod -Method Post -Uri "$BackendUrl/sessions/$($session.id)/end" `
                -Headers @{ 'X-Dev-Impersonate-Oid' = $TeacherOid } -TimeoutSec 5 | Out-Null
        } catch { }
    }

    Write-Host ''
    Write-Host '=================================' -ForegroundColor Green
    Write-Host '  UNBLOCK REST VERIFY: PASS' -ForegroundColor Green
    Write-Host '=================================' -ForegroundColor Green
    exit 0
}
catch {
    Write-Fail $_.Exception.Message
    exit 2
}
