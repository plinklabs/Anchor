#Requires -Version 5.1

<#
.SYNOPSIS
    Self-test for check-ps-scripts.ps1: every rule must flag a file that
    breaks it, and a correct file must pass.

.DESCRIPTION
    scripts-ci.yml runs this before the real check (#363), so a change that
    makes the checker pass everything fails CI instead of going unnoticed.
    It writes one small fixture script per case to a temporary folder, runs
    the checker on it with -Path, and compares the exit code and message.
    It removes the folder afterwards. Run it locally the same way:

        pwsh scripts/ci/test-check-ps-scripts.ps1

.EXAMPLE
    pwsh scripts/ci/test-check-ps-scripts.ps1

    Prints ok or FAIL per case, and exits 1 if any case failed.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$checker = Join-Path $PSScriptRoot 'check-ps-scripts.ps1'
# Plain "file:line: message" lines: the fixtures' expected problems must not
# show up as error annotations on the CI run.
$env:GITHUB_ACTIONS = ''

$good = @'
#Requires -Version 5.1

<#
.SYNOPSIS
    Fixture for the check-ps-scripts.ps1 self-test.

.DESCRIPTION
    Prints a name.

.PARAMETER Name
    The name to print.
#>
param([string] $Name)
Write-Output $Name
'@ -replace "`r`n", "`n"

$emDash = [string][char]0x2014
$twoParams = 'param([string] $Name, [int] $Count)'

# Expect = $null: the checker must pass. Otherwise it must exit 1 and print
# Expect, a piece of the message for that rule.
$cases = @(
    @{ Name = 'correct script'; Text = $good; Bom = $false; Expect = $null }
    @{ Name = 'non-ASCII script with BOM'; Bom = $true; Expect = $null
        Text = $good.Replace('Prints a name.', "Prints a name $emDash any name.") }
    @{ Name = 'no help'; Bom = $false; Expect = 'has no comment-based help'
        Text = "param([string] `$Name)`nWrite-Output `$Name`n" }
    @{ Name = '#Requires directly above <#'; Bom = $false; Expect = 'does not recognize its comment-based help'
        Text = $good.Replace("5.1`n`n<#", "5.1`n<#") }
    @{ Name = 'parameter without .PARAMETER'; Bom = $false; Expect = 'parameter -Count has no .PARAMETER entry'
        Text = $good.Replace('param([string] $Name)', $twoParams) }
    @{ Name = '.PARAMETER for no parameter'; Bom = $false; Expect = '.PARAMETER Gone names no parameter'
        Text = $good.Replace("`n#>", "`n`n.PARAMETER Gone`n    Removed.`n#>") }
    @{ Name = 'stacked .PARAMETER lines'; Bom = $false; Expect = 'parameter -Count has an empty .PARAMETER entry'
        Text = $good.Replace('param([string] $Name)', $twoParams).Replace('.PARAMETER Name', ".PARAMETER Count`n.PARAMETER Name") }
    @{ Name = 'blank .SYNOPSIS'; Bom = $false; Expect = 'empty .SYNOPSIS'
        Text = $good.Replace("    Fixture for the check-ps-scripts.ps1 self-test.`n", '') }
    @{ Name = 'blank .DESCRIPTION'; Bom = $false; Expect = 'no .DESCRIPTION text'
        Text = $good.Replace("    Prints a name.`n", '') }
    @{ Name = 'non-ASCII without BOM'; Bom = $false; Expect = 'no UTF-8 BOM'
        Text = $good.Replace('Prints a name.', "Prints a name $emDash any name.") }
    @{ Name = 'parse error'; Bom = $false; Expect = 'parse error'
        Text = $good + "if (`$true) {`n" }
)

$utf8 = New-Object System.Text.UTF8Encoding($false)
$dir = Join-Path ([System.IO.Path]::GetTempPath()) ('check-ps-scripts-test-' + [guid]::NewGuid())
[void] (New-Item -ItemType Directory -Path $dir)
$failed = 0
try {
    for ($i = 0; $i -lt $cases.Count; $i++) {
        $case = $cases[$i]
        if ($case.Name -ne 'correct script' -and $case.Text -eq $good) {
            throw "case '$($case.Name)' did not change the fixture; fix its Replace()"
        }
        $bytes = $utf8.GetBytes($case.Text)
        if ($case.Bom) { $bytes = [byte[]](0xEF, 0xBB, 0xBF) + $bytes }
        $file = Join-Path $dir ('fixture-{0:d2}.ps1' -f $i)
        [System.IO.File]::WriteAllBytes($file, $bytes)

        $out = & $checker -Path $file | Out-String
        $code = $LASTEXITCODE
        if ($case.Expect) {
            $ok = $code -eq 1 -and $out.Contains($case.Expect)
        } else {
            $ok = $code -eq 0
        }
        if ($ok) {
            Write-Output "ok   $($case.Name)"
        } else {
            $failed++
            Write-Output "FAIL $($case.Name): exit $code, expected $(if ($case.Expect) { "1 and '$($case.Expect)'" } else { '0' }). Output:"
            Write-Output $out
        }
    }
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force
}

if ($failed) {
    Write-Output "$failed of $($cases.Count) self-test cases failed."
    exit 1
}
Write-Output "All $($cases.Count) self-test cases passed."
exit 0
