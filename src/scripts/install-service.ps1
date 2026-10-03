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

Write-Host "Registering JarvisService from $binary"

$process = Start-Process -FilePath $binary -ArgumentList '--install' -Wait -PassThru -NoNewWindow

if ($process.ExitCode -ne 0) {
    Write-Error "The service was not registered. Exit code $($process.ExitCode)."
    exit $process.ExitCode
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
