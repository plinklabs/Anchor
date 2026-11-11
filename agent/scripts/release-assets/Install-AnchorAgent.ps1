#Requires -Version 5.1

<#
.SYNOPSIS
    Intune install command for the Anchor agent: install it silently if it
    isn't installed yet, then start it right away (#457).

.DESCRIPTION
    Intune runs this script as the install command of the agent's Win32 app,
    in user context, from the folder it unpacked the .intunewin into. That
    package holds this script and Anchor.Agent-win-Setup.exe side by side;
    both are assets of every agent release. Install command:

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1

    What it does, in order:

    1. Stops with exit code 13 when it runs as SYSTEM. Velopack installs per
       user, into %LOCALAPPDATA%\Anchor.Agent, so in system context the agent
       would land in the SYSTEM profile, out of the student's reach. The app's
       install behavior must be User.
    2. If the agent is already installed (current\FocusAgent.App.exe exists
       in %LOCALAPPDATA%\Anchor.Agent), skips Setup.exe. A silent Setup.exe
       over an installed agent stops it, also in the middle of a lesson, and
       doesn't start it again. Updates are the agent's own job (#449, #456).
    3. Otherwise runs Setup.exe --silent and waits for it. When Setup.exe
       fails, the script copies the end of Setup.exe's log into its own and
       exits with Setup.exe's exit code.
    4. Starts the agent, unless it's already running, as the signed-in user,
       with the command Windows runs at sign-in: the AnchorFocusAgent value
       under HKCU\Software\Microsoft\Windows\CurrentVersion\Run, which the
       agent's install hook writes (#225). A silent Setup.exe doesn't start
       the agent, and the next sign-in can be weeks away on a laptop that
       sleeps instead of restarting. Then checks that the agent is still
       running a few seconds later.

    The agent is started through WMI (Win32_Process.Create), so it is not a
    child of this script: it shares no job object and no handles with
    whatever runs the install command, and the Intune Management Extension
    can't wait on it or stop it along with the install. Where WMI can't
    start processes (for example when the Defender attack surface reduction
    rule that blocks process creation from WMI is on), the script logs that
    and starts the agent as its own child instead.

    Log: every step goes, with a timestamp, to
    %LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log, next to the
    agent's own logs, and Setup.exe writes its log to intune-setup.log in
    the same folder. Not under %LOCALAPPDATA%\Anchor.Agent: Velopack's Setup
    treats a folder there that isn't empty as an existing install.

    Exit codes. Intune counts any code other than 0 as a failed install, so
    none of these needs to be added to the app's return codes.

        0   The agent is installed and running.
        1   Setup.exe failed: Velopack's Setup exits with 1 on any failure,
            and intune-setup.log says why. Any other code Setup.exe exits
            with is passed on unchanged.
        10  Anchor.Agent-win-Setup.exe isn't in the script's folder.
        11  Setup.exe exited with 0, but the agent isn't installed.
        12  The agent is installed, but there is no AnchorFocusAgent Run
            value to start it with (it won't start at sign-in either).
        13  The script runs as SYSTEM: set the app's install behavior to User.
        14  The agent couldn't be started, or stopped right after it started.
        15  Unexpected error; the log has the details.

.PARAMETER SetupPath
    The Velopack installer to run when the agent isn't installed. Defaults
    to Anchor.Agent-win-Setup.exe in the script's own folder, where it sits
    in the .intunewin package.

.PARAMETER InstallRoot
    The agent's install folder: it counts as installed when
    current\FocusAgent.App.exe exists in it. Defaults to
    %LOCALAPPDATA%\Anchor.Agent, where Setup.exe installs it. Only change it
    to test the script against a stand-in install.

.PARAMETER LogPath
    The script's log file. Defaults to
    %LOCALAPPDATA%\Anchor\FocusAgent\logs\intune-install.log. Setup.exe logs
    to intune-setup.log in the same folder.

.PARAMETER RunKeyPath
    The registry key that holds the agent's sign-in command, the
    AnchorFocusAgent value. Defaults to
    HKCU:\Software\Microsoft\Windows\CurrentVersion\Run, where the agent's
    install hook writes it. Only change it to test the script against a
    stand-in install.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AnchorAgent.ps1

    The Intune install command: installs the agent if needed and starts it.
#>
[CmdletBinding()]
param(
    [string] $SetupPath,
    [string] $InstallRoot,
    [string] $LogPath,
    [string] $RunKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What the agent and its installer use. FocusAgent.Core.Tests pins these
# against the agent's own constants (StartupRunKey, AgentLogPaths) and the
# pack id in pack-release.ps1, so they can't drift apart.
$RunValueName = 'AnchorFocusAgent'
$PackId = 'Anchor.Agent'
$MainExe = 'FocusAgent.App.exe'
$SetupFileName = 'Anchor.Agent-win-Setup.exe'
$AgentLogFolder = 'Anchor\FocusAgent\logs'

# How long the agent must stay up after the start to count as running.
$StayUpSeconds = 5

# Velopack, the agent and its logs all use the known LocalAppData folder (not
# the LOCALAPPDATA environment variable, which a process can change).
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if (-not $SetupPath) { $SetupPath = Join-Path $PSScriptRoot $SetupFileName }
if (-not $InstallRoot) { $InstallRoot = Join-Path $localAppData $PackId }
if (-not $LogPath) { $LogPath = Join-Path (Join-Path $localAppData $AgentLogFolder) 'intune-install.log' }
# Full paths: .NET resolves a relative path against the process's directory,
# which isn't always PowerShell's current location.
$SetupPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SetupPath)
$InstallRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallRoot)
$LogPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
$setupLogPath = Join-Path (Split-Path -Parent $LogPath) 'intune-setup.log'
$installedExe = Join-Path (Join-Path $InstallRoot 'current') $MainExe

function Write-Log([string] $Message, [string] $Level = 'INFO') {
    $line = '{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff zzz'), $Level, $Message
    # Write-Host, not Write-Output: the functions below return values, and a
    # log line must not end up in them.
    Write-Host $line
    try {
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    } catch {
        Write-Host "Could not write to the log ${LogPath}: $($_.Exception.Message)"
    }
}

function Get-FileVersion([string] $Path) {
    try {
        $info = (Get-Item -LiteralPath $Path).VersionInfo
        if ($info.ProductVersion) { return $info.ProductVersion }
        if ($info.FileVersion) { return $info.FileVersion }
    } catch {
        # The version is only for the log.
    }
    return 'unknown'
}

# The agent's sign-in command (the Run value), or $null when there is none.
function Get-RunCommand {
    $item = Get-ItemProperty -LiteralPath $RunKeyPath -Name $RunValueName -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    $value = [string] $item.$RunValueName
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

# Splits a Run command into the exe and its arguments. The agent writes the
# quoted exe path with no arguments (StartupRunKey.CommandFor).
function Split-RunCommand([string] $Command) {
    if ($Command -match '^\s*"(?<exe>[^"]+)"\s*(?<args>.*)$' -or
        $Command -match '^\s*(?<exe>\S+)\s*(?<args>.*)$') {
        return [pscustomobject] @{ Exe = $Matches['exe']; Arguments = $Matches['args'].Trim() }
    }
    return $null
}

# The ids of the processes running from exactly this exe. Through CIM rather
# than Get-Process: a 32-bit PowerShell (what Intune starts for an install
# command) can't read a 64-bit process's path with Get-Process. Another user's
# agent runs from their own profile, so it never matches.
function Get-AgentProcessIds([string] $ExePath) {
    $full = [System.IO.Path]::GetFullPath($ExePath)
    $name = [System.IO.Path]::GetFileName($full).Replace("'", "\'")
    @(Get-CimInstance -ClassName Win32_Process -Filter "Name = '$name'" |
            Where-Object {
                $_.ExecutablePath -and [string]::Equals(
                    [System.IO.Path]::GetFullPath($_.ExecutablePath), $full,
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object { [int] $_.ProcessId })
}

# Runs Setup.exe silently, waits for it alone (not for anything it leaves
# running), and returns its exit code.
function Invoke-Setup {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $SetupPath
    $psi.Arguments = '--silent --log "{0}"' -f $setupLogPath
    $psi.WorkingDirectory = Split-Path -Parent $SetupPath
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        $process.WaitForExit()
        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

# Copies the last lines of Setup.exe's log into this log, so the reason for a
# failed install is in the one file IT opens.
function Write-SetupLogTail {
    try {
        $tail = @(Get-Content -LiteralPath $setupLogPath -Tail 20 -ErrorAction Stop)
    } catch {
        Write-Log "    (no Setup.exe log: $($_.Exception.Message))" 'ERROR'
        return
    }
    foreach ($line in $tail) { Write-Log "    $line" 'ERROR' }
}

# Starts the agent with its sign-in command. Returns whether a start went out.
function Start-Agent([string] $Command, [string] $ExePath, [string] $Arguments) {
    $workingDirectory = Split-Path -Parent $ExePath
    try {
        $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine      = $Command
            CurrentDirectory = $workingDirectory
        }
        if ($result.ReturnValue -eq 0) {
            Write-Log "Started the agent through WMI, outside this script's process tree (PID $($result.ProcessId))."
            return $true
        }
        Write-Log "WMI could not start the agent: Win32_Process.Create returned $($result.ReturnValue)." 'WARN'
    } catch {
        Write-Log "WMI could not start the agent: $($_.Exception.Message)" 'WARN'
    }

    try {
        $startArgs = @{ FilePath = $ExePath; WorkingDirectory = $workingDirectory; PassThru = $true }
        if ($Arguments) { $startArgs.ArgumentList = $Arguments }
        $process = Start-Process @startArgs
        Write-Log ("Started the agent as a child of this script instead (PID $($process.Id)). If the " +
            'install then hangs or times out in Intune, the Intune Management Extension is waiting on it.') 'WARN'
        return $true
    } catch {
        Write-Log "Could not start the agent: $($_.Exception.Message)" 'ERROR'
        return $false
    }
}

try {
    $logDir = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $logDir)) {
        try { New-Item -ItemType Directory -Force -Path $logDir | Out-Null } catch { }
    }

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $bits = if ([Environment]::Is64BitProcess) { '64' } else { '32' }
    Write-Log ("=== Anchor agent install (Intune) | PowerShell $($PSVersionTable.PSVersion) $bits-bit | " +
        "session $session | Setup.exe: $SetupPath | install folder: $InstallRoot ===")

    if ($identity.IsSystem) {
        Write-Log ('Running as SYSTEM. The agent installs per user, so it would land in the SYSTEM profile ' +
            "instead of the student's. Set the Intune app's install behavior to User.") 'ERROR'
        exit 13
    }

    if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
        Write-Log ("The agent is already installed ($installedExe, version $(Get-FileVersion $installedExe)). " +
            'Not running Setup.exe: over an installed agent it would stop a running agent, and the agent ' +
            'updates itself.')
    } else {
        Write-Log "The agent isn't installed yet ($installedExe doesn't exist)."
        if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) {
            Write-Log ("$SetupFileName not found at $SetupPath. Package it in the .intunewin next to this " +
                'script; both are assets of every agent release.') 'ERROR'
            exit 10
        }
        Write-Log "Running $SetupPath --silent (version $(Get-FileVersion $SetupPath)); Setup.exe logs to $setupLogPath."
        $setupExit = Invoke-Setup
        if ($setupExit -ne 0) {
            Write-Log "Setup.exe failed with exit code $setupExit. The end of its log ($setupLogPath):" 'ERROR'
            Write-SetupLogTail
            exit $setupExit
        }
        if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
            Write-Log "Setup.exe exited with 0, but the agent isn't installed: $installedExe doesn't exist. See $setupLogPath" 'ERROR'
            exit 11
        }
        Write-Log "Setup.exe installed the agent (version $(Get-FileVersion $installedExe))."
    }

    $command = Get-RunCommand
    $running = @(Get-AgentProcessIds $installedExe)
    if ($running.Count -gt 0) {
        Write-Log "The agent is already running (PID $($running -join ', ')). Not starting it again."
        if (-not $command) {
            Write-Log ("There is no $RunValueName value under $RunKeyPath, so the agent won't start at the " +
                'next sign-in.') 'WARN'
        }
        exit 0
    }

    if (-not $command) {
        Write-Log ("There is no $RunValueName value under $RunKeyPath to start the agent with. The agent's " +
            'install hook writes it; without it the agent doesn''t start at sign-in either.') 'ERROR'
        exit 12
    }
    $parsed = Split-RunCommand $command
    if ($null -eq $parsed) {
        Write-Log "The $RunValueName value under $RunKeyPath isn't a command: '$command'." 'ERROR'
        exit 12
    }
    Write-Log "The agent isn't running. Starting it with its sign-in command: $command"

    if (-not [string]::Equals([System.IO.Path]::GetFullPath($parsed.Exe), [System.IO.Path]::GetFullPath($installedExe),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Log "The sign-in command starts $($parsed.Exe), not $installedExe." 'WARN'
        $running = @(Get-AgentProcessIds $parsed.Exe)
        if ($running.Count -gt 0) {
            Write-Log "That exe is already running (PID $($running -join ', ')). Not starting it again."
            exit 0
        }
    }

    if (-not (Start-Agent $command $parsed.Exe $parsed.Arguments)) {
        exit 14
    }

    Start-Sleep -Seconds $StayUpSeconds
    $running = @(Get-AgentProcessIds $parsed.Exe)
    if ($running.Count -eq 0) {
        Write-Log ("The agent stopped within $StayUpSeconds s of its start. Its own logs are in " +
            "$(Join-Path $localAppData $AgentLogFolder).") 'ERROR'
        exit 14
    }
    Write-Log "The agent is running (PID $($running -join ', ')). Done."
    exit 0
} catch {
    Write-Log "Unexpected error: $($_.Exception.Message)`n$($_.ScriptStackTrace)" 'ERROR'
    exit 15
}
