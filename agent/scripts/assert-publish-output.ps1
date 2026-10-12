#requires -Version 5.1

<#
.SYNOPSIS
    Fail the release build when the agent's publish folder lacks
    FocusAgent.App.pri, without which the installed agent can't start (#403).

.DESCRIPTION
    pack-release.ps1 runs this against its publish folder before `vpk pack`.

    FocusAgent.App.pri, next to the exe, holds the compiled XAML (.xbf) of
    every agent window and the localized .resw strings. Without it the first
    window's InitializeComponent throws a XamlParseException and the agent
    dies on startup. agent-v0.2.0 and agent-v0.2.1 shipped that way: the
    unpackaged `dotnet publish` left the PRI out (until FocusAgent.App.csproj
    set EnableMsixTooling), and nothing looked at the published folder. The
    e2e suite tests a Debug build, which always had the file.

    The check fails when FocusAgent.App.pri is missing or empty, so a broken
    publish fails the build instead of shipping.

.PARAMETER PublishDir
    The folder `vpk pack` packages: pack-release.ps1's artifacts/publish.

.EXAMPLE
    ./assert-publish-output.ps1 -PublishDir ../artifacts/publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish folder not found: $PublishDir"
}

$priName = 'FocusAgent.App.pri'
$priPath = Join-Path $PublishDir $priName

if (-not (Test-Path -LiteralPath $priPath -PathType Leaf)) {
    throw "$priName is missing from the publish folder $PublishDir. It holds " +
          "the agent's compiled XAML and strings; without it the installed " +
          "agent crashes on startup (#403). Check that FocusAgent.App.csproj " +
          "still sets <EnableMsixTooling>true</EnableMsixTooling>."
}

$priSize = (Get-Item -LiteralPath $priPath).Length
if ($priSize -eq 0) {
    throw "$priName in the publish folder $PublishDir is empty; the installed " +
          "agent would crash on startup (#403)."
}

Write-Host "Publish output OK: $priName present ($priSize bytes)."
