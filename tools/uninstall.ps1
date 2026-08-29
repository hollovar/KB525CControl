<#
.SYNOPSIS
    Uninstalls the KB525C Control app.

.PARAMETER RestoreDellService
    Sets the Dell Peripheral Manager service back to automatic start.

.PARAMETER KeepConfig
    Keep the settings.
#>

[CmdletBinding()]
param(
    [switch] $RestoreDellService,
    [switch] $KeepConfig
)

$ErrorActionPreference = 'Stop'

$AppName    = 'KB525CControl'
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$ConfigDir  = Join-Path $env:APPDATA $AppName
$RunKey     = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

function Write-Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "    $t" -ForegroundColor Green }

# The app disables the buttons and turns the LEDs off on exit,
# so let it shut down normally before deleting anything.
Write-Step 'Stopping'
$running = Get-Process -Name $AppName -ErrorAction SilentlyContinue
if ($running) {
    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Ok 'Stopped.'
} else {
    Write-Ok 'Was not running.'
}

Write-Step 'Removing autostart'
if (Get-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $RunKey -Name $AppName
    Write-Ok 'Removed.'
} else {
    Write-Ok 'Was not set.'
}

Write-Step 'Deleting files'
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Ok "Deleted: $InstallDir"
} else {
    Write-Ok 'Was not installed.'
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($userPath -split ';' | Where-Object { $_ })
if ($parts -contains $InstallDir) {
    [Environment]::SetEnvironmentVariable('Path', (($parts | Where-Object { $_ -ne $InstallDir }) -join ';'), 'User')
    Write-Ok 'Removed from the user PATH.'
}

if (-not $KeepConfig -and (Test-Path $ConfigDir)) {
    Remove-Item -Path $ConfigDir -Recurse -Force
    Write-Ok "Settings deleted: $ConfigDir"
}

if ($RestoreDellService) {
    Write-Step 'Restoring Dell Peripheral Manager (administrator rights required)'
    $inner = @'
Set-Service -Name DPMService -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service -Name DPMService -ErrorAction SilentlyContinue
'@
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
    try {
        Start-Process powershell -Verb RunAs -Wait `
            -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $b64
        Start-Sleep -Milliseconds 800
        $svc = Get-Service -Name DPMService -ErrorAction SilentlyContinue
        if ($svc) { Write-Ok "DPMService: $($svc.Status) / $($svc.StartType)" }
    }
    catch {
        Write-Host "    Elevation did not happen." -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
