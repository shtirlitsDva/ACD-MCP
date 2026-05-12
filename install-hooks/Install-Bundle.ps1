#Requires -Version 7.0
<#
.SYNOPSIS
  Deploy the ACD-MCP AutoCAD plugin bundle to %APPDATA%\Autodesk\
  ApplicationPlugins\.

.DESCRIPTION
  Pure bundle-deploy. Run this once per machine (and again on every
  upgrade) so AutoCAD autoloads the plugin DLLs at startup.

  Idempotent. Refuses to overwrite if AutoCAD is running, or if the
  on-disk bundle is newer than the source — unless -Force.

  Companion script: Install-Mcp.ps1 (registers acd-mcp with non-Claude-Code
  MCP clients). Claude Code users do that side via /plugin install.

.PARAMETER BundleSource
  Path to the ACD-MCP.bundle source folder. Defaults to
  ..\autocad-bundle\ACD-MCP.bundle relative to this script.

.PARAMETER Force
  Overwrite the AutoCAD bundle even if on-disk is newer, and skip the
  AutoCAD-running check.

.EXAMPLE
  pwsh .\Install-Bundle.ps1
  # Deploy the bundle from the sibling autocad-bundle/ folder.

.EXAMPLE
  pwsh .\Install-Bundle.ps1 -Force
  # Overwrite even if AutoCAD is running. Bundle copy may fail if AutoCAD
  # has the DLLs open — close it first whenever possible.

.NOTES
  Windows-only. Run from pwsh 7+.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $BundleSource,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── pretty printing ────────────────────────────────────────────────────────

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Skip2($msg) { Write-Host "    --  $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg) { Write-Host "    !   $msg" -ForegroundColor Yellow }
function Fail($msg)        { Write-Host "    X   $msg" -ForegroundColor Red; throw $msg }

# ─── paths ──────────────────────────────────────────────────────────────────

$scriptRoot = Split-Path -Parent $PSCommandPath
$pluginRoot = Split-Path -Parent $scriptRoot

if (-not $BundleSource) { $BundleSource = Join-Path $pluginRoot 'autocad-bundle\ACD-MCP.bundle' }
$BundleSource = [System.IO.Path]::GetFullPath($BundleSource)

$bundleTargetRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$bundleTarget     = Join-Path $bundleTargetRoot 'ACD-MCP.bundle'

# ─── helpers ────────────────────────────────────────────────────────────────

function Test-AutoCadRunning {
    @('acad','acadlt','accoreconsole') |
        ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ } | Select-Object -First 1
}

function Get-BundleVersion([string] $bundleDir) {
    $pkg = Join-Path $bundleDir 'PackageContents.xml'
    if (-not (Test-Path -LiteralPath $pkg)) { return $null }
    try {
        $xml = [xml](Get-Content -LiteralPath $pkg -Raw)
        return [version]$xml.ApplicationPackage.AppVersion
    } catch { return $null }
}

# ─── main flow ──────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "ACD-MCP bundle installer" -ForegroundColor White
Write-Host "  Source: $BundleSource"
Write-Host "  Target: $bundleTarget"
Write-Host ""

Write-Step "AutoCAD bundle"

if (-not (Test-Path -LiteralPath $BundleSource)) {
    Fail "Bundle source not found: $BundleSource"
}
if (-not (Test-Path -LiteralPath (Join-Path $BundleSource 'PackageContents.xml'))) {
    Fail "Bundle source has no PackageContents.xml: $BundleSource"
}
if (-not (Test-Path -LiteralPath (Join-Path $BundleSource 'Contents'))) {
    Fail "Bundle source has no Contents/ folder: $BundleSource"
}
# Require at least one .dll — the in-repo Contents/ only has .gitkeep until
# scripts\Build-Release.ps1 populates it. Without this guard the installer
# happily deploys a non-functional bundle.
$dllCount = @(Get-ChildItem (Join-Path $BundleSource 'Contents') -File -Filter '*.dll' -ErrorAction SilentlyContinue).Count
if ($dllCount -eq 0) {
    Fail "Bundle Contents/ has no .dll files. Build first: pwsh scripts\Build-Release.ps1"
}

$sourceVer   = Get-BundleVersion $BundleSource
$existingVer = if (Test-Path -LiteralPath $bundleTarget) { Get-BundleVersion $bundleTarget } else { $null }

if ($existingVer -and -not $Force) {
    if ($sourceVer -le $existingVer) {
        Write-Skip2 "Installed bundle ($existingVer) is newer or equal to source ($sourceVer). -Force to overwrite."
        Write-Host ""
        Write-Host "Done." -ForegroundColor Green
        return
    }
}

$running = Test-AutoCadRunning
if ($running -and -not $Force) {
    Fail "AutoCAD is running (PID $($running.Id)). Close it and re-run, or pass -Force."
}
if ($running -and $Force) {
    Write-Warn2 "AutoCAD is running but -Force was passed. Bundle copy may fail with locked-file errors."
}

if ($PSCmdlet.ShouldProcess($bundleTarget, "Deploy ACD-MCP.bundle v$sourceVer")) {
    if (Test-Path -LiteralPath $bundleTarget) {
        Remove-Item -LiteralPath $bundleTarget -Recurse -Force
    }
    New-Item -ItemType Directory -Path $bundleTargetRoot -Force | Out-Null
    Copy-Item -LiteralPath $BundleSource -Destination $bundleTargetRoot -Recurse -Force
    Write-Ok "Deployed bundle v$sourceVer to $bundleTarget"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor White
Write-Host "  1. Launch AutoCAD 2025+. The bundle autoloads (look for 'ACDMCP' commands)."
Write-Host "  2. Run ACDMCP_START to open the named pipe."
Write-Host "  3. Connect with your AI client. Claude Code: /plugin install acd-mcp@acd-mcp."
Write-Host "                                  Others: pwsh .\Install-Mcp.ps1"
Write-Host ""
