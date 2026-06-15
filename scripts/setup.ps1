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
    Azure region. Default: westeurope (matches infra/main.bicep).

.PARAMETER UniqueSuffix
    Suffix for globally-unique resource names, passed straight through to the
    Bicep `uniqueSuffix` parameter. Pick something fork-specific (e.g. your
    school) so you do not collide with the original `arcadia` names.

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

.PARAMETER EntraTenantId
    Override the tenant ID instead of using the signed-in az account tenant.

.PARAMETER EntraClientId
    Override / supply the API app registration client ID (skips API app
    creation lookup). Required together with -SkipEntra for a usable deploy.

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
    [Parameter(Mandatory)]
    [string]$UniqueSuffix,
    [securestring]$SqlAdminPassword,
    [string]$Repo,
    [string]$ApiAppName,
    [string]$SpaAppName,
    [string]$BicepFile,
    [switch]$SkipGitHub,
    [switch]$SkipEntra,
    [string]$EntraTenantId,
    [string]$EntraClientId
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

# SQL password is required for a real deploy.
$sqlPwPlain = ConvertFrom-SecureStringPlain $SqlAdminPassword
if (-not $sqlPwPlain -and -not $WhatIfPreference) {
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

# ── Step 3: Entra app registrations ──────────────────────────────────────────
# Looked up by display name so re-runs reuse the existing app rather than
# creating duplicates (idempotent). Admin consent is flagged as manual.

$apiClientId = $EntraClientId
$spaClientId = $null

if ($SkipEntra) {
    Write-Step 'Entra app registrations — SKIPPED (-SkipEntra)'
    if (-not $apiClientId) {
        Write-Manual 'No -EntraClientId given with -SkipEntra: the deploy will set an empty AzureAd__ClientId and reject tokens until you wire it.'
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

    # Identifier URI api://<appId> and the access_as_user delegated scope.
    $identifierUri = "api://$apiClientId"
    Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'update', '--id', $apiClientId, '--identifier-uris', $identifierUri, '-o', 'none') `
        -Target $apiClientId -Action "set identifier URI $identifierUri"

    # Expose the access_as_user scope via the api.oauth2PermissionScopes patch.
    $scopeId = [guid]::NewGuid().ToString()
    $apiBody = @{
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
    $apiBodyJson = ($apiBody | ConvertTo-Json -Depth 6 -Compress)
    # az ad app update --set api=... requires a JSON value; write to a temp file
    # to avoid shell-quoting issues with native arg parsing.
    if ($PSCmdlet.ShouldProcess($apiClientId, 'expose access_as_user scope')) {
        $tmp = New-TemporaryFile
        try {
            Set-Content -LiteralPath $tmp -Value $apiBodyJson -Encoding UTF8
            Invoke-Native -Exe 'az' -ArgList @('rest', '--method', 'PATCH',
                '--url', "https://graph.microsoft.com/v1.0/applications(appId='$apiClientId')",
                '--headers', 'Content-Type=application/json',
                '--body', "@$tmp", '-o', 'none') -Target $apiClientId -Action 'patch API scope'
        }
        finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
    else {
        Write-Host "    DRYRUN  PATCH applications(appId='$apiClientId') api.oauth2PermissionScopes += access_as_user" -ForegroundColor DarkGray
    }

    # API client secret (for the OBO Graph directory search). Stored as an App
    # Service application setting later is out of scope for the deploy workflow;
    # we surface it for the operator to add (AzureAd__ClientCredentials).
    $apiSecret = Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'credential', 'reset', '--id', $apiClientId, '--append', '--display-name', 'anchor-obo', '--query', 'password', '-o', 'tsv') -Capture `
        -Target $apiClientId -Action 'create API client secret'
    if ($apiSecret) {
        Write-Manual "API client secret created. Add it to the App Service as AzureAd__ClientCredentials__0__SourceType=ClientSecret and AzureAd__ClientCredentials__0__ClientSecret=<value> (needed for the user-directory search OBO call). Value is printed once below."
        Write-Host "    AzureAd__ClientCredentials secret: $apiSecret" -ForegroundColor DarkYellow
    }

    # ── Dashboard SPA app registration ───────────────────────────────────────
    Write-Step "Entra app registration (dashboard SPA) — $SpaAppName"

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

    # The SPA redirect URI is filled in after the deploy (we need the SWA URL),
    # so it is set in Step 5 once $swaUrl is known. Here, grant the baseline
    # Graph User.Read delegated permission so sign-in works.
    Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'permission', 'add', '--id', $spaClientId,
        '--api', $GraphAppId, '--api-permissions', "$GraphUserRead=Scope", '-o', 'none') `
        -Target $spaClientId -Action 'grant Graph User.Read to SPA'
}

# ── Step 4: deploy Bicep ─────────────────────────────────────────────────────

Write-Step "Deploy infra — $BicepFile"

$deployArgs = @(
    'deployment', 'group', 'create',
    '--resource-group', $ResourceGroup,
    '--template-file', $BicepFile,
    '--parameters',
    "uniqueSuffix=$UniqueSuffix",
    "entraTenantId=$tenantId"
)
if ($apiClientId) { $deployArgs += "entraClientId=$apiClientId" }
if ($sqlPwPlain) { $deployArgs += "sqlAdminPassword=$sqlPwPlain" }
$deployArgs += @('--query', 'properties.outputs', '-o', 'json')

$outputsJson = Invoke-Native -Exe 'az' -ArgList $deployArgs `
    -Target $ResourceGroup -Action 'deploy Bicep template' -Capture

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
# against the API scope (idempotent: az overwrites the redirect set).
if (-not $SkipEntra -and $spaClientId) {
    Write-Step 'Entra SPA — redirect URI + API pre-authorization'
    $redirect = "$swaUrl/"
    Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'update', '--id', $spaClientId,
        '--web-redirect-uris', $redirect, '-o', 'none') `
        -Target $spaClientId -Action "set SPA redirect URI $redirect"
    # Request the API's access_as_user scope from the SPA.
    if ($apiClientId -and $outEntraAudience -ne 'api://') {
        Invoke-Native -Exe 'az' -ArgList @('ad', 'app', 'permission', 'add', '--id', $spaClientId,
            '--api', $apiClientId, '--api-permissions', "$scopeId=Scope", '-o', 'none') `
            -Target $spaClientId -Action 'grant API access_as_user to SPA' -ErrorAction SilentlyContinue 2>$null
    }
}

# ── Step 6: deployment secrets (SWA token + publish profile) ──────────────────

Write-Step 'Fetch deployment credentials'

# These are reads, not mutations, but they only succeed once the SWA / App
# Service actually exist (i.e. after a real deploy). In -WhatIf there is no
# deployed resource, so skip the live reads and use placeholders so the GitHub
# wiring plan still prints.
if ($WhatIfPreference) {
    Write-Host '    DRYRUN  (skipping live credential reads; resources not deployed)' -ForegroundColor DarkGray
    $swaToken = '<swa-deployment-token>'
    $publishProfile = '<publish-profile-xml>'
}
else {
    $swaToken = Invoke-Native -Exe 'az' -ArgList @('staticwebapp', 'secrets', 'list',
        '--name', $staticWebAppName, '--query', 'properties.apiKey', '-o', 'tsv') -Capture -ReadOnly
    if (-not $swaToken) { $swaToken = '<swa-deployment-token>' }

    $publishProfile = Invoke-Native -Exe 'az' -ArgList @('webapp', 'deployment', 'list-publishing-profiles',
        '--name', $appServiceName, '--resource-group', $ResourceGroup, '--xml') -Capture -ReadOnly
    if (-not $publishProfile) { $publishProfile = '<publish-profile-xml>' }
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
