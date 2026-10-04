<#
.SYNOPSIS
    Removes the JARVIS elevated service.

.DESCRIPTION
    Stops the service, deletes its registration, and removes everything the installer put on the machine.

    Every artifact is removed, not just the registration. Leaving the installed binary behind means a
    LocalSystem-capable executable stays on disk after the user believes the service is gone, and leaving the
    per-machine state behind means a reinstall inherits a recorded account identifier the new install never
    asked for. Removal is best effort: each step reports what it could not do and the script continues, so a
    single locked file does not leave the other five behind.

    The per-user startup entry is only removed for the account running the script. Another user's tray
    autostart entry is theirs to remove, and this script is usually run by one administrator on a machine
    where other accounts exist.

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

$installDirectory = Join-Path $env:ProgramFiles 'Jarvis Service'
$installedBinary = Join-Path $installDirectory 'Jarvis.Service.exe'
$stateDirectory = Join-Path $env:ProgramData 'Jarvis'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Stopped before anything is deleted. A tray instance holds both the executable and the pipe open, so a
# removal that skipped it would fail to delete the binary and leave the pipe owned by a process that is no
# longer supposed to exist. Matched on the installed path rather than the name, so another user's instance on
# a shared machine is not killed by mistake.
$running = Get-Process -Name 'Jarvis.Service' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $installedBinary }

if ($running) {
    Write-Host "Stopping $($running.Count) tray instance(s) of the installed service."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

$service = Get-Service -Name JarvisService -ErrorAction SilentlyContinue

if ($service) {
    Write-Host 'Stopping JarvisService...'
    Stop-Service -Name JarvisService -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host 'JarvisService is not registered.'
}

# The binary is asked to deregister itself so the recorded account identifier is cleared with it. Fall back
# to sc.exe, because the binary is the thing most likely to be missing and the registration the thing most
# likely to be wanted gone.
$repository = Split-Path -Parent $PSScriptRoot
$builtBinary = Join-Path $repository 'service/Jarvis.Service/bin/Release/net10.0-windows/Jarvis.Service.exe'

if ($service -and (Test-Path $installedBinary)) {
    Write-Host 'Deregistering JarvisService...'
    $process = Start-Process -FilePath $installedBinary -ArgumentList '--uninstall' -Wait -PassThru -NoNewWindow

    if ($process.ExitCode -ne 0) {
        Write-Error "The service binary reported exit code $($process.ExitCode); falling back to sc.exe."
        $null = & sc.exe delete JarvisService
    }
}
elseif ($service) {
    Write-Warning "No installed binary at $installedBinary, so the registration was removed with sc.exe instead."
    $null = & sc.exe delete JarvisService
}

# The service is marked for deletion while the control manager holds it open, which can take a moment after
# the binary has gone. Polled so the message is truthful.
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

# The per-user autostart entry. Read before deleting anything, so the report can say whether there was one.
if (Test-Path $runKey) {
    $entry = (Get-ItemProperty -Path $runKey -Name 'JarvisServiceTray' -ErrorAction SilentlyContinue).JarvisServiceTray

    if ($entry) {
        Remove-ItemProperty -Path $runKey -Name 'JarvisServiceTray' -ErrorAction SilentlyContinue
        Write-Host 'Removed the tray autostart entry for this account.'
    }
}

# Then the two directories. Each is reported rather than assumed, because a partial removal that printed
# "removed" would be the same lie as a registration that was never deleted.
foreach ($target in @($installDirectory, $stateDirectory)) {
    if (-not (Test-Path $target)) {
        continue
    }

    try {
        Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
        Write-Host "Removed $target"
    }
    catch {
        Write-Warning "Could not remove $target : $($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host 'Uninstalled.'
Write-Host ''

if ($builtBinary -and (Test-Path $builtBinary)) {
    Write-Host "The build output at $builtBinary was left alone; it is not installed."
    Write-Host ''
}
