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

# The binary's output is captured to a file and printed, rather than streamed. It is a Windows-subsystem
# executable, so its console output does not reliably reach the calling window, and the only thing that
# says why a registration was refused is that output. Redirecting to a file works regardless of whether
# the binary managed to attach to this console.
$stdout = Join-Path $env:TEMP 'jarvis-install-out.txt'
$stderr = Join-Path $env:TEMP 'jarvis-install-err.txt'

$process = Start-Process -FilePath $binary -ArgumentList '--install' -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

foreach ($file in @($stdout, $stderr)) {
    if (Test-Path $file) {
        $text = (Get-Content $file -Raw -ErrorAction SilentlyContinue)

        if ($text) {
            Write-Host $text.TrimEnd()
        }

        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
}

$exit = $process.ExitCode

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

    # Queried rather than assumed. Under StrictMode, reading a property off a null result throws, which
    # turns "the service is not there" into an unrelated-looking script error instead of a clear message.
    $service = Get-Service -Name JarvisService -ErrorAction SilentlyContinue

    if ($null -eq $service) {
        Write-Warning @"
JarvisService is not registered, even though the installer reported success.

Check what the service manager actually has:

    sc.exe query JarvisService
    Get-Service | Where-Object { `$_.Name -like '*arvis*' }

and re-run the installer directly to see its full output:

    & "$binary" --install
"@
        exit 1
    }

    Write-Host "JarvisService is $($service.Status)."
}

Write-Host ''
Write-Host 'Installed. To remove it again:'
Write-Host '  .\uninstall-service.ps1'
Write-Host ''
Write-Host 'Run it with a tray icon instead of as a service:'
Write-Host "  $binary"
