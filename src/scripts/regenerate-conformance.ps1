<#
.SYNOPSIS
    Regenerates both conformance reports and the packed artifact.

.DESCRIPTION
    Both committed reports were produced by hand, which is the same as saying nothing keeps them true. This
    runs the two subjects the CLI supports and leaves the output where the repository expects it, so a
    reviewer can regenerate them rather than trusting whatever was committed.

    Two subjects, because they answer different questions:

      project  - the plugin as built: capability contracts, cancellation semantics, the reserved
                 endpoints, logging limits. Its manifest checks skip, because the manifest points at
                 runtimes/<rid>/ which only the packer produces.
      artifact - the packed plugin: the same capabilities plus the manifest itself, so this is the one
                 that carries MDC0104 through MDC0107.

.PARAMETER SkipBuild
    Validates and tests what is already packed instead of repacking first.

.PARAMETER SkipProjectSuite
    Regenerates only the artifact report. The project suite is the slower of the two and answers a question
    the artifact report also answers, so a caller that only needs the manifest checks can ask for one run.

.EXAMPLE
    ./regenerate-conformance.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipProjectSuite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$plugin = Join-Path $repository 'src/Jarvis.Plugin'
$artifacts = Join-Path $repository 'artifacts'

Push-Location $repository

try {
    if (-not $SkipBuild) {
        Write-Host 'Building and packing the plugin...'
        macrodeck-plugin build --source $plugin --output $artifacts --force

        if ($LASTEXITCODE -ne 0) {
            Write-Error "The plugin did not pack. Exit code $LASTEXITCODE."
            exit $LASTEXITCODE
        }
    }

    $artifact = Get-ChildItem -LiteralPath $artifacts -Filter '*.macroDeckPlugin' -EA SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $artifact) {
        Write-Error "No .macroDeckPlugin artifact was found in $artifacts."
        exit 1
    }

    Write-Host "Validating $($artifact.Name) at publication level..."
    macrodeck-plugin validate --artifact $artifact.FullName --level Publication

    if ($LASTEXITCODE -ne 0) {
        Write-Error 'The artifact failed publication validation.'
        exit $LASTEXITCODE
    }

    if (-not $SkipProjectSuite) {
        Write-Host 'Running the project conformance suite...'
        macrodeck-plugin test --project $plugin --report markdown --output (Join-Path $repository 'conformance.md')

        if ($LASTEXITCODE -ne 0) {
            Write-Error "The project conformance suite failed. Exit code $LASTEXITCODE."
            exit $LASTEXITCODE
        }
    }
    else {
        Write-Host 'Skipping the project conformance suite.'
    }

    Write-Host 'Running the artifact conformance suite...'
    macrodeck-plugin test --artifact $artifact.FullName --report markdown --output (Join-Path $repository 'artifact-conformance.md')

    # Checked, because neither suite checked it. A suite that fails without writing its output leaves the
    # previously committed report sitting there, and "both reports regenerated" is printed either way, so a
    # real conformance failure was reported as a success with a stale report behind it.
    if ($LASTEXITCODE -ne 0) {
        Write-Error "The artifact conformance suite failed. Exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }

    Write-Host ''
    Write-Host 'Both reports regenerated.'
    Write-Host ''
    Write-Host 'The project report skips the four manifest checks by construction. That is expected, and'
    Write-Host 'the artifact report is the one that carries them.'
}
finally {
    Pop-Location
}