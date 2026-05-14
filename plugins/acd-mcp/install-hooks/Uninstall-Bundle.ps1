#Requires -Version 7.0
<#
.SYNOPSIS
  Remove the ACD-MCP AutoCAD plugin bundle from %APPDATA%\Autodesk\
  ApplicationPlugins\, and optionally purge user data.

.DESCRIPTION
  Inverse of Install-Bundle.ps1. Refuses while AutoCAD is running unless
  -Force.

  -Purge also wipes user-authored content under %APPDATA%\Acd.Mcp\ and
  %LOCALAPPDATA%\Acd.Mcp\ (dto-user, saved scripts, batch-run history,
  diagnostic log). Lives on this side of the split because the bundle is
  what creates that data — removing the bundle is the "I'm done with
  ACD-MCP" signal. Uninstall-Mcp.ps1 leaves user data alone.

.PARAMETER Force
  Skip the AutoCAD-running check when removing the bundle.

.PARAMETER Purge
  Also delete dto-user/, scripts/, batch-runs/, log.txt.

.EXAMPLE
  pwsh .\Uninstall-Bundle.ps1
  # Remove the bundle; leave user content intact.

.EXAMPLE
  pwsh .\Uninstall-Bundle.ps1 -Purge
  # Remove the bundle AND wipe all user content.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Force,
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Skip2($msg) { Write-Host "    --  $msg" -ForegroundColor DarkGray }
function Fail($msg)        { Write-Host "    X   $msg" -ForegroundColor Red; throw $msg }

function Test-AutoCadRunning {
    @('acad','acadlt','accoreconsole') |
        ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ } | Select-Object -First 1
}

# ─── bundle ─────────────────────────────────────────────────────────────────

Write-Step "AutoCAD bundle"
$bundleTarget = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\ACD-MCP.bundle'
if (Test-Path -LiteralPath $bundleTarget) {
    $running = Test-AutoCadRunning
    if ($running -and -not $Force) {
        Fail "AutoCAD is running (PID $($running.Id)). Close it and re-run, or pass -Force."
    }
    if ($PSCmdlet.ShouldProcess($bundleTarget, "Remove bundle")) {
        Remove-Item -LiteralPath $bundleTarget -Recurse -Force
        Write-Ok "Removed $bundleTarget"
    }
} else {
    Write-Skip2 "Bundle not installed"
}

# ─── purge user data ────────────────────────────────────────────────────────

if ($Purge) {
    Write-Step "Purge user content"
    foreach ($dir in @(
        Join-Path $env:APPDATA       'Acd.Mcp'
        Join-Path $env:LOCALAPPDATA  'Acd.Mcp'
    )) {
        if (Test-Path -LiteralPath $dir) {
            if ($PSCmdlet.ShouldProcess($dir, "Recursively remove")) {
                Remove-Item -LiteralPath $dir -Recurse -Force
                Write-Ok "Removed $dir"
            }
        } else { Write-Skip2 "Not present: $dir" }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
