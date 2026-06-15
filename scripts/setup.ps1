#Requires -Version 5.1
<#
.SYNOPSIS
    One-command fork provisioning for an Anchor cloud environment (#213).

.DESCRIPTION
    Stands up a fork's Azure + GitHub environment end to end so that a
    subsequent push to `main` deploys the backend and dashboard with no further
    manual steps. It automates the operator checklist in docs/RELEASE.md:

      1. Preflight: confirm `az` and `gh` are installed, that you are logged in
         to both, and resolve the target GitHub repository.
      2. Create the resource group (idempotent).
      3. Create / update the two Entra app registrations the product needs:
            - the API app registration (exposes an `access_as_user` scope and
              an `api://<id>` identifier URI; gets a client secret for the
              on-behalf-of Graph directory search), and
            - the dashboard SPA app registration (SPA redirect URI for the
              Static Web App, pre-authorized to call the API scope).
         Both are looked up by display name first, so re-runs reuse them.
      4. Deploy infra/main.bicep into the resource group, passing the Entra
         tenant/client IDs so the App Service application settings are wired.
      5. Read the deployment outputs (resource names + URLs).
      6. Fetch the Static Web App deployment token and the App Service publish
         profile.
      7. Write the GitHub Actions secrets and variables the deploy workflows
         consume (names verified against .github/workflows/*-deploy.yml).

    The script is idempotent: every create step is guarded by an existence
    check or an idempotent `az` verb, so a re-run after a partial failure
    converges rather than erroring.

    DRY RUN: run with -WhatIf to print every mutating az / gh command without
    executing it. Read-only preflight and lookups still run so the plan is
    grounded in the real environment.

    ADMIN CONSENT IS MANUAL. Granting tenant-wide admin consent for the API
    permissions (and for the dashboard SPA to call the API) requires a
    Global Administrator / Privileged Role Administrator clicking "Grant admin
    consent" in the portal, or running `az ad app permission admin-consent`
    as such an admin. The script prints the exact command and the portal path,
    but does not assume it can perform it. It is safe to finish the rest and
    grant consent afterwards.

.PARAMETER ResourceGroup
    Resource group to create / deploy into. Default: anchor-rg.

.PARAMETER Location
    Default Azure region for the resource group and every resource that does not
    have a per-resource override. Default: westeurope.

.PARAMETER SqlLocation
.PARAMETER AppServiceLocation
.PARAMETER SignalRLocation
.PARAMETER StaticWebAppLocation
    Per-resource region overrides, passed straight through to the matching Bicep
    parameters. When omitted, an *existing* resource keeps its current region
    (read live, so a re-run never tries to move it — region is immutable in
    Azure) and a not-yet-created resource falls back to -Location. Use these to
    reproduce a split layout (e.g. the live arcadia env spans Belgium Central +
    West Europe) or to place SignalR / the Static Web App in a region where they
    are offered.

.PARAMETER UniqueSuffix
    Suffix for globally-unique resource names, passed straight through to the
    Bicep `uniqueSuffix` parameter. Pick something fork-specific (e.g. your
    school) so you do not collide with the original `arcadia` names.

.PARAMETER SqlAdminLogin
    SQL administrator login. When omitted, an existing SQL server's current
    login is reused (Azure does not allow changing it on an existing server),
    and a brand-new server falls back to the Bicep default (anchoradmin).

.PARAMETER SqlAdminPassword
    SQL admin password (SecureString). Required for a real deploy. Prompted
    securely if omitted and not a dry run.

.PARAMETER Repo
    GitHub repository in OWNER/REPO form to write secrets/variables to.
    Default: the repo of the current git remote (resolved via `gh repo view`).

.PARAMETER ApiAppName
    Display name of the API Entra app registration. Default: Anchor API (<suffix>).

.PARAMETER SpaAppName
    Display name of the dashboard SPA Entra app registration.
    Default: Anchor Dashboard (<suffix>).

.PARAMETER BicepFile
    Path to the Bicep template. Default: infra/main.bicep relative to the repo.

.PARAMETER SkipGitHub
    Provision Azure only; do not touch GitHub secrets/variables.

.PARAMETER SkipEntra
    Do not create/update Entra app registrations. Use when you manage app
    registrations out of band and only want infra + GitHub wiring. You must
    then pass -EntraClientId / -EntraTenantId for the deploy to be usable.

.PARAMETER SkipInfra
    Do not deploy infra/main.bicep. Resource names are still resolved and the
    live deployment credentials (SWA token + publish profile) are still read so
    GitHub secrets/variables can be (re-)wired against an environment that
    already exists. Use to resume after the infra is already deployed, or to
    re-wire GitHub for the live env without touching Azure.

.PARAMETER EntraTenantId
    Override the tenant ID instead of using the signed-in az account tenant.

.PARAMETER EntraClientId
    Override / supply the API app registration client ID (skips API app
    creation lookup). Required together with -SkipEntra for a usable deploy.
    When adopting an existing environment, pass the real API app id so the
    deploy re-applies the same AzureAd__ClientId instead of creating a new app.

.PARAMETER SpaClientId
    Override / supply the dashboard SPA app registration client ID, mirroring
    -EntraClientId for the SPA. Used for the GitHub ENTRA_CLIENT_ID variable.

.EXAMPLE
    ./scripts/setup.ps1 -UniqueSuffix lincolnhigh -WhatIf
    # Dry run: prints the full provisioning plan for the lincolnhigh fork.

.EXAMPLE
    ./scripts/setup.ps1 -UniqueSuffix lincolnhigh
    # Provisions Azure + Entra, wires GitHub secrets/variables, prints the
    # admin-consent command to run as a tenant admin.

.NOTES
    Verify (no Azure needed):
        Invoke-ScriptAnalyzer scripts/setup.ps1
        powershell -NoProfile -Command "[void][ScriptBlock]::Create((Get-Content -Raw scripts/setup.ps1))"
        ./scripts/setup.ps1 -UniqueSuffix test -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
# This is an interactive operator script: coloured, host-facing progress output
# via Write-Host is intentional (it is not producing pipeline data), so the
# PSAvoidUsingWriteHost rule does not apply here.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '')]
param(
    [string]$ResourceGroup = 'anchor-rg',
    [string]$Location = 'westeurope',
    [string]$SqlLocation,
    [string]$AppServiceLocation,
    [string]$SignalRLocation,
    [string]$StaticWebAppLocation,
    [Parameter(Mandatory)]
    [string]$UniqueSuffix,
    [string]$SqlAdminLogin,
    [securestring]$SqlAdminPassword,
    [string]$Repo,
    [string]$ApiAppName,
    [string]$SpaAppName,
    [string]$BicepFile,
    [switch]$SkipGitHub,
    [switch]$SkipEntra,
    [switch]$SkipInfra,
    [string]$EntraTenantId,
    [string]$EntraClientId,
    [string]$SpaClientId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Defaults derived from the suffix ─────────────────────────────────────────
if (-not $ApiAppName) { $ApiAppName = "Anchor API ($UniqueSuffix)" }
if (-not $SpaAppName) { $SpaAppName = "Anchor Dashboard ($UniqueSuffix)" }
if (-not $BicepFile) {
    $BicepFile = Join-Path (Join-Path $PSScriptRoot '..') 'infra/main.bicep'
}
$BicepFile = (Resolve-Path -LiteralPath $BicepFile -ErrorAction Stop).Path

# Microsoft Graph well-known app id + the User.Read delegated permission id,
# used to grant the SPA the baseline sign-in/profile scope.
$GraphAppId = '00000003-0000-0000-c000-000000000000'
$GraphUserRead = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'  # User.Read (delegated)

# ── Small helpers ────────────────────────────────────────────────────────────

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Manual {
    param([string]$Message)
    Write-Host "[MANUAL] $Message" -ForegroundColor Yellow
}

# Invoke a native command. Honours -WhatIf via ShouldProcess: in dry-run it
# prints the command and returns $null instead of executing. $Capture returns
# stdout (trimmed) for commands we read output from.
function Invoke-Native {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string[]]$ArgList,
        [string]$Target,       # ShouldProcess target (the thing being changed)
        [string]$Action,       # ShouldProcess action description
        [switch]$Capture,      # return stdout
        [switch]$ReadOnly      # always run, even under -WhatIf (lookups)
    )
    # Redact secret-bearing args (password / client secret) for any display or
    # error text, so a dry-run plan or a failure message never leaks them.
    $safeArgs = $ArgList | ForEach-Object {
        if ($_ -match '^(sqlAdminPassword|password|clientSecret)=' ) {
            ($_ -replace '=.*$', '=<redacted>')
        }
        else { $_ }
    }
    $display = "$Exe $($safeArgs -join ' ')"
    if (-not $ReadOnly) {
        $tgt = if ($Target) { $Target } else { $display }
        $act = if ($Action) { $Action } else { 'run' }
        if (-not $PSCmdlet.ShouldProcess($tgt, $act)) {
            Write-Host "    DRYRUN  $display" -ForegroundColor DarkGray
            return $null
        }
    }
    Write-Verbose "    exec    $display"
    if ($Capture) {
        $out = & $Exe @ArgList
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed ($LASTEXITCODE): $display"
        }
        return ($out | Out-String).Trim()
    }
    else {
        & $Exe @ArgList
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed ($LASTEXITCODE): $display"
        }
    }
}

# Convert a SecureString to plaintext for the few az calls that need it.
function ConvertFrom-SecureStringPlain {
    param([securestring]$Secure)
    if (-not $Secure) { return $null }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# ── Read-only discovery helpers ──────────────────────────────────────────────
# All of these probe the live environment so a re-run can adopt what already
# exists. They tolerate a missing resource (return $null / $false) and never
# throw, so they are safe to call before anything is provisioned and under
# -WhatIf. They go through Invoke-AzRead rather than Invoke-Native because
# Invoke-Native treats a non-zero exit as fatal, whereas "not found" is an
# expected outcome here.

# Run a read-only `az` command tolerantly: return trimmed stdout, or $null on a
# non-zero exit / empty output. Locally relaxes $ErrorActionPreference to
# 'Continue' (function-scoped, so it reverts on return) because in Windows
# PowerShell 5.1 redirecting a native command's stderr (2>$null) wraps each line
# as a NativeCommandError, which the script-level 'Stop' preference would
# otherwise turn into a terminating error for an expected "not found".
function Invoke-AzRead {
    param([string[]]$ArgList)
    $ErrorActionPreference = 'Continue'
    $out = & az @ArgList 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $s = ($out | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    return $s
}

# Region of an existing resource, or $null if it does not exist. Normalised to
# the canonical region name (lowercase, no spaces) because `az resource show`
# returns the display form ("Belgium Central") for some resource types and the
# canonical form ("belgiumcentral") for others; Bicep + az --parameters need the
# canonical name, and an embedded space would break native arg parsing.
function Get-ResourceLocation {
    param([string]$Name, [string]$ResourceType)
    $loc = Invoke-AzRead @('resource', 'show', '--resource-group', $ResourceGroup, '--name', $Name, '--resource-type', $ResourceType, '--query', 'location', '-o', 'tsv')
    if (-not $loc) { return $null }
    return ($loc -replace '\s', '').ToLowerInvariant()
}

# All application settings of an existing App Service as a hashtable name->value,
# or $null if the App Service does not exist / cannot be read.
function Get-AppServiceSetting {
    param([string]$Name)
    $json = Invoke-AzRead @('webapp', 'config', 'appsettings', 'list', '--name', $Name, '--resource-group', $ResourceGroup, '-o', 'json')
    if (-not $json) { return $null }
    $map = @{}
    foreach ($s in ($json | ConvertFrom-Json)) {
        if ($s.PSObject.Properties.Name -contains 'name') { $map[$s.name] = $s.value }
    }
    return $map
}

# StrictMode-safe property equality on a (possibly null / shapeless) object:
# returns $false rather than throwing when the property is absent. `az` returns
# an empty array `[]` for an app with no scopes / secrets / permissions, and
# under Set-StrictMode -Version Latest piping that into `Where-Object { $_.prop }`
# throws PropertyNotFoundStrict — so all the JSON filters below go through here.
function Test-Prop {
    param($Object, [string]$Name, $Value)
    if ($null -eq $Object) { return $false }
    if ($Object.PSObject.Properties.Name -notcontains $Name) { return $false }
    return $Object.$Name -eq $Value
}

# Id of the existing access_as_user delegated scope on an app, or $null.
function Get-AccessAsUserScopeId {
    param([string]$AppId)
    $json = Invoke-AzRead @('ad', 'app', 'show', '--id', $AppId, '--query', 'api.oauth2PermissionScopes', '-o', 'json')
    if (-not $json) { return $null }
    foreach ($scope in ($json | ConvertFrom-Json)) {
        if (Test-Prop $scope 'value' 'access_as_user') { return $scope.id }
    }
    return $null
}

# True if the app already has a client secret with the given display name.
function Test-ClientSecret {
    param([string]$AppId, [string]$DisplayName)
    $json = Invoke-AzRead @('ad', 'app', 'credential', 'list', '--id', $AppId, '-o', 'json')
    if (-not $json) { return $false }
    foreach ($cred in ($json | ConvertFrom-Json)) {
        if (Test-Prop $cred 'displayName' $DisplayName) { return $true }
    }
    return $false
}

# True if the app already requests $PermissionId on $ResourceAppId.
function Test-PermissionGranted {
    param([string]$AppId, [string]$ResourceAppId, [string]$PermissionId)
    $json = Invoke-AzRead @('ad', 'app', 'show', '--id', $AppId, '--query', 'requiredResourceAccess', '-o', 'json')
    if (-not $json) { return $false }
    foreach ($r in ($json | ConvertFrom-Json)) {
        if (-not (Test-Prop $r 'resourceAppId' $ResourceAppId)) { continue }
        foreach ($access in $r.resourceAccess) {
            if (Test-Prop $access 'id' $PermissionId) { return $true }
        }
    }
    return $false
}

# ── Step 1: preflight ────────────────────────────────────────────────────────

Write-Step 'Preflight — tooling and login'

foreach ($tool in 'az', 'gh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required tool '$tool' is not on PATH. Install it and re-run."
    }
}

# az: who am I (read-only — runs even under -WhatIf so the plan is grounded).
$account = Invoke-Native -Exe 'az' -ArgList @('account', 'show', '-o', 'json') -Capture -ReadOnly
if (-not $account) {
    throw "Not logged in to Azure. Run 'az login' first."
}
$acct = $account | ConvertFrom-Json
$tenantId = if ($EntraTenantId) { $EntraTenantId } else { $acct.tenantId }
Write-Host "    Azure subscription : $($acct.name) ($($acct.id))"
Write-Host "    Tenant            : $tenantId"

if (-not $SkipGitHub) {
    # gh auth (read-only)
    Invoke-Native -Exe 'gh' -ArgList @('auth', 'status') -ReadOnly | Out-Null
    if (-not $Repo) {
        $Repo = Invoke-Native -Exe 'gh' -ArgList @('repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner') -Capture -ReadOnly
    }
    if (-not $Repo) { throw 'Could not resolve the GitHub repository. Pass -Repo OWNER/REPO.' }
    Write-Host "    GitHub repo       : $Repo"
}

# SQL password is required for a real deploy (the deploy always passes it, so
# when adopting an existing SQL server supply the *current* admin password — a
# different value resets it). Not needed when the deploy is skipped.
$sqlPwPlain = ConvertFrom-SecureStringPlain $SqlAdminPassword
if (-not $sqlPwPlain -and -not $WhatIfPreference -and -not $SkipInfra) {
    $SqlAdminPassword = Read-Host -AsSecureString -Prompt 'SQL admin password'
    $sqlPwPlain = ConvertFrom-SecureStringPlain $SqlAdminPassword
}

# ── Step 2: resource group ───────────────────────────────────────────────────

Write-Step "Resource group — $ResourceGroup ($Location)"

$rgExists = Invoke-Native -Exe 'az' -ArgList @('group', 'exists', '-n', $ResourceGroup) -Capture -ReadOnly
if ($rgExists -eq 'true') {
    Write-Host "    Resource group already exists — reusing."
}
else {
    Invoke-Native -Exe 'az' -ArgList @('group', 'create', '-n', $ResourceGroup, '-l', $Location, '-o', 'none') `
        -Target $ResourceGroup -Action 'create resource group'
}

# ── Step 2b: discover existing environment (read-only) ───────────────────────
# Resource names are deterministic from the suffix (they mirror the Bicep
# defaults), so probe for an already-deployed environment and adopt what's
# there: pin each resource's current region (region is immutable, so a re-run
# must never try to move it) and reuse the App Service's existing Entra wiring
# (so we never overwrite a working AzureAd__ClientId with a freshly-created
# app). All reads are tolerant and run under -WhatIf.

Write-Step 'Discover existing environment'

$sqlServerNameGuess    = "anchor-sql-$UniqueSuffix"
$appServiceNameGuess   = "anchor-api-$UniqueSuffix"
$signalrNameGuess      = 'anchor-signalr'
$staticWebAppNameGuess = 'anchor-dashboard'

# Per-resource region: explicit override > existing resource's region > -Location.
function Resolve-ResourceLocation {
    param([string]$Override, [string]$Name, [string]$ResourceType)
    if ($Override) { return $Override }
    $existing = Get-ResourceLocation -Name $Name -ResourceType $ResourceType
    if ($existing) {
        Write-Host "    $Name exists in $existing — pinning region (no move)."
        return $existing
    }
    return $Location
}

$resolvedSqlLocation     = Resolve-ResourceLocation $SqlLocation          $sqlServerNameGuess    'Microsoft.Sql/servers'
$resolvedAppLocation     = Resolve-ResourceLocation $AppServiceLocation   $appServiceNameGuess   'Microsoft.Web/sites'
$resolvedSignalrLocation = Resolve-ResourceLocation $SignalRLocation      $signalrNameGuess      'Microsoft.SignalRService/SignalR'
$resolvedSwaLocation     = Resolve-ResourceLocation $StaticWebAppLocation $staticWebAppNameGuess 'Microsoft.Web/staticSites'

# SQL admin login: explicit override > existing server's login > Bicep default.
# Azure does not allow changing an existing server's administrator login, so a
# redeploy MUST pass the current one (adopt-in-place, like the regions above).
# $null means "don't pass it" → the Bicep default applies (fresh environments).
$resolvedSqlLogin = $SqlAdminLogin
if (-not $resolvedSqlLogin) {
    $existingSqlLogin = Invoke-AzRead @('sql', 'server', 'show', '--name', $sqlServerNameGuess, '--resource-group', $ResourceGroup, '--query', 'administratorLogin', '-o', 'tsv')
    if ($existingSqlLogin) {
        $resolvedSqlLogin = $existingSqlLogin
        Write-Host "    SQL server $sqlServerNameGuess admin login is '$resolvedSqlLogin' — reusing (login is immutable)."
    }
}

# Adopt the live Entra wiring from the App Service if present.
$existingClientId = $null
$existingTenantId = $null
$existingSettings = Get-AppServiceSetting -Name $appServiceNameGuess
if ($existingSettings) {
    $existingClientId = $existingSettings['AzureAd__ClientId']
    $existingTenantId = $existingSettings['AzureAd__TenantId']
    if ($existingClientId) {
        Write-Host "    App Service already wired to AzureAd__ClientId $existingClientId — preserving."
    }
}

# Effective tenant id: explicit override > existing App Service setting > account.
if (-not $EntraTenantId -and $existingTenantId) {
    $tenantId = $existingTenantId
    Write-Host "    Adopting tenant from App Service: $tenantId"
}

# ── Step 3: Entra app registrations ──────────────────────────────────────────
# Looked up by display name so re-runs reuse the existing app rather than
# creating duplicates (idempotent). When an id is supplied (-EntraClientId) or
# discovered on the live App Service, that app is *adopted*: we reuse it and do
# not re-run the mutating configuration steps, so we never disturb a working
# registration. Admin consent is flagged as manual.

# API client id: explicit override > adopted from App Service > (look up / create).
$apiClientId = if ($EntraClientId) { $EntraClientId } elseif ($existingClientId) { $existingClientId } else { $null }
$apiAppAdopted = [bool]$apiClientId
$spaClientId = $SpaClientId
$scopeId = $null

if ($SkipEntra) {
    Write-Step 'Entra app registrations — SKIPPED (-SkipEntra)'
    if (-not $apiClientId) {
        Write-Manual 'No -EntraClientId given with -SkipEntra: the deploy will set an empty AzureAd__ClientId and reject tokens until you wire it.'
    }
}
elseif ($apiAppAdopted) {
    # An id was supplied or discovered — adopt it untouched. Reuse the existing
    # access_as_user scope id for the SPA pre-authorization later; do not
    # re-issue secrets or re-patch the scope on a registration we don't own.
    Write-Step "Entra app registration (API) — adopting $apiClientId"
    Write-Host "    Reusing existing API app; skipping create/scope/secret mutations."
    $scopeId = Get-AccessAsUserScopeId -AppId $apiClientId
    if (-not $scopeId) {
        Write-Manual "Could not read an access_as_user scope on $apiClientId; skipping SPA API pre-authorization. Verify the API app exposes that scope."
    }
    # Adopt the SPA app too when not explicitly provided (lookup by display name).
    if (-not $spaClientId) {
        $spaClientId = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'list', '--display-name', $SpaAppName, '--query', '[0].appId', '-o', 'tsv') -Capture -ReadOnly
        if ($spaClientId) { Write-Host "    Found existing SPA app: $spaClientId" }
        else { Write-Manual "No SPA app found by name '$SpaAppName' and none supplied (-SpaClientId); ENTRA_CLIENT_ID will fall back to the API id." }
    }
}
else {
    Write-Step "Entra app registration (API) — $ApiAppName"

    # Look up by display name (read-only).
    $found = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'list', '--display-name', $ApiAppName, '--query', '[0].appId', '-o', 'tsv') -Capture -ReadOnly
    if ($found) {
        $apiClientId = $found
        Write-Host "    Found existing API app: $apiClientId"
    }
    else {
        $created = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'create', '--display-name', $ApiAppName, '--sign-in-audience', 'AzureADMyOrg', '--query', 'appId', '-o', 'tsv') -Capture `
            -Target $ApiAppName -Action 'create API app registration'
        $apiClientId = if ($created) { $created } else { '<api-client-id>' }
        Write-Host "    Created API app: $apiClientId"
    }

    # Identifier URI api://<appId> (idempotent — same value on re-run).
    $identifierUri = "api://$apiClientId"
    Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'update', '--id', $apiClientId, '--identifier-uris', $identifierUri, '-o', 'none') `
        -Target $apiClientId -Action "set identifier URI $identifierUri"

    # Expose the access_as_user scope. Reuse the existing scope id on a re-run so
    # the id is stable (a changed id would invalidate prior admin consent and the
    # SPA pre-authorization). Only PATCH when the scope is absent.
    $scopeId = Get-AccessAsUserScopeId -AppId $apiClientId
    if ($scopeId) {
        Write-Host "    access_as_user scope already present ($scopeId) — reusing."
    }
    else {
        $scopeId = [guid]::NewGuid().ToString()
        # oauth2PermissionScopes is a property of the application's `api`
        # (apiApplication) entity, not a top-level property — Graph rejects a
        # top-level form with "Invalid property 'oauth2PermissionScopes'".
        $apiBody = @{
            api = @{
                oauth2PermissionScopes = @(
                    @{
                        id                      = $scopeId
                        adminConsentDescription = 'Allow the app to access the Anchor API on behalf of the signed-in user.'
                        adminConsentDisplayName = 'Access Anchor API'
                        userConsentDescription  = 'Allow the app to access the Anchor API on your behalf.'
                        userConsentDisplayName  = 'Access Anchor API'
                        value                   = 'access_as_user'
                        type                    = 'User'
                        isEnabled               = $true
                    }
                )
            }
        }
        $apiBodyJson = ($apiBody | ConvertTo-Json -Depth 8 -Compress)
        # az ad app update --set api=... requires a JSON value; write to a temp file
        # to avoid shell-quoting issues with native arg parsing.
        if ($PSCmdlet.ShouldProcess($apiClientId, 'expose access_as_user scope')) {
            # PATCH by directory object id, not the applications(appId='...')
            # form: those parentheses are passed through the Windows az.cmd
            # wrapper to cmd.exe, which treats ( ) as special and fails the call
            # with "--headers was unexpected at this time". The object-id URL has
            # no parens and parses cleanly.
            $objectId = Invoke-AzRead @('ad', 'app', 'show', '--id', $apiClientId, '--query', 'id', '-o', 'tsv')
            if (-not $objectId) { throw "Could not resolve the directory object id for app $apiClientId" }
            $tmp = New-TemporaryFile
            try {
                Set-Content -LiteralPath $tmp -Value $apiBodyJson -Encoding UTF8
                Invoke-Native -Exe 'az' -ArgList @('rest', '--method', 'PATCH',
                    '--url', "https://graph.microsoft.com/v1.0/applications/$objectId",
                    '--headers', 'Content-Type=application/json',
                    '--body', "@$tmp", '-o', 'none') -Target $apiClientId -Action 'patch API scope'
            }
            finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        }
        else {
            Write-Host "    DRYRUN  PATCH applications/<object-id> api.oauth2PermissionScopes += access_as_user" -ForegroundColor DarkGray
        }
    }

    # API client secret (for the OBO Graph directory search). A secret value can
    # only be read at creation, so creating one on every run would both leak
    # duplicates and be unrecoverable — skip when an 'anchor-obo' secret already
    # exists (idempotent / resume-safe). Create without --append so we don't
    # accumulate secrets across runs of a fresh fork.
    if (Test-ClientSecret -AppId $apiClientId -DisplayName 'anchor-obo') {
        Write-Host "    API client secret 'anchor-obo' already exists — skipping (cannot be re-displayed)."
    }
    else {
        $apiSecret = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'credential', 'reset', '--id', $apiClientId, '--display-name', 'anchor-obo', '--query', 'password', '-o', 'tsv') -Capture `
            -Target $apiClientId -Action 'create API client secret'
        if ($apiSecret) {
            Write-Manual "API client secret created. It is ONLY needed if you use the user-directory search (the on-behalf-of Graph call); skip this otherwise."
            Write-Host "    Add it to the App Service '$appServiceNameGuess' as two application settings:" -ForegroundColor Yellow
            Write-Host "        AzureAd__ClientCredentials__0__SourceType  = ClientSecret" -ForegroundColor Yellow
            Write-Host "        AzureAd__ClientCredentials__0__ClientSecret = <the secret printed below>" -ForegroundColor Yellow
            Write-Host "    Portal: App Service '$appServiceNameGuess' -> Settings -> Environment variables -> App settings." -ForegroundColor Yellow
            Write-Host "    Or run (replace <secret>):" -ForegroundColor Yellow
            Write-Host "        az webapp config appsettings set --name $appServiceNameGuess --resource-group $ResourceGroup --settings AzureAd__ClientCredentials__0__SourceType=ClientSecret AzureAd__ClientCredentials__0__ClientSecret=<secret>" -ForegroundColor Yellow
            Write-Host "    AzureAd__ClientCredentials secret: $apiSecret" -ForegroundColor DarkYellow
        }
    }

    # ── Dashboard SPA app registration ───────────────────────────────────────
    Write-Step "Entra app registration (dashboard SPA) — $SpaAppName"

    if ($spaClientId) {
        Write-Host "    Using provided SPA app id: $spaClientId"
    }
    else {
        $foundSpa = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'list', '--display-name', $SpaAppName, '--query', '[0].appId', '-o', 'tsv') -Capture -ReadOnly
        if ($foundSpa) {
            $spaClientId = $foundSpa
            Write-Host "    Found existing SPA app: $spaClientId"
        }
        else {
            $createdSpa = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'create', '--display-name', $SpaAppName, '--sign-in-audience', 'AzureADMyOrg', '--query', 'appId', '-o', 'tsv') -Capture `
                -Target $SpaAppName -Action 'create dashboard SPA app registration'
            $spaClientId = if ($createdSpa) { $createdSpa } else { '<spa-client-id>' }
            Write-Host "    Created SPA app: $spaClientId"
        }
    }

    # The SPA redirect URI is filled in after the deploy (we need the SWA URL),
    # so it is set in Step 5 once $swaUrl is known. Here, grant the baseline
    # Graph User.Read delegated permission so sign-in works (skip if already
    # granted so a re-run doesn't duplicate the entry).
    if (Test-PermissionGranted -AppId $spaClientId -ResourceAppId $GraphAppId -PermissionId $GraphUserRead) {
        Write-Host "    SPA already has Graph User.Read — skipping."
    }
    else {
        Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'permission', 'add', '--id', $spaClientId,
            '--api', $GraphAppId, '--api-permissions', "$GraphUserRead=Scope", '-o', 'none') `
            -Target $spaClientId -Action 'grant Graph User.Read to SPA'
    }
}

# ── Step 4: deploy Bicep ─────────────────────────────────────────────────────
# Per-resource regions are passed explicitly (resolved in Step 2b) so an
# existing resource keeps its region. Skipped entirely under -SkipInfra, in
# which case Step 5 falls back to the deterministic resource names.

if ($SkipInfra) {
    Write-Step 'Deploy infra — SKIPPED (-SkipInfra)'
    $outputsJson = $null
}
else {
    Write-Step "Deploy infra — $BicepFile"

    $deployArgs = @(
        'deployment', 'group', 'create',
        '--resource-group', $ResourceGroup,
        '--template-file', $BicepFile,
        '--parameters',
        "uniqueSuffix=$UniqueSuffix",
        "entraTenantId=$tenantId",
        "sqlServerLocation=$resolvedSqlLocation",
        "appServiceLocation=$resolvedAppLocation",
        "signalrLocation=$resolvedSignalrLocation",
        "staticWebAppLocation=$resolvedSwaLocation"
    )
    if ($apiClientId) { $deployArgs += "entraClientId=$apiClientId" }
    if ($resolvedSqlLogin) { $deployArgs += "sqlAdminLogin=$resolvedSqlLogin" }
    if ($sqlPwPlain) { $deployArgs += "sqlAdminPassword=$sqlPwPlain" }
    $deployArgs += @('--query', 'properties.outputs', '-o', 'json')

    $outputsJson = Invoke-Native -Exe 'az' -ArgList $deployArgs `
        -Target $ResourceGroup -Action 'deploy Bicep template' -Capture
}

# ── Step 5: read outputs ─────────────────────────────────────────────────────

Write-Step 'Deployment outputs'

# In a real run we get JSON; in dry-run there is nothing, so synthesize the
# names from the same defaults the Bicep template uses, so the plan for the
# remaining steps is still printed.
function Get-Output {
    param([object]$Outputs, [string]$Name, [string]$Fallback)
    if ($Outputs -and ($Outputs.PSObject.Properties.Name -contains $Name)) {
        return $Outputs.$Name.value
    }
    return $Fallback
}

$outputs = if ($outputsJson) { $outputsJson | ConvertFrom-Json } else { $null }

$appServiceName = Get-Output $outputs 'appServiceName' "anchor-api-$UniqueSuffix"
$appServiceUrl = Get-Output $outputs 'appServiceUrl'  "https://anchor-api-$UniqueSuffix.azurewebsites.net"
$staticWebAppName = Get-Output $outputs 'staticWebAppName' 'anchor-dashboard'
$swaUrl = Get-Output $outputs 'swaUrl' 'https://<swa-host>'
$outEntraTenant = Get-Output $outputs 'entraTenantId' $tenantId
$outEntraClient = Get-Output $outputs 'entraClientId' $apiClientId
$outEntraAudience = Get-Output $outputs 'entraAudience' "api://$apiClientId"

Write-Host "    appServiceName : $appServiceName"
Write-Host "    appServiceUrl  : $appServiceUrl"
Write-Host "    staticWebApp   : $staticWebAppName"
Write-Host "    swaUrl         : $swaUrl"
Write-Host "    entraTenantId  : $outEntraTenant"
Write-Host "    entraClientId  : $outEntraClient"
Write-Host "    entraAudience  : $outEntraAudience"

# API scope the dashboard requests: <audience>/access_as_user.
$apiScope = "$outEntraAudience/access_as_user"

# Now that the SWA URL is known, set the SPA redirect URI + pre-authorize it
# against the API scope (idempotent: az overwrites the redirect set). Skipped
# when the SWA URL is unknown (placeholder under -WhatIf / -SkipInfra) so we
# never write a bogus redirect.
if (-not $SkipEntra -and $spaClientId -and ($swaUrl -notmatch '<')) {
    Write-Step 'Entra SPA — redirect URI + API pre-authorization'
    $redirect = "$swaUrl/"
    Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'update', '--id', $spaClientId,
        '--web-redirect-uris', $redirect, '-o', 'none') `
        -Target $spaClientId -Action "set SPA redirect URI $redirect"
    # Request the API's access_as_user scope from the SPA (needs a resolved scope id).
    if ($apiClientId -and $scopeId -and $outEntraAudience -ne 'api://') {
        Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'permission', 'add', '--id', $spaClientId,
            '--api', $apiClientId, '--api-permissions', "$scopeId=Scope", '-o', 'none') `
            -Target $spaClientId -Action 'grant API access_as_user to SPA' -ErrorAction SilentlyContinue 2>$null
    }
}
elseif (-not $SkipEntra -and $spaClientId) {
    Write-Host '    SPA redirect URI deferred — SWA URL not yet known (re-run after the deploy completes).' -ForegroundColor DarkGray
}

# ── Step 6: deployment secrets (SWA token + publish profile) ──────────────────

Write-Step 'Fetch deployment credentials'

# These are reads, not mutations, but they only succeed once the SWA / App
# Service actually exist (i.e. after a real deploy). In -WhatIf there is no
# deployed resource, so skip the live reads and use placeholders so the GitHub
# wiring plan still prints. In a real run we tolerate a not-yet-existing
# resource (e.g. resuming after a half-finished deploy): the credential stays a
# placeholder and Step 7 skips that secret rather than failing the whole run.
if ($WhatIfPreference) {
    Write-Host '    DRYRUN  (skipping live credential reads; resources not deployed)' -ForegroundColor DarkGray
    $swaToken = '<swa-deployment-token>'
    $publishProfile = '<publish-profile-xml>'
}
else {
    $swaToken = Invoke-AzRead @('staticwebapp', 'secrets', 'list', '--name', $staticWebAppName, '--query', 'properties.apiKey', '-o', 'tsv')
    if (-not $swaToken) {
        $swaToken = '<swa-deployment-token>'
        Write-Host "    Static Web App '$staticWebAppName' not reachable yet — skipping its token (re-run after deploy)." -ForegroundColor Yellow
    }

    $publishProfile = Invoke-AzRead @('webapp', 'deployment', 'list-publishing-profiles', '--name', $appServiceName, '--resource-group', $ResourceGroup, '--xml')
    if (-not $publishProfile) {
        $publishProfile = '<publish-profile-xml>'
        Write-Host "    App Service '$appServiceName' not reachable yet — skipping publish profile (re-run after deploy)." -ForegroundColor Yellow
    }
}

# ── Step 7: GitHub secrets + variables ───────────────────────────────────────
# Names verified against .github/workflows/backend-deploy.yml and
# dashboard-deploy.yml. Secrets: AZURE_WEBAPP_PUBLISH_PROFILE,
# AZURE_STATIC_WEB_APPS_API_TOKEN. Variables: AZURE_WEBAPP_NAME, API_BASE_URL,
# ENTRA_TENANT_ID, ENTRA_CLIENT_ID, API_SCOPE.

if ($SkipGitHub) {
    Write-Step 'GitHub secrets/variables — SKIPPED (-SkipGitHub)'
}
else {
    Write-Step "GitHub secrets + variables — $Repo"

    function Set-GhSecret {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$Name, [string]$Value)
        # A placeholder value means the underlying credential could not be read
        # (resource not deployed yet). Skip rather than overwrite a good secret
        # with junk; the operator re-runs once the resource exists. Still shown
        # in the -WhatIf plan so the intent is visible.
        if (-not $WhatIfPreference -and $Value -match '^<.*>$') {
            Write-Host "    secret  $Name SKIPPED (credential not available yet)" -ForegroundColor Yellow
            return
        }
        if ($PSCmdlet.ShouldProcess("$Repo/$Name", 'set GitHub secret')) {
            $Value | & gh secret set $Name --repo $Repo --body -
            if ($LASTEXITCODE -ne 0) { throw "gh secret set $Name failed ($LASTEXITCODE)" }
            Write-Host "    secret  $Name set"
        }
        else {
            Write-Host "    DRYRUN  gh secret set $Name --repo $Repo --body <hidden>" -ForegroundColor DarkGray
        }
    }

    function Set-GhVariable {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$Name, [string]$Value)
        Invoke-Native -Exe 'gh' -ArgList @('variable', 'set', $Name, '--repo', $Repo, '--body', $Value) `
            -Target "$Repo/$Name" -Action 'set GitHub variable'
        if (-not $WhatIfPreference) { Write-Host "    var     $Name = $Value" }
    }

    Set-GhSecret -Name 'AZURE_WEBAPP_PUBLISH_PROFILE'   -Value $publishProfile
    Set-GhSecret -Name 'AZURE_STATIC_WEB_APPS_API_TOKEN' -Value $swaToken

    Set-GhVariable -Name 'AZURE_WEBAPP_NAME' -Value $appServiceName
    Set-GhVariable -Name 'API_BASE_URL'      -Value $appServiceUrl
    Set-GhVariable -Name 'ENTRA_TENANT_ID'   -Value $outEntraTenant
    # The dashboard SPA client id when we created one, else the API id as a
    # last resort (single-app setups).
    Set-GhVariable -Name 'ENTRA_CLIENT_ID'   -Value ($(if ($spaClientId) { $spaClientId } else { $outEntraClient }))
    Set-GhVariable -Name 'API_SCOPE'         -Value $apiScope
}

# ── Final: manual follow-ups ─────────────────────────────────────────────────

Write-Step 'Done — remaining manual steps'

Write-Manual 'Grant Entra admin consent (requires a tenant admin):'
if (-not $SkipEntra -and $apiClientId) {
    Write-Host "    az ad app permission admin-consent --id $apiClientId" -ForegroundColor Yellow
}
if (-not $SkipEntra -and $spaClientId) {
    Write-Host "    az ad app permission admin-consent --id $spaClientId" -ForegroundColor Yellow
}
Write-Host '    Or: Entra portal -> App registrations -> <app> -> API permissions -> Grant admin consent.' -ForegroundColor Yellow
Write-Manual 'Add the API client secret to the App Service as AzureAd__ClientCredentials (see above) if the user-directory search is needed.'
Write-Host ''
Write-Host 'Provisioning plan complete. Push to `main` to trigger the deploy workflows.' -ForegroundColor Green

# Reaching here means success: every mutating step goes through Invoke-Native,
# which throws (terminating, under $ErrorActionPreference='Stop') on failure. The
# tolerant read-only probes leave a non-zero $LASTEXITCODE behind on an expected
# "not found", so exit 0 explicitly to report the real outcome.
exit 0
