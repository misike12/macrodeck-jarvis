<#
.SYNOPSIS
    Installs the JARVIS elevated service.

.DESCRIPTION
    Builds the service and registers it with the service control manager. Registration needs an
    administrator shell, so this script checks for that first and says so plainly rather than failing
    later with an access-denied code.

    The service runs as LocalSystem, which is the identity that can write under HKLM without a prompt
    per operation. On a shared machine, review that in NativeServiceControl before using it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-service.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'service/Jarvis.Service/Jarvis.Service.csproj'
$binary = Join-Path $repository 'service/Jarvis.Service/bin/Release/net10.0-windows/Jarvis.Service.exe'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Elevated)) {
    Write-Error @'
This has to run elevated.

Right-click Windows Terminal or PowerShell, choose "Run as administrator", and run this script again.

To check whether a shell is elevated:

    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
'@
    exit 5
}

if (-not $SkipBuild) {
    Write-Host 'Building the service...'
    dotnet publish $project -c Release -r win-x64 --self-contained false -o (Split-Path -Parent $binary) -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "The service did not build. Exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $binary)) {
    Write-Error "The service binary was not found at $binary."
    exit 1
}

# A tray-mode instance holds its own executable open, so a rebuild over it fails with MSB3026 and the
# install then runs a stale binary. Stopped first rather than reported, because the only thing it can be is
# an instance of this same service.
$running = Get-Process -Name 'Jarvis.Service' -ErrorAction SilentlyContinue

if ($running) {
    Write-Host "Stopping $($running.Count) running Jarvis Service instance(s) so the binary can be replaced."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

Write-Host "Registering JarvisService from $binary"

# The binary's own output is streamed rather than captured. It is the only thing that says why a
# registration was refused, and a silent exit code is the least useful thing a failed install can do.
& $binary --install
$exit = $LASTEXITCODE

if ($exit -eq 5) {
    Write-Error @'
The service binary reported that this shell is not elevated.

Check it directly, then run again from an elevated PowerShell:

    & "C:\Users\Misu\Desktop\ideas\JARVIS\src\service\Jarvis.Service\bin\Release\net10.0-windows\Jarvis.Service.exe" --diagnose
'@
    exit 5
}

if ($exit -ne 0) {
    Write-Error "The service was not registered. Exit code $exit."
    exit $exit
}

if ($Start) {
    Write-Host 'Starting JarvisService...'
    Start-Service -Name JarvisService -ErrorAction SilentlyContinue
    $status = (Get-Service -Name JarvisService -ErrorAction SilentlyContinue).Status
    Write-Host "JarvisService is $status."
}

Write-Host ''
Write-Host 'Installed. To remove it again:'
Write-Host '  .\uninstall-service.ps1'
Write-Host ''
Write-Host 'Run it with a tray icon instead of as a service:'
Write-Host "  $binary"
