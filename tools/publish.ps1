<#
.SYNOPSIS
    Builds a release: the tray app and the kb525c CLI as single self-contained
    executables, collected into dist\ together with the install/uninstall scripts.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -NoCli
#>

[CmdletBinding()]
param(
    [switch] $NoCli
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

function Write-Step($t) { Write-Host "==> $t" -ForegroundColor Cyan }

$common = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true'
)

Write-Step 'KB525CControl.exe'
dotnet publish (Join-Path $root 'KB525CControl\KB525CControl.csproj') @common -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'Publishing KB525CControl failed.' }

if (-not $NoCli) {
    Write-Step 'kb525c.exe'
    dotnet publish (Join-Path $root 'KB525CCli\KB525CCli.csproj') @common
    if ($LASTEXITCODE -ne 0) { throw 'Publishing kb525c failed.' }
}

Write-Step "Collecting into: $dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Copy-Item (Join-Path $root 'KB525CControl\bin\Release\net10.0-windows\win-x64\publish\KB525CControl.exe') $dist -Force
if (-not $NoCli) {
    Copy-Item (Join-Path $root 'KB525CCli\bin\Release\net10.0\win-x64\publish\kb525c.exe') $dist -Force
}
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $dist -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dist -Force

Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

Write-Host 'Done. Install on the target machine: powershell -ExecutionPolicy Bypass -File .\install.ps1' -ForegroundColor Green
