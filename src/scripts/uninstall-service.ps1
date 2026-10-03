<#
.SYNOPSIS
    Removes the JARVIS elevated service.

.DESCRIPTION
    Stops the service and deletes its registration. Registration changes need an administrator shell,
    so this script checks for that first.

    The binary is left in place, because a rebuild is cheaper than a reinstall and the script is
    usually run before that rather than after.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

$existing = Get-Service -Name JarvisService -ErrorAction SilentlyContinue

if (-not $existing) {
    Write-Host 'JarvisService is not installed.'
    exit 0
}

Write-Host 'Stopping JarvisService...'
Stop-Service -Name JarvisService -Force -ErrorAction SilentlyContinue

$repository = Split-Path -Parent $PSScriptRoot
$binary = Join-Path $repository 'service/Jarvis.Service/bin/Release/net10.0-windows/Jarvis.Service.exe'

if (Test-Path $binary) {
    Write-Host 'Deregistering JarvisService...'
    $process = Start-Process -FilePath $binary -ArgumentList '--uninstall' -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        Write-Error "The service registration was not removed. Exit code $($process.ExitCode)."
        exit $process.ExitCode
    }
}
else {
    Write-Warning "The binary was not found at $binary, so the registration was removed with sc.exe instead."
    sc.exe delete JarvisService | Out-Null
}

# The service is marked for deletion while the control manager holds it open, which can take a moment
# after the binary has gone. Polled so the message is truthful.
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Service -Name JarvisService -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

if (Get-Service -Name JarvisService -ErrorAction SilentlyContinue) {
    Write-Warning 'JarvisService is still listed. It will disappear once nothing holds it open.'
}
else {
    Write-Host 'JarvisService has been removed.'
}
