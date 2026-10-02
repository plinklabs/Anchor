#Requires -Version 5.1

<#
.SYNOPSIS
    Check every tracked PowerShell file for unrecognized or incomplete
    comment-based help, non-ASCII text without a UTF-8 BOM, and parse errors.

.DESCRIPTION
    scripts-ci.yml runs this on every pull request that touches a PowerShell
    file (#363). It is the whole check, so running it locally gives the same
    result as CI:

        pwsh scripts/ci/check-ps-scripts.ps1

    It reads every *.ps1, *.psm1 and *.psd1 file that git tracks (or only the
    files given with -Path) and reports a problem when:

    - Encoding: the file has a byte of 0x80 or higher but doesn't start with
      a UTF-8 BOM (EF BB BF). Windows PowerShell 5.1 reads a file without a
      BOM as Windows-1252, so its non-ASCII text turns into mojibake, and a
      byte such as an arrow's 0x92 or an em dash's 0x94 can end a string
      early and break the parse (#380). Save the file as UTF-8 with BOM, or
      keep it ASCII. A Linux runner can't run 5.1, so this rule stands in
      for a 5.1 parse.
    - Parse: PowerShell's parser reports an error in the file.
    - Help (*.ps1 files without parse errors): the script's comment-based
      help, as Get-Help reads it, must exist and have a non-blank synopsis
      and description. Every param() parameter needs a non-blank .PARAMETER
      entry, and every .PARAMETER entry must name a declared parameter.
      PowerShell ignores the whole help block when a comment such as
      #Requires sits on the line directly above it (#349). Stacked or
      misspelled .PARAMETER lines leave parameters without help (#362). The
      check reads the parser's GetHelpContent(), because Get-Help's synopsis
      is never empty: without help it shows the syntax.

    Under GitHub Actions each problem is printed as an error annotation on
    its file and line. The script exits 1 if it found any problem.

.PARAMETER Path
    Files to check instead of every tracked PowerShell file, for example a
    new script that git doesn't track yet. The same rules apply.

.EXAMPLE
    pwsh scripts/ci/check-ps-scripts.ps1

    Checks every tracked PowerShell file in the repo, from any directory.

.EXAMPLE
    pwsh scripts/ci/check-ps-scripts.ps1 -Path scripts/dev/my-new-script.ps1

    Checks one file.
#>
[CmdletBinding()]
param(
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = git -C $PSScriptRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or -not $repoRoot) {
    throw "$PSScriptRoot is not inside a git work tree"
}
$repoRoot = [System.IO.Path]::GetFullPath($repoRoot)

# Repo-relative path with forward slashes, as GitHub annotations expect.
function Get-DisplayPath([string] $FullPath) {
    $prefix = $repoRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($FullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $FullPath = $FullPath.Substring($prefix.Length)
    }
    $FullPath.Replace('\', '/')
}

if ($Path) {
    $files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).ProviderPath })
} else {
    # -z: no quoting of unusual names. Filter here rather than with a git
    # pathspec so the extension match is case-insensitive on every OS.
    $files = @((git -C $repoRoot ls-files -z) -split "`0" |
            Where-Object { $_ -match '\.ps[dm]?1$' } |
            ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $repoRoot $_)) } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
    # The repo has PowerShell scripts, so finding none means the listing broke,
    # not that there is nothing to check.
    if (-not $files) { throw "found no tracked PowerShell files under $repoRoot" }
}

$problems = New-Object System.Collections.Generic.List[string]
$problemFiles = New-Object System.Collections.Generic.HashSet[string]

function Add-Problem([string] $File, [int] $Line, [string] $Message) {
    [void] $problemFiles.Add($File)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $escaped = $Message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        $problems.Add("::error file=$File,line=$Line::$escaped")
    } else {
        $problems.Add("${File}:${Line}: $Message")
    }
}

# 1-based line number of a character offset in $Text.
function Get-LineNumber([string] $Text, [int] $Index) {
    ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count + 1
}

$latin1 = [System.Text.Encoding]::GetEncoding(28591)  # one char per byte
$helpChecked = 0

foreach ($full in $files) {
    $rel = Get-DisplayPath $full

    # Encoding.
    $bytes = [System.IO.File]::ReadAllBytes($full)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (-not $hasBom) {
        $raw = $latin1.GetString($bytes)
        $nonAscii = [regex]::Match($raw, '[\x80-\xFF]')
        if ($nonAscii.Success) {
            Add-Problem $rel (Get-LineNumber $raw $nonAscii.Index) (
                'has non-ASCII text but no UTF-8 BOM, so Windows PowerShell 5.1 reads it as ' +
                'Windows-1252 and can misparse it. Save it as UTF-8 with BOM, or make it ASCII.')
        }
    }

    # Parse.
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref] $tokens, [ref] $errors)
    foreach ($err in $errors) {
        Add-Problem $rel $err.Extent.StartLineNumber "parse error: $($err.Message)"
    }

    # Help. Module (.psm1) help is per function, and .psd1 is data.
    if ($full -notmatch '\.ps1$' -or $errors) { continue }
    $helpChecked++
    $text = $ast.Extent.Text
    $synopsis = [regex]::Match($text, '(?im)^[ \t]*#?[ \t]*\.SYNOPSIS\b')
    $synopsisLine = 1
    if ($synopsis.Success) { $synopsisLine = Get-LineNumber $text $synopsis.Index }

    $help = $ast.GetHelpContent()
    if (-not $help) {
        if ($synopsis.Success) {
            Add-Problem $rel $synopsisLine (
                'has a .SYNOPSIS, but PowerShell does not recognize its comment-based help, so ' +
                'Get-Help shows only the syntax. Is a comment (such as #Requires) on the line ' +
                'directly above <#? Keep a blank line between them.')
        } else {
            Add-Problem $rel 1 (
                'has no comment-based help. Add a <# ... #> block with .SYNOPSIS, .DESCRIPTION ' +
                'and a .PARAMETER per parameter, like the other scripts.')
        }
        continue
    }
    if ([string]::IsNullOrWhiteSpace($help.Synopsis)) {
        Add-Problem $rel $synopsisLine 'comment-based help has an empty .SYNOPSIS.'
    }
    if ([string]::IsNullOrWhiteSpace($help.Description)) {
        Add-Problem $rel $synopsisLine 'comment-based help has no .DESCRIPTION text.'
    }

    # PowerShell hashtables compare keys case-insensitively, like Get-Help.
    $documented = @{}
    foreach ($entry in $help.Parameters.GetEnumerator()) { $documented[$entry.Key] = $entry.Value }
    $declared = @()
    if ($ast.ParamBlock) { $declared = @($ast.ParamBlock.Parameters) }

    foreach ($param in $declared) {
        $name = $param.Name.VariablePath.UserPath
        if (-not $documented.ContainsKey($name)) {
            Add-Problem $rel $param.Extent.StartLineNumber "parameter -$name has no .PARAMETER entry in the help."
        } elseif ([string]::IsNullOrWhiteSpace($documented[$name])) {
            Add-Problem $rel $param.Extent.StartLineNumber (
                "parameter -$name has an empty .PARAMETER entry. Give each parameter its own " +
                'entry with text; stacked .PARAMETER lines leave all but the last one empty.')
        }
    }

    $declaredNames = @($declared | ForEach-Object { $_.Name.VariablePath.UserPath })
    foreach ($name in $documented.Keys) {
        if ($declaredNames -contains $name) { continue }
        $line = $synopsisLine
        $shown = $name
        $entry = [regex]::Match($text, '(?im)^[ \t]*#?[ \t]*\.PARAMETER[ \t]+(' + [regex]::Escape($name) + ')[ \t]*\r?$')
        if ($entry.Success) {
            $line = Get-LineNumber $text $entry.Index
            $shown = $entry.Groups[1].Value
        }
        Add-Problem $rel $line ".PARAMETER $shown names no parameter of the script's param() block."
    }
}

$problems | ForEach-Object { Write-Output $_ }
$summary = "Checked $($files.Count) PowerShell file(s), $helpChecked of them for help"
if ($problems.Count) {
    Write-Output "${summary}: $($problems.Count) problem(s) in $($problemFiles.Count) file(s)."
    exit 1
}
Write-Output "${summary}: no problems."
exit 0
