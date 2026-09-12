<#
.SYNOPSIS
    Builds the SolScan release installer (SolScan-Setup.exe) and copies it into
    the repo-root Releases folder, named after the version in Directory.Build.props.

.DESCRIPTION
    - Reads <Version> from Directory.Build.props (single source of truth for
      every project's AssemblyVersion and the bundle's own Bundle/@Version).
    - Builds SolScan.Bundle.wixproj (which pulls in SolScan.Setup.wixproj, which
      in turn publishes SolScan.App) in the given configuration.
    - Copies the resulting SolScan-Setup.exe to
      Releases\SolScan-Setup-<version>.exe.
    - Releases\ is already covered by .gitignore's [Rr]eleases/ rule, so
      built installers never get committed.

    Whichever vendor camera SDK DLLs (ASICamera2.dll/altaircam.dll - see
    SolScan.Infrastructure\ASICamera2.README.md/altaircam.README.md) are present in
    SolScan.Infrastructure\ at build time ride along into the installer automatically
    (SolScan.Setup harvests the publish output, which already copies them there when
    present) - neither file is required for the build to succeed, but a release meant
    for real hardware should have both dropped in first. This script warns, but does
    not fail, when either is missing.

.PARAMETER Configuration
    Build configuration to use. Defaults to Release.

.PARAMETER Force
    Overwrite an existing Releases\SolScan-Setup-<version>.exe if present.
    Without this, the script refuses to overwrite an existing release build
    (bump <Version> in Directory.Build.props for a new build instead).

.PARAMETER SkipBuild
    Skip the dotnet build step and just (re)copy whatever's already in
    SolScan.Bundle\bin\x64\<Configuration>\SolScan-Setup.exe. Useful for
    re-running the copy step after a manual build.

.EXAMPLE
    .\scripts\Build-Release.ps1
    Builds Release and produces Releases\SolScan-Setup-0.1.0.exe

.EXAMPLE
    .\scripts\Build-Release.ps1 -Force
    Same, but overwrites an existing file for that version.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Force,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# Script lives in <repoRoot>\scripts, so its parent is always the repo root
# regardless of the caller's own working directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$bundleProj = Join-Path $repoRoot "SolScan.Bundle\SolScan.Bundle.wixproj"
$releasesDir = Join-Path $repoRoot "Releases"

if (-not (Test-Path $propsPath)) {
    throw "Could not find Directory.Build.props at $propsPath"
}

# Directory.Build.props is the single <Version> every project (and the
# bundle's own Bundle/@Version, via SolScan.Bundle.wixproj's
# DefineConstants=SolScanVersion=$(Version)) picks up - read it here instead
# of taking a -Version parameter so the built installer and its filename
# can never disagree.
$propsContent = Get-Content -Raw $propsPath
if ($propsContent -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw "Could not find <Version> in $propsPath"
}
$version = $Matches[1]
Write-Host "Release version (from Directory.Build.props): $version" -ForegroundColor Cyan

# Non-fatal heads-up only - see this script's own doc comment. A simulator-only build (no real
# camera hardware exercised) is a legitimate reason to ship without either file.
$infraDir = Join-Path $repoRoot "SolScan.Infrastructure"
foreach ($dll in @("ASICamera2.dll", "altaircam.dll")) {
    if (-not (Test-Path (Join-Path $infraDir $dll))) {
        Write-Warning "$dll is not present in SolScan.Infrastructure\ - this release build will NOT include real-hardware support for that camera vendor. See SolScan.Infrastructure\$($dll -replace '\.dll$', '.README.md')."
    }
}

$destName = "SolScan-Setup-$version.exe"
$destPath = Join-Path $releasesDir $destName

# Fail fast, before spending minutes on a build, if this version's file is
# already there - avoids silently clobbering a previous release build.
if ((Test-Path $destPath) -and -not $Force) {
    throw "$destPath already exists. Bump <Version> in Directory.Build.props for a new release, or pass -Force to overwrite."
}

if (-not $SkipBuild) {
    Write-Host "Building $bundleProj ($Configuration)..." -ForegroundColor Cyan
    # Building SolScan.Bundle.wixproj transitively builds SolScan.Setup.wixproj
    # (ProjectReference), which publishes SolScan.App via its own
    # PrepareForBuild hook - one command produces the whole chained bootstrapper.
    & dotnet build $bundleProj -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        # See RASTA's CLAUDE.md: WIX0350 (stale local MSI validation engine) has been a
        # machine-specific quirk there, not an authoring bug - worth one retry
        # with validation suppressed before giving up.
        Write-Warning "Build failed - retrying with -p:SuppressValidation=true (known WIX0350 machine-specific quirk)."
        & dotnet build $bundleProj -c $Configuration -p:SuppressValidation=true
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for $bundleProj (exit code $LASTEXITCODE)."
        }
    }
}

# SolScan.Bundle.wixproj hardcodes OutputName=SolScan-Setup and Platform=x64, so
# the bin path below is fixed regardless of $Configuration.
$builtExe = Join-Path $repoRoot "SolScan.Bundle\bin\x64\$Configuration\SolScan-Setup.exe"
if (-not (Test-Path $builtExe)) {
    throw "Expected build output not found at $builtExe"
}

if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

# Releases\ is matched by .gitignore's [Rr]eleases/ rule, so these
# version-named installers are kept out of source control on purpose.
Copy-Item -Path $builtExe -Destination $destPath -Force
Write-Host "Release installer written to $destPath" -ForegroundColor Green
