<#
.SYNOPSIS
    Installs the JARVIS elevated service.

.DESCRIPTION
    Publishes the service into Program Files, hardens the directory's permissions, and registers it with
    the service control manager. Registration needs an administrator shell, so this script checks for that
    first and says so plainly rather than failing later with an access-denied code.

    The service runs as LocalSystem, which is the identity that can write under HKLM without a prompt
    per operation.

    Why Program Files rather than the build output
    -----------------------------------------------
    A service registered from anywhere the interactive user can write is a privilege escalation: the user
    replaces the executable and the next start runs their code as SYSTEM. The whole path from C:\Users
    downwards is user-writable by default, so a build directory is exactly the wrong place to register a
    LocalSystem binary from. The service therefore lives under Program Files with an explicit descriptor,
    and the install fails if that descriptor did not take.

    Why the user identifier is captured
    -----------------------------------
    The pipe the plugin talks to is granted to one account. The interactive group would grant it to every
    signed-in user on the machine, which on anything shared is a cross-user escalation into a SYSTEM
    process. This shell is elevated but is still the user, so its identifier is the right one to record.

.PARAMETER SkipBuild
    Registers without publishing first. Only useful when the binary in Program Files is already current.

.PARAMETER Start
    Starts the service afterwards and fails if it does not reach Running.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install-service.ps1 -Start
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
$builtBinary = Join-Path $repository 'service/Jarvis.Service/bin/Release/net10.0-windows/Jarvis.Service.exe'
$installDirectory = Join-Path $env:ProgramFiles 'Jarvis Service'
$installedBinary = Join-Path $installDirectory 'Jarvis.Service.exe'

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

# Captured before anything else is done, because it is the one piece of information the whole security
# boundary rests on and there is no second chance to ask for it.
$userSid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
Write-Host "The elevated pipe will be reachable only by $userSid"

# Stopped before anything is published or copied, not after. A running service holds its own executable and
# every assembly beside it open, so copying over the top of a live install fails with a sharing violation on
# the first DLL and leaves the old binary in place. Doing this after the copy, which is where it was, meant
# an install over a running service could not succeed at all.
$watchedPaths = @($installedBinary, $builtBinary) | Where-Object { $_ }

function Get-JarvisProcessesHolding {
    foreach ($process in @(Get-Process -Name 'Jarvis.Service' -ErrorAction SilentlyContinue)) {
        try {
            # Matched on the path rather than the name, so another user's tray instance on a shared machine
            # is not killed. Reading Path is itself denied for another user's process, which is the same
            # answer: not ours to stop.
            if ($watchedPaths -contains $process.Path) {
                $process
            }
        }
        catch {
            # Access denied on Path.
        }
    }
}

$service = Get-Service -Name JarvisService -ErrorAction SilentlyContinue

if ($service -and $service.Status -ne 'Stopped') {
    Write-Host 'Stopping the JarvisService registration so its files can be replaced...'
    Stop-Service -Name JarvisService -Force -ErrorAction SilentlyContinue
}

$running = @(Get-JarvisProcessesHolding)

if ($running) {
    Write-Host "Stopping $($running.Count) Jarvis Service process(es) holding the files open."
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
}

# Waited for rather than assumed. Stop-Process returns once the kill is issued, and the handles are released
# a moment later, so a copy issued immediately would still race the exit.
$deadline = (Get-Date).AddSeconds(20)

while ((@(Get-JarvisProcessesHolding).Count -gt 0) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

$stillRunning = @(Get-JarvisProcessesHolding)

if ($stillRunning.Count -gt 0) {
    $stillRunning | ForEach-Object { Write-Error "Process $($_.Id) is still running and holds $installedBinary open." }

    Write-Error @'
Close it and run again. A process that is holding the installed files open cannot be replaced, and
installing over it would leave the old binary in place under a new registration.
'@
    exit 1
}

if (-not $SkipBuild) {
    Write-Host 'Building the service...'

    # Published to a staging directory first. Writing straight into Program Files would leave a partially
    # copied binary in place if the copy failed part way, and the next start would run whatever landed.
    $staging = Join-Path $env:TEMP ("jarvis-service-" + [Guid]::NewGuid().ToString('N'))

    try {
        dotnet publish $project -c Release -r win-x64 --self-contained false -o $staging -v quiet

        if ($LASTEXITCODE -ne 0) {
            Write-Error "The service did not build. Exit code $LASTEXITCODE."
            exit $LASTEXITCODE
        }

        if (-not (Test-Path (Join-Path $staging 'Jarvis.Service.exe'))) {
            Write-Error 'The publish did not produce Jarvis.Service.exe.'
            exit 1
        }

        Write-Host "Installing into $installDirectory"

        # Emptied rather than copied over. An assembly that a previous version shipped and this one no
        # longer produces would otherwise stay in the directory forever, and a stale assembly next to the
        # new binary is exactly what an in-process service loads by accident.
        if (Test-Path $installDirectory) {
            Get-ChildItem -LiteralPath $installDirectory -Force | Remove-Item -Recurse -Force
        }
        else {
            New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
        }

        # The descriptor is applied before the files land, not after. A directory that briefly contains a
        # SYSTEM binary under inherited permissions is a window, not a formality.
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)

        $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
        $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
        $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')

        $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        $propagate = [Security.AccessControl.PropagationFlags]::None
        $allow = [Security.AccessControl.AccessControlType]::Allow

        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $system, 'FullControl', $inherit, $propagate, $allow)))
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $administrators, 'FullControl', $inherit, $propagate, $allow)))

        # Users get read and execute so the binary can be inspected and --diagnose can be run by hand.
        # Explicitly not Modify: write access to this directory is the whole escalation being closed off.
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $users, 'ReadAndExecute', $inherit, $propagate, $allow)))

        Set-Acl -LiteralPath $installDirectory -AclObject $acl

        Copy-Item -Path (Join-Path $staging '*') -Destination $installDirectory -Recurse -Force
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $installedBinary)) {
    Write-Error "The service was not found at $installedBinary."
    exit 1
}

# Verified rather than assumed. Setting a descriptor can succeed and still be narrower than intended if
# something re-applied an inherited one, and the cost of not noticing is a service binary any user can
# replace. This is the check that makes the install trustworthy.
$effective = Get-Acl -LiteralPath $installDirectory
$usersSid = 'S-1-5-32-545'

foreach ($rule in $effective.Access) {
    if ($rule.IdentityReference.Value -ne $usersSid) {
        continue
    }

    $writable = @(
        'WriteData', 'CreateFiles', 'AppendData', 'WriteExtendedAttributes', 'WriteAttributes',
        'Delete', 'DeleteSubdirectoriesAndFiles', 'ChangePermissions', 'TakeOwnership'
    )

    $granted = $rule.FileSystemRights.ToString()

    foreach ($right in $writable) {
        if ($granted -match $right) {
            Write-Error @"
The install directory is still writable by ordinary users.

    Everyone: $granted  ($right)

A LocalSystem service must not be run from a directory the interactive user can replace, or the next
start executes whatever is sitting there as SYSTEM. This usually means a group policy or antivirus is
resetting the descriptor; check the directory's permissions in Computer Management, then run again.
"@
            exit 1
        }
    }
}

Write-Host "Verified: ordinary users cannot modify $installDirectory"

# The machine-wide state directory, established here rather than left to the first log line. Program Data
# hands every local user read access by default, and this directory holds the log, which names the account
# the pipe is granted to and the key paths callers asked about. The service narrows the descriptor when it
# creates the directory itself, but a directory created with the default one stays wide until then, and on a
# machine where the service never starts there is no then.
$stateDirectory = Join-Path $env:ProgramData 'Jarvis'

if (-not (Test-Path $stateDirectory)) {
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
}

$stateAcl = New-Object Security.AccessControl.DirectorySecurity
$stateAcl.SetAccessRuleProtection($true, $false)

$stateInherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$statePropagate = [Security.AccessControl.PropagationFlags]::None
$stateAllow = [Security.AccessControl.AccessControlType]::Allow

foreach ($identity in @(
    (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
    (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544'))
)) {
    $stateAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $identity, 'FullControl', $stateInherit, $statePropagate, $stateAllow)))
}

Set-Acl -LiteralPath $stateDirectory -AclObject $stateAcl
Write-Host "Verified: $stateDirectory is readable only by SYSTEM and administrators"

# An earlier version of this script registered the service straight out of the build output, which is
# inside the user's own profile. A registration is not a privilege on its own, but that one points at a
# binary the user can replace, so anyone able to start the service runs their own code as SYSTEM. Leaving
# it behind is the worst of both states: the new binary is correct but the old registration can still be
# started. It has to be deleted outright, because sc.exe config cannot move a service to a different
# binary path and the installer below creates a new one rather than reconfiguring the old.
$existing = Get-CimInstance Win32_Service -Filter "Name='JarvisService'" -ErrorAction SilentlyContinue

if ($null -ne $existing) {
    $quoted = [regex]::Match($existing.PathName, '^"([^"]+)"')
    $bare = [regex]::Match($existing.PathName, '^(\S+)')

    if ($quoted.Success) {
        $registeredBinary = $quoted.Groups[1].Value
    }
    elseif ($bare.Success) {
        $registeredBinary = $bare.Groups[1].Value
    }
    else {
        $registeredBinary = $existing.PathName
    }

    $insideInstallDirectory = $registeredBinary.StartsWith(
        $installDirectory, [StringComparison]::OrdinalIgnoreCase)

    if (-not $insideInstallDirectory) {
        Write-Host "Removing a previous registration pointing at $registeredBinary"

        if ($existing.State -ne 'Stopped') {
            Stop-Service -Name 'JarvisService' -Force -ErrorAction SilentlyContinue

            $stopped = (Get-Date).AddSeconds(15)

            while ((Get-Service -Name 'JarvisService' -ErrorAction SilentlyContinue) -ne $null -and
                   (Get-Date) -lt $stopped) {
                Start-Sleep -Milliseconds 500
            }
        }

        # Out parameters, because Start-Process cannot otherwise keep them, and the exit code is checked:
        # a delete that failed would leave the old registration in place and the create below would then
        # fail with a name-already-exists error that says nothing about the real cause.
        $null = Start-Process -FilePath 'sc.exe' -ArgumentList @('delete', 'JarvisService') `
            -Wait -PassThru -NoNewWindow -RedirectStandardOutput 'NUL'

        if ($LASTEXITCODE -ne 0) {
            Write-Error @"
The previous registration could not be removed, so the service cannot be reinstalled.

    sc.exe delete JarvisService

This usually means the service is still open in the service console. Close it and run again.
"@
            exit 1
        }

        # The manager keeps the service marked for deletion until every handle to it is closed, so creating
        # the replacement immediately can still collide with the name.
        $gone = (Get-Date).AddSeconds(15)

        while ((Get-Service -Name 'JarvisService' -ErrorAction SilentlyContinue) -ne $null -and
               (Get-Date) -lt $gone) {
            Start-Sleep -Milliseconds 500
        }
    }
}

Write-Host "Registering JarvisService from $installedBinary"

# The binary's output is captured to a file and printed, rather than streamed. It is a Windows-subsystem
# executable, so its console output does not reliably reach the calling window, and the only thing that
# says why a registration was refused is that output. Redirecting to a file works regardless of whether
# the binary managed to attach to this console.
$stdout = Join-Path $env:TEMP 'jarvis-install-out.txt'
$stderr = Join-Path $env:TEMP 'jarvis-install-err.txt'

$process = Start-Process -FilePath $installedBinary `
    -ArgumentList @('--install', '--user-sid', $userSid) -Wait -PassThru -NoNewWindow `
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

    & "$env:ProgramFiles\Jarvis Service\Jarvis.Service.exe" --diagnose
'@
    exit 5
}

if ($exit -ne 0) {
    Write-Error "The service was not registered. Exit code $exit."
    exit $exit
}

if ($Start) {
    Write-Host 'Starting JarvisService...'

    # Not silenced. A service that will not start is the failure worth seeing, and the service control
    # manager's own message says why in a way no amount of guessing here would.
    try {
        Start-Service -Name JarvisService -ErrorAction Stop
    }
    catch {
        Write-Warning "The service did not start: $($_.Exception.Message)"
    }

    # Queried rather than assumed. Under StrictMode, reading a property off a null result throws, which
    # turns "the service is not there" into an unrelated-looking script error instead of a clear message.
    $service = Get-Service -Name JarvisService -ErrorAction SilentlyContinue

    if ($null -eq $service) {
        Write-Error @'
JarvisService is not registered, even though the installer reported success.

Check what the service manager actually has:

    sc.exe queryex JarvisService
    sc.exe qc JarvisService
'@
        exit 1
    }

    # Given a moment. The manager reports Stopped for a moment after a successful start.
    $deadline = (Get-Date).AddSeconds(15)

    while ($service.Status -ne 'Running' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $service.Refresh()
    }

    Write-Host "JarvisService is $($service.Status)."

    if ($service.Status -ne 'Running') {
        # A non-zero exit, because a caller asked for a working service and did not get one. Warning and
        # exit 0 reports success for something that does not work, which is the failure this whole script
        # exists to prevent.
        Write-Error @"
The service did not reach Running. The service control manager's reason:

    sc.exe queryex JarvisService

The most recent two service events:

    Get-WinEvent -LogName System -MaxEvents 40 -ErrorAction SilentlyContinue |
        Where-Object ProviderName -eq 'Service Control Manager' |
        Select-Object -First 2 TimeCreated, Id, Message

The service's own log, which names the cause where the manager does not:

    Get-Content "$env:ProgramData\Jarvis\Jarvis.Service.log" -Tail 20
"@
        exit 1
    }
}

Write-Host ''
Write-Host 'Installed. To remove it again:'
Write-Host '  .\uninstall-service.ps1'
Write-Host ''
Write-Host 'Run it with a tray icon instead of as a service:'
Write-Host "  $installedBinary"