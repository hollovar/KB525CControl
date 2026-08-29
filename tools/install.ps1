<#
.SYNOPSIS
    Installs the KB525C Control tray app and the kb525c command-line client.

.DESCRIPTION
    Copies the executables into the user profile, registers autostart, adds the
    install folder to the user PATH, and disables the Dell Peripheral Manager
    service when needed.

    Why DPMService must be disabled: the Dell software opens the keyboard's vendor
    command channel EXCLUSIVELY, so while it runs no other program can control the
    collaboration buttons.

.PARAMETER Source
    Path of KB525CControl.exe. Defaults to the exe next to this script or the publish folder.

.PARAMETER NoAutostart
    Do not register autostart.

.PARAMETER KeepDellService
    Leave DPMService alone. The app cannot work while that service runs.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Source C:\temp\KB525CControl.exe
#>

[CmdletBinding()]
param(
    [string] $Source,
    [switch] $NoAutostart,
    [switch] $KeepDellService
)

$ErrorActionPreference = 'Stop'

$AppName    = 'KB525CControl'
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$TargetExe  = Join-Path $InstallDir "$AppName.exe"
$RunKey     = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "    $text" -ForegroundColor Yellow }

# --- 1. locate the source ------------------------------------------------------

if (-not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot "$AppName.exe"),
        (Join-Path $PSScriptRoot "..\KB525CControl\bin\Release\net10.0-windows\win-x64\publish\$AppName.exe")
    )
    $Source = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Source -or -not (Test-Path $Source)) {
    throw "Cannot find $AppName.exe. Pass it with -Source."
}

Write-Step "Source: $Source"

# --- 2. keyboard check ---------------------------------------------------------

Write-Step 'Looking for the keyboard'
$kb = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
      Where-Object { $_.InstanceId -like '*VID_413C&PID_2123*' }

if ($kb) { Write-Ok 'Dell KB525C is connected.' }
else     { Write-Warn2 'The KB525C is not connected right now. Installation continues.' }

# --- 3. copy ---------------------------------------------------------------------

Write-Step "Installing to: $InstallDir"

$running = Get-Process -Name $AppName -ErrorAction SilentlyContinue
if ($running) {
    Write-Warn2 'An instance is running, stopping it.'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item -Path $Source -Destination $TargetExe -Force
Write-Ok "Copied: $TargetExe"

# The kb525c command-line client (optional, but needed for Claude Code / script integration).
$cliCandidates = @(
    (Join-Path (Split-Path $Source -Parent) 'kb525c.exe'),
    (Join-Path $PSScriptRoot 'kb525c.exe'),
    (Join-Path $PSScriptRoot '..\KB525CCli\bin\Release\net10.0\win-x64\publish\kb525c.exe')
)
$cliSource = $cliCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($cliSource) {
    Copy-Item -Path $cliSource -Destination (Join-Path $InstallDir 'kb525c.exe') -Force
    Write-Ok "Copied: $InstallDir\kb525c.exe"

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @($userPath -split ';' | Where-Object { $_ })
    if ($parts -notcontains $InstallDir) {
        [Environment]::SetEnvironmentVariable('Path', (($parts + $InstallDir) -join ';'), 'User')
        Write-Ok "Added to the user PATH ('kb525c' is available in new terminals)."
    } else {
        Write-Ok 'Already on the PATH.'
    }
}
else {
    Write-Warn2 'kb525c.exe not found, skipping the command-line client.'
}

# --- 4. Dell service -------------------------------------------------------------

$svc = Get-Service -Name DPMService -ErrorAction SilentlyContinue

if (-not $svc) {
    Write-Step 'Dell Peripheral Manager'
    Write-Ok 'Not installed, nothing to do.'
}
elseif ($KeepDellService) {
    Write-Step 'Dell Peripheral Manager'
    Write-Warn2 'Left alone because of -KeepDellService.'
    Write-Warn2 'KB525C Control cannot control the buttons while it runs.'
}
elseif ($svc.StartType -eq 'Disabled' -and $svc.Status -eq 'Stopped') {
    Write-Step 'Dell Peripheral Manager'
    Write-Ok 'Already disabled.'
}
else {
    Write-Step 'Disabling Dell Peripheral Manager (administrator rights required)'
    Write-Warn2 'This removes the factory Zoom/Teams integration of the buttons and firmware updates.'
    Write-Warn2 'Undo: Set-Service DPMService -StartupType Automatic; Start-Service DPMService'

    $inner = @'
Set-Service -Name DPMService -StartupType Disabled -ErrorAction SilentlyContinue
Stop-Service -Name DPMService -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700
Get-Process -Name DPM,DDPM,DDPM.Subagent,DDPM.Subagent.User,DPMCrashHandler -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
'@
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

    try {
        Start-Process powershell -Verb RunAs -Wait `
            -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $b64
        Start-Sleep -Milliseconds 800
        $svc = Get-Service -Name DPMService
        if ($svc.StartType -eq 'Disabled') { Write-Ok "DPMService: $($svc.Status) / $($svc.StartType)" }
        else { Write-Warn2 'Could not disable it. Run it manually as administrator.' }
    }
    catch {
        Write-Warn2 "Elevation did not happen: $($_.Exception.Message)"
        Write-Warn2 'The buttons will not work while DPMService runs.'
    }
}

# --- 5. autostart ----------------------------------------------------------------

Write-Step 'Autostart'
if ($NoAutostart) {
    Write-Warn2 'Skipped because of -NoAutostart.'
}
else {
    New-ItemProperty -Path $RunKey -Name $AppName -Value "`"$TargetExe`"" -PropertyType String -Force | Out-Null
    Write-Ok 'Starts at logon.'
}

# --- 6. start --------------------------------------------------------------------

Write-Step 'Starting'
# Do not pass Claude Code session markers on (this script is often run from a Claude Code
# terminal); the app scrubs them too. -UseNewEnvironment is NOT an option: it yields a
# broken environment without USERPROFILE and friends.
Remove-Item env:CLAUDE* -ErrorAction SilentlyContinue
Start-Process -FilePath $TargetExe
Start-Sleep -Seconds 2

if (Get-Process -Name $AppName -ErrorAction SilentlyContinue) {
    Write-Ok 'Running. Look for the icon in the tray.'
}
else {
    Write-Warn2 "It did not start. Start it manually: $TargetExe"
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  Settings  : $env:APPDATA\$AppName\config.json"
Write-Host "  Uninstall : .\uninstall.ps1"
