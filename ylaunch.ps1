#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build and test OmniEurope.Diagnostics.
.DESCRIPTION
    Package launcher: a library has no host and no database, so the launcher builds
    the solution and runs its test suites. This file holds only what is specific to the repository: the
    parameters, the PowerShell 7 trampoline and $LaunchConfig. The mechanics live in
    scripts\ylaunch-core.ps1, a versioned shared core (never edit the copy). .\ylaunch.ps1 -hl for the workflow.
.EXAMPLE
    .\ylaunch.ps1 -t          Build + unit tests + exit
    .\ylaunch.ps1 -c          Unit tests with coverage, then the coverage floors and the CRAP gate
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Alias("s")]   [switch]$Silent,
    [Alias("t")]   [switch]$TestUnit,
    [Alias("ta")]  [switch]$TestAll,
    [Alias("tl")]  [switch]$TestLibrary,
    [Alias("c")]   [switch]$Coverage,
    [Alias("h")]   [switch]$Help,
    [Alias("hl")]  [switch]$HelpLong
)

# PowerShell 5.1 parses the whole file before running it, so the core (PS 7 syntax) is only
# dot-sourced after this gate.
if ($PSVersionTable.PSVersion -lt [version]"7.2") {
    $pwshPath = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $pwshPath -or $PSVersionTable.PSEdition -ne "Desktop") {
        Write-Host "ERROR: PowerShell 7.2+ required. Install from https://aka.ms/powershell" -ForegroundColor Red
        exit 1
    }
    $boundArgs = @()
    foreach ($key in $PSBoundParameters.Keys) {
        $val = $PSBoundParameters[$key]
        if ($val -is [switch]) { if ($val) { $boundArgs += "-$key" } }
        else { $boundArgs += "-$key"; $boundArgs += ($(if ($val -is [array]) { [string]::Join(',', $val) } else { "$val" })) }
    }
    & $pwshPath -NoLogo -NoProfile -File $PSCommandPath @boundArgs
    exit $LASTEXITCODE
}

# ---------- project configuration (the only project-specific part) ----------
$LaunchConfig = @{
    Name         = "OmniEurope.Diagnostics"
    Solution     = "OmniEurope.Diagnostics.slnx"
    OwnedFolders = @("src")
    Tests        = @(
        @{ Key = "Library"; Flag = "TestLibrary"; Alias = "tl"; Kind = "Unit"; Coverage = $true; Project = "tests\OmniEurope.Diagnostics.Tests\OmniEurope.Diagnostics.Tests.csproj" }
    )
    Exceptions   = @{ E2E = "package: no application to drive" }
}

$ErrorActionPreference = "Stop"
$corePath = Join-Path $PSScriptRoot "scripts\ylaunch-core.ps1"
try { . $corePath } catch { Write-Host "ERROR: cannot load ${corePath}: $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
$options = @{}
foreach ($key in $PSBoundParameters.Keys) { $options[$key] = $PSBoundParameters[$key] }
Invoke-YLaunch -Config $LaunchConfig -Root $PSScriptRoot -Options $options
# A green coverage run (-c) also passes the coverage floors (95 % of lines, 85 % of branches, on the merged
# report) and the CRAP gate, no method above 30 outside .config/crap-exceptions.json. Until the launcher
# core runs the gates itself.
if ($Coverage -and $script:YLaunchExitCode -eq 0) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "scripts\coverage-gate.ps1") -CoverageRoot (Join-Path $PSScriptRoot "TestResults\CoverageReport")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "scripts\crap-gate.ps1") -CoverageRoot (Join-Path $PSScriptRoot "TestResults\Coverage")
    exit $LASTEXITCODE
}
exit $script:YLaunchExitCode