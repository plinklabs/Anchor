#requires -Version 5.1
<#
.SYNOPSIS
    Substitute the per-deployment #{TOKEN}# placeholders in the agent's
    appsettings.Production.json template at Velopack pack time (#209).

.DESCRIPTION
    appsettings.Production.json is committed as a TEMPLATE (#203): its
    Backend:BaseUrl + Auth values are `#{TOKEN}#` placeholders so the committed
    repo never hardcodes a single fork's backend/Entra. The release pipeline
    runs this script against the *published* copy of that file (next to the
    built exe) to bake in the deploying fork's values before `vpk pack`, so the
    shipped agent targets the right backend the moment it's installed — with no
    source edit and no runtime config server.

    Each placeholder is filled from an environment variable of the SAME name as
    the token (e.g. `#{BACKEND_BASE_URL}#` <- $env:BACKEND_BASE_URL). This is
    the agent-side mirror of the dashboard's `--dart-define` substitution
    (dashboard-deploy.yml): non-secret per-deployment config sourced from CI
    `vars.*`, secrets from `secrets.*`.

    The script is STRICT by design: after substitution it re-scans the file and
    fails if any `#{...}#` token remains (a typo or a missing variable would
    otherwise ship a literal placeholder as a live backend URL). It also
    re-parses the result as JSON so a substituted value containing a quote or
    backslash can't produce a broken config that the agent would only choke on
    at first launch on a student's machine.

.PARAMETER Path
    Path to the appsettings.Production.json to rewrite IN PLACE. In the release
    build this is the published copy next to the exe, never the committed
    template (the template must keep its placeholders for the next build and for
    the substitution unit test).

.PARAMETER Values
    Optional hashtable of token-name -> value. When supplied it takes precedence
    over environment variables for that token; tokens absent from the hashtable
    still fall back to the matching env var. Primarily a testing seam so the
    unit test can drive substitution deterministically without mutating
    process-wide environment state.

.PARAMETER OptionalTokens
    Names of tokens that may legitimately be empty/whitespace (e.g. a blank
    LoginHint). Every OTHER token is REQUIRED: a missing OR blank value fails the
    build. This is the #247 hardening — the release that shipped a crashing agent
    did so because all four required tokens resolved to "" (the CI variables were
    misnamed), and an empty string used to count as a legitimate value, so the
    pipeline baked `""` into a live config and shipped it. Treating blank as
    missing for required tokens turns that silent, install-time-only failure into
    a loud build failure. The template currently declares no optional tokens
    (LoginHint is hardcoded "" in the template, not a placeholder), so this
    defaults to empty.

.EXAMPLE
    $env:BACKEND_BASE_URL = 'https://anchor-api-arcadia.azurewebsites.net'
    $env:AUTH_TENANT_ID   = '...'; $env:AUTH_CLIENT_ID = '...'; $env:AUTH_SCOPE = 'api://.../.default'
    ./substitute-config.ps1 -Path ./publish/appsettings.Production.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    [hashtable] $Values,

    [string[]] $OptionalTokens = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) {
    throw "Config template not found: $Path"
}

# Read as UTF-8 explicitly. Windows PowerShell 5.1's Get-Content default uses
# the system codepage, which would corrupt the non-ASCII characters in the
# template's `//` comment (em-dashes) on round-trip.
$content = [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))

# Discover every distinct #{TOKEN}# placeholder actually present in the file, so
# the script's contract is "fill exactly what the template declares" rather than
# a hardcoded list that could silently drift from appsettings.Production.json.
$tokenRegex = [regex] '#\{(?<name>[A-Za-z0-9_]+)\}#'
# @(...) keeps this an array even when 0 or 1 tokens match, so the StrictMode
# .Count / enumeration below is safe on a fully-substituted or token-less file.
$tokens = @($tokenRegex.Matches($content) |
    ForEach-Object { $_.Groups['name'].Value } |
    Sort-Object -Unique)

$missing = @()
foreach ($token in $tokens) {
    $value = $null
    if ($Values -and $Values.ContainsKey($token)) {
        $value = [string] $Values[$token]
    }
    else {
        $envValue = [Environment]::GetEnvironmentVariable($token)
        if ($null -ne $envValue) { $value = $envValue }
    }

    # A required token must have a non-blank value. An *unset* source is missing
    # as before; #247 additionally treats a blank (empty/whitespace) value as
    # missing for required tokens, because a misnamed CI variable resolves to ""
    # and would otherwise be baked in as a live (broken) backend URL/auth value
    # and shipped — the exact failure that crashed agent-v0.1.0 on launch. A
    # token listed in -OptionalTokens keeps the old "blank is legitimate" carve-
    # out (e.g. a deliberately empty LoginHint).
    $isOptional = $OptionalTokens -contains $token
    $isBlank = ($null -ne $value) -and [string]::IsNullOrWhiteSpace($value)
    if ($null -eq $value -or ($isBlank -and -not $isOptional)) {
        $missing += $token
        continue
    }

    $content = $content.Replace("#{$token}#", $value)
}

if ($missing.Count -gt 0) {
    throw "No value supplied for required placeholder(s): $($missing -join ', '). " +
          "Set the matching environment variable (or pass -Values) to a non-blank " +
          "value for each (or list a genuinely-optional token in -OptionalTokens)."
}

# Belt-and-braces: no real placeholder may survive. We re-scan for the same
# `#{NAME}#` token shape we substitute (a valid token always has that shape), so
# this catches a token the loop somehow missed without false-positiving on prose
# in the template's `//` comment that mentions `#{...}#` literally. Shipping a
# live `#{NAME}#` as a backend URL would be a silent, install-time-only failure.
$leftover = $tokenRegex.Matches($content)
if ($leftover.Count -gt 0) {
    $sample = ($leftover | ForEach-Object { $_.Value } | Sort-Object -Unique) -join ', '
    throw "Placeholder(s) still present after substitution: $sample."
}

# A substituted value could contain a character that breaks JSON. Fail now
# (in CI) rather than at the student's first launch.
try {
    $null = $content | ConvertFrom-Json
}
catch {
    throw "Substituted config is not valid JSON: $($_.Exception.Message)"
}

# Write UTF-8 WITHOUT a BOM: a leading BOM is legal-but-awkward for some JSON
# parsers and isn't present in the committed template, so keep the round-trip
# byte-faithful aside from the substitutions.
[System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))

Write-Host "Substituted $($tokens.Count) placeholder(s) in $Path."
