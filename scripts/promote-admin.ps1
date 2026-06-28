#Requires -Version 5.1
<#
.SYNOPSIS
    Promote an existing Anchor user to the DB-only `Admin` role in a deployed
    (Azure SQL) environment — the manual first-admin bootstrap for production.

.DESCRIPTION
    `Admin` is NOT an Entra app role (Entra mints only `Teacher` / `Student`);
    it is a DB-only designation resolved per-request by AdminRoleAuthorizationHandler
    (#75). The first-admin auto-bootstrap in MeController is deliberately gated on
    the Development environment — a misconfigured production deployment must never
    be able to mint admins by accident (see docs/SETUP.md). So on a real Azure
    deployment the first teacher signs in as `Teacher` and stays there, with no
    one able to create bundles (POST /bundles requires Admin).

    This script is the sanctioned manual escape hatch: it sets one named user's
    row in the `Users` table to `Role = 'Admin'`. That user can thereafter
    promote further admins through the admin-only `POST /me/promote` endpoint, so
    this only ever needs running once per environment.

    Steps:
      1. Resolve the target user's Entra object id (from -EntraOid, or by looking
         up -UserPrincipalName via `az ad user show`).
      2. Discover the SQL logical server FQDN + database in the resource group
         (or use -SqlServerFqdn / -SqlDatabaseName overrides).
      3. Unless -SkipFirewallRule, add a temporary SQL firewall rule for this
         machine's public IP so the local connection isn't blocked, and remove it
         again on exit (the rule is always cleaned up, even on failure).
      4. Connect with the SQL admin login/password and, if the user exists and
         isn't already an admin, UPDATE their role to Admin.

    PRECONDITION: the target user must have signed in to Anchor at least once, so
    that GET /me has created their `Users` row. If no row exists the script stops
    and tells you to have them sign in first (it deliberately does NOT insert a
    user from thin air — the EntraOid→identity binding is established by sign-in).

    DRY RUN: run with -WhatIf to print the firewall and UPDATE actions without
    executing them. Read-only discovery (Entra lookup, server discovery, the
    current-role SELECT) still runs so the plan is grounded in the real state.

.PARAMETER UserPrincipalName
    The target user's UPN / email (e.g. teacher@school.edu). Resolved to an Entra
    object id via `az ad user show`. Provide this OR -EntraOid.

.PARAMETER EntraOid
    The target user's Entra object id (GUID), if you already know it. Skips the
    `az ad user show` lookup. Provide this OR -UserPrincipalName.

.PARAMETER ResourceGroup
    Resource group the SQL server lives in. Default: anchor-rg (matches setup.ps1).

.PARAMETER SqlServerFqdn
    Fully-qualified SQL server name (e.g. anchor-sql-xyz.database.windows.net).
    When omitted, discovered from the resource group via `az sql server list`.

.PARAMETER SqlDatabaseName
    SQL database name. Default: anchordb (matches infra/main.bicep).

.PARAMETER SqlAdminLogin
    SQL admin login. Default: anchoradmin (matches infra/main.bicep).

.PARAMETER SqlAdminPassword
    SQL admin password (secure string). Prompted for if not supplied. This is the
    same `sqlAdminPassword` you passed to setup.ps1 / the Bicep deploy.

.PARAMETER SkipFirewallRule
    Don't add/remove the temporary firewall rule (use when your IP already has
    access to the SQL server, e.g. you're running from inside the VNet).

.EXAMPLE
    ./scripts/promote-admin.ps1 -UserPrincipalName teacher@school.edu

.EXAMPLE
    ./scripts/promote-admin.ps1 -EntraOid 11111111-1111-1111-1111-111111111111 `
        -ResourceGroup anchor-rg -SqlServerFqdn anchor-sql-xyz.database.windows.net

.EXAMPLE
    ./scripts/promote-admin.ps1 -UserPrincipalName teacher@school.edu -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'ByUpn')]
param(
    [Parameter(Mandatory, ParameterSetName = 'ByUpn')]
    [string]$UserPrincipalName,

    [Parameter(Mandatory, ParameterSetName = 'ByOid')]
    [guid]$EntraOid,

    [string]$ResourceGroup = 'anchor-rg',
    [string]$SqlServerFqdn,
    [string]$SqlDatabaseName = 'anchordb',
    [string]$SqlAdminLogin = 'anchoradmin',
    [securestring]$SqlAdminPassword,
    [switch]$SkipFirewallRule
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Small helpers ────────────────────────────────────────────────────────────

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Run an `az` command, throwing on non-zero exit. Returns captured stdout.
function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$ArgList, [string]$Action = 'az command')
    $out = & az @ArgList
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to $Action (az exited $LASTEXITCODE). Args: $($ArgList -join ' ')"
    }
    return $out
}

function ConvertTo-PlainText {
    param([securestring]$Secure)
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ── 1. Resolve the target Entra object id ────────────────────────────────────

Write-Step 'Resolve target user'

if ($PSCmdlet.ParameterSetName -eq 'ByOid') {
    $targetOid = $EntraOid.ToString()
    Write-Host "    EntraOid : $targetOid (supplied)"
}
else {
    Write-Host "    Looking up $UserPrincipalName in Entra..."
    $targetOid = (Invoke-Az -ArgList @('ad', 'user', 'show', '--id', $UserPrincipalName, '--query', 'id', '-o', 'tsv') `
            -Action "look up $UserPrincipalName").Trim()
    if (-not $targetOid) {
        throw "Could not resolve an Entra object id for '$UserPrincipalName'."
    }
    Write-Host "    EntraOid : $targetOid"
}

# ── 2. Discover the SQL server + database ─────────────────────────────────────

Write-Step 'Resolve SQL server'

if (-not $SqlServerFqdn) {
    $serversJson = Invoke-Az -ArgList @('sql', 'server', 'list', '-g', $ResourceGroup,
        '--query', '[].{name:name,fqdn:fullyQualifiedDomainName}', '-o', 'json') `
        -Action "list SQL servers in $ResourceGroup"
    $servers = @($serversJson | ConvertFrom-Json)
    if ($servers.Count -eq 0) {
        throw "No SQL server found in resource group '$ResourceGroup'. Pass -SqlServerFqdn explicitly."
    }
    # Prefer the Anchor-named server, but fall back to the sole server if the
    # naming was overridden at deploy time.
    $picked = ($servers | Where-Object { $_.name -like 'anchor-sql*' } | Select-Object -First 1)
    if (-not $picked) { $picked = $servers[0] }
    if ($servers.Count -gt 1) {
        Write-Host "    Multiple SQL servers found; using '$($picked.name)'. Override with -SqlServerFqdn if wrong."
    }
    $SqlServerFqdn = $picked.fqdn
    $sqlServerName = $picked.name
}
else {
    # The short name is the first DNS label, needed for the firewall-rule az call.
    $sqlServerName = $SqlServerFqdn.Split('.')[0]
}

Write-Host "    server   : $SqlServerFqdn"
Write-Host "    database : $SqlDatabaseName"
Write-Host "    login    : $SqlAdminLogin"

if (-not $SqlAdminPassword) {
    $SqlAdminPassword = Read-Host -AsSecureString "SQL admin password for '$SqlAdminLogin'"
}

# ── 3. Temporary firewall rule for this machine ───────────────────────────────

$firewallRuleName = "anchor-promote-admin-$(Get-Date -Format 'yyyyMMddHHmmss')"
$firewallRuleAdded = $false

try {
    if (-not $SkipFirewallRule) {
        Write-Step 'Open temporary SQL firewall rule'
        $myIp = $null
        try { $myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15).Trim() } catch { }
        if (-not $myIp) {
            throw "Could not determine this machine's public IP (needed for the firewall rule). " +
                  "Add a rule manually and re-run with -SkipFirewallRule."
        }
        Write-Host "    public IP : $myIp"
        if ($PSCmdlet.ShouldProcess("$sqlServerName / $myIp", "add SQL firewall rule '$firewallRuleName'")) {
            Invoke-Az -ArgList @('sql', 'server', 'firewall-rule', 'create',
                '-g', $ResourceGroup, '-s', $sqlServerName, '-n', $firewallRuleName,
                '--start-ip-address', $myIp, '--end-ip-address', $myIp, '-o', 'none') `
                -Action 'create temporary firewall rule' | Out-Null
            $firewallRuleAdded = $true
            Write-Host "    added rule '$firewallRuleName' (removed automatically on exit)"
        }
    }

    # ── 4. Promote in the database ────────────────────────────────────────────

    Write-Step 'Promote user in the database'

    $pw = ConvertTo-PlainText $SqlAdminPassword
    $connString = "Server=tcp:$SqlServerFqdn,1433;Database=$SqlDatabaseName;" +
        "User ID=$SqlAdminLogin;Password=$pw;Encrypt=true;TrustServerCertificate=false;Connection Timeout=30;"

    $conn = New-Object System.Data.SqlClient.SqlConnection $connString
    try {
        $conn.Open()

        # Read the current row first so we can report what we found and refuse to
        # invent a user that hasn't signed in yet.
        $select = $conn.CreateCommand()
        $select.CommandText = 'SELECT DisplayName, Role FROM Users WHERE EntraOid = @oid'
        [void]$select.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@oid', [System.Data.SqlDbType]::UniqueIdentifier)))
        $select.Parameters['@oid'].Value = [guid]$targetOid

        $reader = $select.ExecuteReader()
        $found = $false; $displayName = $null; $currentRole = $null
        if ($reader.Read()) {
            $found = $true
            $displayName = $reader['DisplayName']
            $currentRole = $reader['Role']
        }
        $reader.Close()

        if (-not $found) {
            throw "No Users row for EntraOid $targetOid. The user must sign in to Anchor at " +
                  "least once (so GET /me creates their row) before they can be promoted."
        }

        Write-Host "    user     : $displayName"
        Write-Host "    current  : $currentRole"

        if ($currentRole -eq 'Admin') {
            Write-Host "    Already an Admin — nothing to do." -ForegroundColor Green
            return
        }

        if ($PSCmdlet.ShouldProcess("$displayName ($targetOid)", "set Role = 'Admin'")) {
            $update = $conn.CreateCommand()
            $update.CommandText = "UPDATE Users SET Role = 'Admin' WHERE EntraOid = @oid"
            [void]$update.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@oid', [System.Data.SqlDbType]::UniqueIdentifier)))
            $update.Parameters['@oid'].Value = [guid]$targetOid
            $rows = $update.ExecuteNonQuery()
            if ($rows -ne 1) {
                throw "Expected to update exactly 1 row but updated $rows. No change committed is not guaranteed — investigate."
            }
            Write-Host "    Promoted $displayName to Admin." -ForegroundColor Green
        }
    }
    finally {
        $conn.Dispose()
        if ($pw) { $pw = $null }
    }
}
finally {
    if ($firewallRuleAdded) {
        Write-Step 'Remove temporary SQL firewall rule'
        try {
            & az sql server firewall-rule delete -g $ResourceGroup -s $sqlServerName -n $firewallRuleName -o none
            if ($LASTEXITCODE -eq 0) {
                Write-Host "    removed rule '$firewallRuleName'"
            }
            else {
                Write-Warning "Could not remove firewall rule '$firewallRuleName' (az exited $LASTEXITCODE). Remove it by hand."
            }
        }
        catch {
            Write-Warning "Could not remove firewall rule '$firewallRuleName': $($_.Exception.Message). Remove it by hand."
        }
    }
}
