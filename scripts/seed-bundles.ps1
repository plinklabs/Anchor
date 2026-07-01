#Requires -Version 5.1
<#
.SYNOPSIS
    Seed a curated set of example bundles into a deployed (Azure SQL) Anchor
    environment — the production companion to the dev-only bundle seed (#292).

.DESCRIPTION
    A fresh production deployment starts with **zero bundles**:
    `DevDataSeeder.EnsureDevBundlesAsync` only runs in Development, and the
    production startup path just applies EF Core migrations (empty tables, no
    seed). So after `setup.ps1` provisions an environment the bundle picker is
    empty and the first admin has to recreate every bundle by hand before a
    session can enforce anything.

    This script is the sanctioned fix: it idempotently inserts the real example
    bundles (Microsoft 365 / Smartschool / Bingel) straight into the `Bundles` /
    `BundleEntries` tables, mirroring `DevDataSeeder`'s catalogue minus the
    dev-only `Notepad (dev)` headless-verify fixture. It reuses the exact
    SQL-admin + temporary-firewall-rule connection pattern of
    `promote-admin.ps1` (#287), so the two production bootstrap helpers behave
    the same way.

    Steps:
      1. Discover the SQL logical server FQDN + database in the resource group
         (or use -SqlServerFqdn / -SqlDatabaseName overrides).
      2. Unless -SkipFirewallRule, add a temporary SQL firewall rule for this
         machine's public IP so the local connection isn't blocked, and remove
         it again on exit (the rule is always cleaned up, even on failure).
      3. Connect with the SQL admin login/password and, for each example bundle,
         INSERT it (plus its entries) only when a row with the same
         (Name, Version = 1) does not already exist — so a re-run is a no-op and
         never duplicates or disturbs an admin's hand-edited catalogue.

    SCHEMA PRECONDITION: the `Bundles` / `BundleEntries` tables are created by the
    backend's startup migrations on its first deploy — `setup.ps1` provisions the
    infrastructure but does not itself deploy the app. So this script must run
    *after* the backend has been deployed at least once. If the tables are not yet
    present the script stops with a clear message rather than a raw SQL error.

    IDEMPOTENT: each bundle is matched on (Name, Version = 1). Existing bundles
    are left untouched; only missing ones are inserted. Safe to re-run.

    DRY RUN: run with -WhatIf to print the firewall and INSERT actions without
    executing them. Read-only discovery (server discovery, the schema check and
    the per-bundle existence SELECT) still runs so the plan is grounded in the
    real state.

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
    ./scripts/seed-bundles.ps1

.EXAMPLE
    ./scripts/seed-bundles.ps1 -ResourceGroup anchor-rg `
        -SqlServerFqdn anchor-sql-xyz.database.windows.net -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ResourceGroup = 'anchor-rg',
    [string]$SqlServerFqdn,
    [string]$SqlDatabaseName = 'anchordb',
    [string]$SqlAdminLogin = 'anchoradmin',
    [securestring]$SqlAdminPassword,
    [switch]$SkipFirewallRule
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── The example catalogue ─────────────────────────────────────────────────────
# The real production bundles, mirroring DevDataSeeder.EnsureDevBundlesAsync but
# deliberately EXCLUDING the dev-only `Notepad (dev)` fixture (an app-bearing
# bundle that exists only so the headless verify-bundle-switch script can watch
# the agent rebuild its app matcher — it has no place in a real catalogue, #292).
# Kind / MatchType are stored as the enum *name* strings (Domain/App,
# Exact/Wildcard/Suffix/SignedPublisher) — see BundleEntryConfiguration's
# HasConversion<string>(). All entries below are domains, so Kind is Domain.
$ExampleBundles = @(
    @{
        Name    = 'Microsoft 365'
        Entries = @(
            @{ Value = '*.office.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = '*.office365.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = '*.microsoft.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = '*.microsoftonline.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = '*.live.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = '*.sharepoint.com'; MatchType = 'Wildcard'; Kind = 'Domain' }
            @{ Value = 'outlook.office.com'; MatchType = 'Exact'; Kind = 'Domain' }
            @{ Value = 'teams.microsoft.com'; MatchType = 'Exact'; Kind = 'Domain' }
        )
    },
    @{
        Name    = 'Smartschool'
        Entries = @(
            @{ Value = '*.smartschool.be'; MatchType = 'Wildcard'; Kind = 'Domain' }
        )
    },
    @{
        Name    = 'Bingel'
        Entries = @(
            @{ Value = '*.bingel.be'; MatchType = 'Wildcard'; Kind = 'Domain' }
        )
    }
)

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

# Add a uniqueidentifier parameter carrying a [guid] value to a command.
function Add-GuidParam {
    param([System.Data.SqlClient.SqlCommand]$Command, [string]$Name, [guid]$Value)
    [void]$Command.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter($Name, [System.Data.SqlDbType]::UniqueIdentifier)))
    $Command.Parameters[$Name].Value = $Value
}

# Add an nvarchar parameter carrying a string value to a command.
function Add-StringParam {
    param([System.Data.SqlClient.SqlCommand]$Command, [string]$Name, [string]$Value)
    [void]$Command.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter($Name, [System.Data.SqlDbType]::NVarChar)))
    $Command.Parameters[$Name].Value = $Value
}

# ── 1. Discover the SQL server + database ─────────────────────────────────────

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

# ── 2. Temporary firewall rule for this machine ───────────────────────────────

$firewallRuleName = "anchor-seed-bundles-$(Get-Date -Format 'yyyyMMddHHmmss')"
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

    # ── 3. Seed the bundles ───────────────────────────────────────────────────

    Write-Step 'Seed example bundles'

    $pw = ConvertTo-PlainText $SqlAdminPassword
    $connString = "Server=tcp:$SqlServerFqdn,1433;Database=$SqlDatabaseName;" +
        "User ID=$SqlAdminLogin;Password=$pw;Encrypt=true;TrustServerCertificate=false;Connection Timeout=30;"

    $conn = New-Object System.Data.SqlClient.SqlConnection $connString
    try {
        $conn.Open()

        # The schema is created by the backend's startup migrations on first
        # deploy, not by setup.ps1. Fail clearly if the tables aren't there yet
        # rather than surfacing a raw "Invalid object name 'Bundles'".
        $schemaCheck = $conn.CreateCommand()
        $schemaCheck.CommandText =
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_NAME = 'Bundles') THEN 1 ELSE 0 END"
        if ([int]$schemaCheck.ExecuteScalar() -ne 1) {
            throw "The 'Bundles' table does not exist in '$SqlDatabaseName' yet. Its schema is " +
                  "created by the backend's startup migrations on first deploy. Deploy the backend " +
                  "(push to main) at least once, then re-run this script."
        }

        $inserted = 0
        $skipped = 0
        foreach ($bundle in $ExampleBundles) {
            # Idempotency: match on (Name, Version = 1), the table's unique key.
            $exists = $conn.CreateCommand()
            $exists.CommandText = 'SELECT COUNT(1) FROM Bundles WHERE Name = @name AND Version = 1'
            Add-StringParam $exists '@name' $bundle.Name
            if ([int]$exists.ExecuteScalar() -gt 0) {
                Write-Host "    '$($bundle.Name)' already present — skipping." -ForegroundColor DarkGray
                $skipped++
                continue
            }

            if (-not $PSCmdlet.ShouldProcess("$($bundle.Name) (+$($bundle.Entries.Count) entries)", 'insert bundle')) {
                Write-Host "    DRYRUN  insert '$($bundle.Name)' with $($bundle.Entries.Count) entries" -ForegroundColor DarkGray
                continue
            }

            # Bundle + its entries are one atomic unit: a half-written bundle (no
            # entries) would silently allow everything, so wrap in a transaction.
            $tx = $conn.BeginTransaction()
            try {
                $bundleId = [guid]::NewGuid()
                $insertBundle = $conn.CreateCommand()
                $insertBundle.Transaction = $tx
                $insertBundle.CommandText =
                    'INSERT INTO Bundles (Id, Name, Version, IsArchived) VALUES (@id, @name, 1, 0)'
                Add-GuidParam $insertBundle '@id' $bundleId
                Add-StringParam $insertBundle '@name' $bundle.Name
                [void]$insertBundle.ExecuteNonQuery()

                foreach ($entry in $bundle.Entries) {
                    $insertEntry = $conn.CreateCommand()
                    $insertEntry.Transaction = $tx
                    $insertEntry.CommandText =
                        'INSERT INTO BundleEntries (Id, BundleId, Kind, Value, MatchType) ' +
                        'VALUES (@id, @bundleId, @kind, @value, @matchType)'
                    Add-GuidParam $insertEntry '@id' ([guid]::NewGuid())
                    Add-GuidParam $insertEntry '@bundleId' $bundleId
                    Add-StringParam $insertEntry '@kind' $entry.Kind
                    Add-StringParam $insertEntry '@value' $entry.Value
                    Add-StringParam $insertEntry '@matchType' $entry.MatchType
                    [void]$insertEntry.ExecuteNonQuery()
                }

                $tx.Commit()
                Write-Host "    Seeded '$($bundle.Name)' ($($bundle.Entries.Count) entries)." -ForegroundColor Green
                $inserted++
            }
            catch {
                $tx.Rollback()
                throw
            }
        }

        Write-Host ''
        Write-Host "    Done: $inserted inserted, $skipped already present." -ForegroundColor Green
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
