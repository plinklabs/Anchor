#Requires -Version 7.0

<#
.SYNOPSIS
    Run setup.ps1's deploy-identity helpers against a stub az, in a dry run
    and for real, and check what they print and which commands they issue.

.DESCRIPTION
    `setup.ps1 -WhatIf` must print the whole plan without touching Azure. Two
    of its helpers write a JSON body to a temporary file before they call az:
    Set-DeployRole (the deploy identity's role assignments) and
    Add-DeployFederatedCredential (its OIDC federated credential). Under
    -WhatIf, New-TemporaryFile creates nothing and returns $null, so a helper
    that creates the file before it checks ShouldProcess stops the dry run at
    Set-Content (#504, #528).

    The whole script can't run here: it needs PwshSpectreConsole and a signed-in
    az and gh. So this takes the helpers' definitions out of setup.ps1 with the
    PowerShell parser, replaces `az` with a stub function that records each call
    and answers the read-only lookups, and calls the helpers with
    $WhatIfPreference set the way `setup.ps1 -WhatIf` sets it. Scripts CI runs
    it; run it locally the same way:

        pwsh scripts/ci/test-setup-dry-run.ps1

.EXAMPLE
    pwsh scripts/ci/test-setup-dry-run.ps1

    Prints ok or FAIL per case, and exits 1 if any case failed.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setupPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'setup.ps1'
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($setupPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors) { throw "setup.ps1 does not parse: $($parseErrors[0])" }

# The helpers under test and the setup.ps1 functions they call.
foreach ($name in 'Invoke-Native', 'Invoke-AzRead', 'Test-Prop', 'Set-DeployRole', 'Add-DeployFederatedCredential') {
    $fn = $ast.Find({
            param($n)
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
        }, $true)
    if (-not $fn) { throw "setup.ps1 defines no function $name; update this test" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

# setup.ps1's Write-Manual renders through PwshSpectreConsole; record instead.
$script:Manual = [System.Collections.Generic.List[string]]::new()
function Write-Manual { param([string]$Message) $script:Manual.Add($Message) }

# Stub az: records every call, with the text of each @file argument (read
# during the call, because the helpers delete the file right after it), and
# answers the lookups the helpers make.
$script:AzCalls = [System.Collections.Generic.List[object]]::new()
$script:FederatedCredentials = '[]'
function az {
    $files = @{}
    foreach ($a in $args) {
        if ("$a".StartsWith('@')) {
            $path = "$a".Substring(1)
            $files[$path] = Get-Content -Raw -LiteralPath $path
        }
    }
    $line = $args -join ' '
    $script:AzCalls.Add([pscustomobject]@{ Line = $line; Files = $files })
    $global:LASTEXITCODE = 0
    switch -Wildcard ($line) {
        'ad sp show *' { 'sp-object-id' }
        'role assignment list *' { '[]' }
        'ad app federated-credential list *' { $script:FederatedCredentials }
    }
}

function Find-AzCall([string]$Pattern) {
    @($script:AzCalls | Where-Object { $_.Line -like "*$Pattern*" })
}

# Call a helper with $WhatIfPreference as `setup.ps1 -WhatIf` (or a real run)
# leaves it, and return what it printed and any error it threw.
function Invoke-Helper {
    param([scriptblock]$Call, [bool]$DryRun)
    $WhatIfPreference = $DryRun
    $script:AzCalls.Clear()
    $run = @{ Output = ''; Error = $null }
    try { $run.Output = & $Call 6>&1 | Out-String }
    catch { $run.Error = $_ }
    $run
}

$script:Failed = 0
$script:Cases = 0
function Complete-Case {
    param([string]$Name, [object[]]$Problems, [hashtable]$Run)
    $script:Cases++
    if (-not $Problems) {
        Write-Output "ok   $Name"
        return
    }
    $script:Failed++
    Write-Output "FAIL $Name"
    $Problems | ForEach-Object { Write-Output "       $_" }
    if ($Run.Error) { Write-Output "       error: $($Run.Error)" }
    Write-Output "       output: $($Run.Output.Trim())"
    Write-Output "       az calls: $(($script:AzCalls | ForEach-Object Line) -join ' | ')"
}

$appId = '11111111-2222-3333-4444-555555555555'
$repo = 'someone/anchor-fork'
$subject = "repo:${repo}:environment:production"

# 1. The dry run reaches the create and prints it (#528).
$script:FederatedCredentials = '[]'
$run = Invoke-Helper -DryRun $true { Add-DeployFederatedCredential -AppId $appId -Repo $repo }
Complete-Case 'federated credential, dry run: prints the create (#528)' -Run $run -Problems @(
    if ($run.Error) { 'threw instead of printing the plan' }
    if ($run.Output -notmatch 'DRYRUN  az ad app federated-credential create') { 'printed no DRYRUN line for the create' }
    if (Find-AzCall 'federated-credential create') { 'ran az ad app federated-credential create under -WhatIf' }
)

# 2. A real run creates the credential from a parameters file, then deletes it.
$script:FederatedCredentials = '[]'
$run = Invoke-Helper -DryRun $false { Add-DeployFederatedCredential -AppId $appId -Repo $repo }
$create = Find-AzCall 'ad app federated-credential create'
Complete-Case 'federated credential, real run: creates it from a parameters file' -Run $run -Problems @(
    if ($run.Error) { 'threw' }
    if ($create.Count -ne 1) { "expected 1 az ad app federated-credential create, got $($create.Count)" }
    else {
        $call = $create[0]
        if ($call.Line -notlike "*--id $appId *") { "create does not target --id $appId" }
        if ($call.Files.Count -ne 1) { 'create was not given one @parameters file' }
        else {
            $fic = @($call.Files.Values)[0] | ConvertFrom-Json
            if ($fic.subject -ne $subject) { "subject is '$($fic.subject)', expected '$subject'" }
            if ($fic.issuer -ne 'https://token.actions.githubusercontent.com') { "issuer is '$($fic.issuer)'" }
            if (@($fic.audiences) -join ',' -ne 'api://AzureADTokenExchange') { "audiences are '$($fic.audiences)'" }
            if ($fic.name -ne 'github-anchor-production') { "name is '$($fic.name)'" }
            if (Test-Path -LiteralPath @($call.Files.Keys)[0]) { 'left the parameters file behind' }
        }
    }
)

# 3. A credential with the subject already there: nothing to create.
$script:FederatedCredentials = ConvertTo-Json -Compress @(@{ name = 'github-anchor-production'; subject = $subject })
$run = Invoke-Helper -DryRun $false { Add-DeployFederatedCredential -AppId $appId -Repo $repo }
Complete-Case 'federated credential, already there: skips it' -Run $run -Problems @(
    if ($run.Error) { 'threw' }
    if (Find-AzCall 'federated-credential create') { 'created a second credential' }
    if ($run.Output -notmatch 'already exists') { 'did not say the credential exists' }
)

# 4. The role assignment's dry run prints the PUT (#504).
$run = Invoke-Helper -DryRun $true {
    Set-DeployRole -AppId $appId -SubscriptionId 'sub-id' -ResourceGroupName 'anchor-rg' `
        -ResourceType 'Microsoft.Sql/servers' -ResourceName 'anchor-sql-test' `
        -RoleName 'SQL Server Contributor' -RoleDefinitionId '6d8ee4ec-f05a-4a1d-8b00-a9b17e38b437'
}
Complete-Case 'deploy role, dry run: prints the assignment (#504)' -Run $run -Problems @(
    if ($run.Error) { 'threw instead of printing the plan' }
    if ($run.Output -notmatch 'DRYRUN  PUT .*/Microsoft.Sql/servers/anchor-sql-test/') { 'printed no DRYRUN line for the PUT' }
    if (Find-AzCall 'rest') { 'ran az rest under -WhatIf' }
)

if ($script:Failed) {
    Write-Output "$($script:Failed) of $($script:Cases) setup.ps1 dry-run cases failed."
    exit 1
}
Write-Output "All $($script:Cases) setup.ps1 dry-run cases passed."
exit 0
