#Requires -Version 7.0
<#
.SYNOPSIS
  Refresh Claude Code's local plugin cache for ACD-MCP from this repo's
  bin/. Closes V3-H2 in CRASH_TEST_V3_JOURNAL.md.

.DESCRIPTION
  Claude Code resolves `${CLAUDE_PLUGIN_ROOT}/bin/Acd.Mcp.Bridge.exe`
  (from .mcp.json) to its plugin cache:
    ~/.claude/plugins/cache/acd-mcp/acd-mcp/<version>/bin/
  The cache is a SNAPSHOT taken at `/plugin install` time; subsequent
  rebuilds of the bridge (or git pulls of the repo) do NOT update what
  the running bridge actually loads. Symptom: agent calls
  `autocad_batch_list_files` and sees the OLD bridge's behaviour
  despite a fresh `dotnet publish`.

  This script copies the repo's bin/ contents into every matching cache
  version directory so the next bridge spawn picks up the fresh bits.

  Bridge processes that are CURRENTLY running keep their old code until
  they exit. Killing them mid-session would disconnect Claude Code's
  in-session MCP server (V3-H1 — the harness does not auto-respawn);
  prefer to /reload-plugins instead. This script does not kill bridges.

.PARAMETER Publish
  Also run `dotnet publish` first so bin/ holds a fresh Release build
  of Acd.Mcp.Bridge before the copy. Equivalent to running
  scripts/Build-Release.ps1's publish step.

.PARAMETER WhatIf
  Standard PowerShell -WhatIf. Print the copy operations without
  performing them.

.EXAMPLE
  pwsh scripts/Refresh-PluginCache.ps1
  # Copy current bin/ into every cache version dir.

.EXAMPLE
  pwsh scripts/Refresh-PluginCache.ps1 -Publish
  # dotnet publish bridge -> bin/, then refresh cache.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  + $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "  ! $msg" -ForegroundColor Yellow }
function Fail($msg)       { Write-Host "  - $msg" -ForegroundColor Red; throw $msg }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$repoBin  = Join-Path $repoRoot 'bin'

# --- optional publish -----------------------------------------------------

if ($Publish) {
    Write-Step "dotnet publish Acd.Mcp.Bridge -> bin/"
    # Match Build-Release.ps1's behaviour: wipe the existing bin/ files
    # so removed transitive deps from a prior build don't linger, then
    # publish in Release.
    if (Test-Path $repoBin) {
        Get-ChildItem $repoBin -File | Remove-Item -Force
    }
    dotnet publish (Join-Path $repoRoot 'src/Acd.Mcp.Bridge/Acd.Mcp.Bridge.csproj') `
        -c Release -o $repoBin --self-contained false -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { Fail "Bridge publish failed" }
    # Drop pdbs from the committed bin/ — Build-Release.ps1 convention.
    Get-ChildItem $repoBin -File -Filter '*.pdb' | Remove-Item -Force
    Write-Ok "Bridge published to $repoBin"
}

if (-not (Test-Path $repoBin)) {
    Fail "$repoBin does not exist. Run with -Publish or scripts/Build-Release.ps1 first."
}

# --- find cache dirs ------------------------------------------------------

$cacheRoot = Join-Path $env:USERPROFILE '.claude/plugins/cache/acd-mcp/acd-mcp'
if (-not (Test-Path $cacheRoot)) {
    Write-Warn2 "No plugin cache at $cacheRoot — ACD-MCP not installed via Claude Code on this machine, or installed under a different name. Nothing to refresh."
    exit 0
}

# Every version subdir under .../acd-mcp/acd-mcp/<version>/bin/ counts.
# Usually there's exactly one; we refresh them all to stay robust to
# /plugin update having left an older copy behind.
$versionDirs = Get-ChildItem $cacheRoot -Directory -ErrorAction SilentlyContinue
if (-not $versionDirs) {
    Write-Warn2 "Cache root $cacheRoot exists but contains no version subdirs. Nothing to refresh."
    exit 0
}

# --- copy -----------------------------------------------------------------

$repoBinFiles = Get-ChildItem $repoBin -File
if (-not $repoBinFiles) {
    Fail "$repoBin is empty. Did the publish fail?"
}

$refreshed = 0
foreach ($vd in $versionDirs) {
    $cacheBin = Join-Path $vd.FullName 'bin'
    if (-not (Test-Path $cacheBin)) {
        Write-Warn2 "Skipping $($vd.Name): no bin/ subdirectory"
        continue
    }
    Write-Step "Refreshing $cacheBin"
    # Check for a running bridge under this cache path — refresh still
    # works (file copy overwrites the .dll; the .exe is just a stub),
    # but the running process keeps its old in-memory code until exit.
    $liveBridges = Get-Process Acd.Mcp.Bridge -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and (Split-Path $_.Path -Parent) -eq $cacheBin }
    if ($liveBridges) {
        Write-Warn2 "$(($liveBridges | Measure-Object).Count) live bridge(s) under this cache (PIDs: $($liveBridges.Id -join ', '))."
        Write-Warn2 "After this refresh, run /reload-plugins in Claude Code to respawn them with the new dll."
    }
    foreach ($f in $repoBinFiles) {
        $dest = Join-Path $cacheBin $f.Name
        if ($PSCmdlet.ShouldProcess($dest, "Copy from $($f.FullName)")) {
            try {
                Copy-Item $f.FullName $dest -Force
            } catch {
                Write-Warn2 "  $($f.Name): $($_.Exception.Message)"
            }
        }
    }
    Write-Ok "$($vd.Name): $($repoBinFiles.Count) files copied"
    $refreshed++
}

Write-Host ""
if ($refreshed -gt 0) {
    Write-Host "Refreshed $refreshed cache dir(s). To activate the new bridge in your current Claude Code session, run /reload-plugins." -ForegroundColor Green
} else {
    Write-Host "No cache dirs refreshed." -ForegroundColor Yellow
}
