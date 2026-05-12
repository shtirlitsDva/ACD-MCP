#Requires -Version 7.0
<#
.SYNOPSIS
  Uninstall ACD-MCP: remove the AutoCAD plugin bundle and deregister the
  acd-mcp stdio MCP server from each supported AI client.

.DESCRIPTION
  Inverse of Install-AcdMcp.ps1. User-authored DTOs in
  %APPDATA%\Acd.Mcp\dto-user\ and saved scripts under
  %APPDATA%\Acd.Mcp\scripts\ are preserved unless -Purge is passed.

.PARAMETER Purge
  Also delete user-authored DTOs, saved scripts, batch-run history, and
  log files. Default: false (preserves user content).

.PARAMETER SkipBundle
  Leave the AutoCAD bundle in place; only deregister MCP clients.

.PARAMETER Force
  Skip the AutoCAD-running check when removing the bundle.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Purge,
    [switch] $SkipBundle,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Skip($msg)  { Write-Host "  · $msg" -ForegroundColor DarkGray }
function Fail($msg)        { Write-Host "  ✗ $msg" -ForegroundColor Red; throw $msg }

function Test-AutoCadRunning {
    @('acad','acadlt','accoreconsole') |
        ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ } | Select-Object -First 1
}

# ─── bundle ─────────────────────────────────────────────────────────────────

if (-not $SkipBundle) {
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
        Write-Skip "Bundle not installed"
    }
}

# ─── client deregistration ──────────────────────────────────────────────────

function Remove-FromTomlTable {
    param([string] $path, [string] $tableName)
    if (-not (Test-Path -LiteralPath $path)) { Write-Skip "Not present: $path"; return }
    $content = Get-Content -LiteralPath $path -Raw
    $pattern = "(?ms)^\s*\[$([regex]::Escape($tableName))\][^\[]*"
    if ($content -match $pattern) {
        $updated = [regex]::Replace($content, $pattern, '')
        if ($PSCmdlet.ShouldProcess($path, "Remove [$tableName]")) {
            Set-Content -LiteralPath $path -Value $updated -NoNewline -Encoding UTF8
            Write-Ok "Removed [$tableName] from $(Split-Path -Leaf $path)"
        }
    } else {
        Write-Skip "No [$tableName] block in $(Split-Path -Leaf $path)"
    }
}

function Remove-FromJsonField {
    param([string] $path, [string] $parent, [string] $key)
    if (-not (Test-Path -LiteralPath $path)) { Write-Skip "Not present: $path"; return }
    try {
        $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 32
    } catch {
        Write-Skip "Failed to parse $path"; return
    }
    if (-not $json.PSObject.Properties[$parent]) { Write-Skip "No '$parent' in $path"; return }
    if (-not $json.$parent.PSObject.Properties[$key]) { Write-Skip "No '$parent.$key' in $path"; return }
    $json.$parent.PSObject.Properties.Remove($key)
    if ($PSCmdlet.ShouldProcess($path, "Remove $parent.$key")) {
        $json | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding UTF8
        Write-Ok "Removed $parent.$key from $(Split-Path -Leaf $path)"
    }
}

Write-Step "Codex"
Remove-FromTomlTable -path (Join-Path $env:USERPROFILE '.codex\config.toml') -tableName 'mcp_servers.acd-mcp'

Write-Step "GitHub Copilot (VS Code)"
foreach ($mcpJson in @(
    Join-Path $env:APPDATA 'Code\User\mcp.json'
    Join-Path $env:APPDATA 'Code - Insiders\User\mcp.json'
)) {
    Remove-FromJsonField -path $mcpJson -parent 'servers' -key 'acd-mcp'
}

Write-Step "Claude Desktop"
Remove-FromJsonField -path (Join-Path $env:APPDATA 'Claude\claude_desktop_config.json') -parent 'mcpServers' -key 'acd-mcp'

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
        } else { Write-Skip "Not present: $dir" }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
