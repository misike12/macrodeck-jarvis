<#
.SYNOPSIS
    Fails when a conformance report claims conformance it did not earn.

.DESCRIPTION
    Fails when a conformance report claims conformance it did not earn.

    A conformance report can say "Conformant: yes" while every Required manifest check is SKIPped, and
    both committed reports used to do exactly that: MDC0104 through MDC0107 were all skipped with the
    reason "This subject has no manifest", so the manifest version, id, SemVer, icon media type and
    protocol range were all unverified and the summary still read yes.

    A Required check that skipped is not a pass. This makes it one, which is what turns the report from a
    description into a gate.

    NotApplicable below is the list of Required checks this plugin cannot satisfy, each with the reason.
    They are structural rather than outstanding: a weather station id cannot be invalid in a plugin that
    declares no weather capability, and an in-process registration cannot be observed from an out-of-process
    subject. Anything not on that list has to be fixed or added deliberately, which is the point: a new
    Required skip fails this script rather than being absorbed into a summary.

.PARAMETER Report
    The markdown report to read. Pass the artifact report. The project report skips MDC0104 through
    MDC0107 by construction, because a project's manifest points at a runtimes/ slot that only the packer
    produces, so gating on it would be gating on something that can never pass.

.PARAMETER AllowSkipped
    Additional Required check ids to tolerate. Empty by default.

.EXAMPLE
    ./require-conformance.ps1 -Report artifact-conformance.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Report,

    [string[]]$AllowSkipped = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Report)) {
    Write-Error "The report was not found at $Report."
    exit 1
}

$text = Get-Content -LiteralPath $Report -Raw

# Required checks this plugin cannot satisfy, with the reason. Structural, not outstanding.
$notApplicable = @{
    'MDC0103' = 'JARVIS declares no weather capability, so there are no weather instance ids.'
    'MDC0204' = 'Out-of-process subject: in-process state sharing across two starts is not observable.'
    'MDC0206' = 'Out-of-process subject: interactive pairing is not observable.'
    'MDC0402' = 'JARVIS declares no weather capability.'
    'MDC0311' = 'The variable provider reports values but declares no catalog.'
    'MDC0312' = 'No action declares ProvidesIcon.'
    'MDC0313' = 'No action declares ProvidesIcon.'
    'MDC0314' = 'JARVIS declares no writable variable.'
}

if ($text -notmatch '(?im)^\s*[-*|]?\s*\**\s*Conformant:\s*\**\s*(?<verdict>yes|no)\b') {
    Write-Error @"
The report has no conformance verdict.

    $Report

A report that does not state a verdict cannot be gated on, and one written by hand can be stale without
anything noticing. Regenerate it:

    cd src\scripts
    .\regenerate-conformance.ps1
"@
    exit 1
}

$verdict = $Matches['verdict'].Trim().ToLowerInvariant()

# A Required check that skipped rather than ran. Read from the table rows rather than a summary count,
# because the count is what was wrong in the first place.
$skippedRequired = [System.Collections.Generic.List[string]]::new()
$tolerated = [System.Collections.Generic.List[string]]::new()

foreach ($line in ($text -split "`r?`n")) {
    if ($line -notmatch '(?i)\bMDC\d{4}\b') {
        continue
    }

    if ($line -notmatch '(?i)\bSKIP\b') {
        continue
    }

    if ($line -notmatch '(?i)\bRequired\b') {
        continue
    }

    $id = [regex]::Match($line, '(?i)MDC\d{4}').Value.ToUpperInvariant()

    if ($AllowSkipped -contains $id) {
        continue
    }

    if ($notApplicable.ContainsKey($id)) {
        $tolerated.Add("$id ($($notApplicable[$id]))")
        continue
    }

    $skippedRequired.Add($id)
}

if ($verdict -ne 'yes') {
    Write-Error @"
The report says the plugin is not conformant.

    $Report
"@
    exit 1
}

if ($skippedRequired.Count -gt 0) {
    Write-Error @"
The report claims conformance while $(($skippedRequired | Sort-Object -Unique) -join ', ') skipped.

    $Report

A Required check that skipped proves nothing. The previous reports did exactly this: the four manifest
checks were all skipped because the project subject has no manifest, and the summary still read "yes", so
the manifest version, id, SemVer, icon media type and protocol range were never verified.

Validate the artifact, which does carry a manifest:

    macrodeck-plugin build --source src/Jarvis.Plugin --output ./artifacts
    macrodeck-plugin test --artifact ./artifacts/*.macroDeckPlugin --report markdown --output src/artifact-conformance.md
"@
    exit 1
}

Write-Host "$Report claims conformance."

if ($tolerated.Count -gt 0) {
    Write-Host ''
    Write-Host 'Required checks this plugin does not declare, and therefore cannot satisfy:'
    $tolerated | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
}