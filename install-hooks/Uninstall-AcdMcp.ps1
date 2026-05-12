#Requires -Version 7.0
<#
.SYNOPSIS
  Uninstall ACD-MCP: remove the AutoCAD plugin bundle and deregister the
  acd-mcp stdio MCP server from each detected AI client.

.DESCRIPTION
  Inverse of Install-AcdMcp.ps1. CLI-first like the installer; file-edit
  fallback when no CLI is available.

  User content under %APPDATA%\Acd.Mcp\ (dto-user/, scripts/) and
  %LOCALAPPDATA%\Acd.Mcp\ (logs, batch-run history) is preserved by
  default. Pass -Purge to delete those too.

.PARAMETER Clients
  Which clients to deregister from. 'auto' detects installed clients.

.PARAMETER Purge
  Also delete user-authored DTOs, saved scripts, batch-run history, and
  the diagnostic log.

.PARAMETER SkipBundle
  Leave the AutoCAD bundle in place.

.PARAMETER Force
  Skip the AutoCAD-running check when removing the bundle.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('auto','codex','copilot','claude-desktop','claude-code','none')]
    [string[]] $Clients = @('auto'),

    [switch] $Purge,
    [switch] $SkipBundle,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "    OK  $msg" -ForegroundColor Green }
function Write-Skip2($msg) { Write-Host "    --  $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg) { Write-Host "    !   $msg" -ForegroundColor Yellow }
function Fail($msg)        { Write-Host "    X   $msg" -ForegroundColor Red; throw $msg }

function Test-Command([string] $name) {
    [bool](Get-Command $name -ErrorAction SilentlyContinue)
}
function Test-AutoCadRunning {
    @('acad','acadlt','accoreconsole') |
        ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ } | Select-Object -First 1
}

$serverName = 'acd-mcp'

# Remove a TOML table — same robust regex as the installer.
function Remove-TomlTable([string] $content, [string] $tableName) {
    $escaped = [regex]::Escape($tableName)
    $pattern = "(?m)^\s*\[$escaped\][^\r\n]*\r?\n(?:(?!^\s*\[).*\r?\n?)*"
    [regex]::Replace($content, $pattern, '')
}

# Remove a JSON property safely. Returns $true if the file was modified.
function Remove-JsonProperty([string] $path, [string] $parent, [string] $key) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try {
        $raw  = Get-Content -LiteralPath $path -Raw
        $json = $raw | ConvertFrom-Json -Depth 32
    } catch {
        Write-Warn2 "Failed to parse $path"; return $false
    }
    if (-not $json.PSObject.Properties[$parent]) { return $false }
    if (-not $json.$parent.PSObject.Properties[$key]) { return $false }

    $json.$parent.PSObject.Properties.Remove($key)
    if ($PSCmdlet.ShouldProcess($path, "Remove $parent.$key")) {
        $json | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $path -Encoding UTF8
    }
    return $true
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
        Write-Skip2 "Bundle not installed"
    }
}

# ─── client deregistration ──────────────────────────────────────────────────

function Unregister-Codex {
    Write-Step "Codex"
    if (Test-Command 'codex') {
        if ($PSCmdlet.ShouldProcess($serverName, "codex mcp remove")) {
            & codex mcp remove $serverName 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Ok "Removed via codex CLI"; return }
            Write-Skip2 "codex mcp remove exited $LASTEXITCODE — falling back to file edit"
        }
    } else {
        Write-Skip2 "codex CLI not on PATH"
    }

    $cfg = Join-Path $env:USERPROFILE '.codex\config.toml'
    if (-not (Test-Path -LiteralPath $cfg)) { Write-Skip2 "No config.toml present"; return }
    $existing = Get-Content -LiteralPath $cfg -Raw
    $updated  = Remove-TomlTable $existing "mcp_servers.$serverName"
    if ($updated -eq $existing) {
        Write-Skip2 "[mcp_servers.$serverName] not in config.toml"
    } else {
        if ($PSCmdlet.ShouldProcess($cfg, "Remove [mcp_servers.$serverName]")) {
            Set-Content -LiteralPath $cfg -Value $updated -NoNewline -Encoding UTF8
            Write-Ok "Removed via file edit at $cfg"
        }
    }
}

function Unregister-Copilot {
    Write-Step "GitHub Copilot (VS Code)"
    # VS Code has no documented `code --remove-mcp`. File-edit is the path.
    $any = $false
    foreach ($cfg in @(
        Join-Path $env:APPDATA 'Code\User\mcp.json'
        Join-Path $env:APPDATA 'Code - Insiders\User\mcp.json'
    )) {
        if (Remove-JsonProperty $cfg 'servers' $serverName) {
            Write-Ok "Removed from $cfg"
            $any = $true
        }
    }
    if (-not $any) { Write-Skip2 "No servers.$serverName entry found" }
}

function Unregister-ClaudeDesktop {
    Write-Step "Claude Desktop"
    $cfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    if (Remove-JsonProperty $cfg 'mcpServers' $serverName) {
        Write-Ok "Removed from $cfg"
    } else {
        Write-Skip2 "No mcpServers.$serverName entry found"
    }
}

function Unregister-ClaudeCode {
    Write-Step "Claude Code"
    if (-not (Test-Command 'claude')) { Write-Skip2 "claude CLI not on PATH"; return }
    if ($PSCmdlet.ShouldProcess($serverName, "claude mcp remove")) {
        & claude mcp remove $serverName 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Ok "Removed via claude CLI" }
        else { Write-Skip2 "Not found (or claude mcp remove exited $LASTEXITCODE)" }
    }
}

# Resolve 'auto' / 'none' the same way the installer does.
$effective = if ($Clients -contains 'none') {
    @()
} elseif ($Clients -contains 'auto') {
    $detected = @()
    if ((Test-Command 'codex') -or (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.codex'))) {
        $detected += 'codex'
    }
    if ((Test-Command 'code') -or (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Code\User')) -or
        (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Code - Insiders\User'))) {
        $detected += 'copilot'
    }
    if (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Claude')) {
        $detected += 'claude-desktop'
    }
    $detected
} else {
    $Clients
}

foreach ($client in ($effective | Select-Object -Unique)) {
    switch ($client) {
        'codex'          { Unregister-Codex }
        'copilot'        { Unregister-Copilot }
        'claude-desktop' { Unregister-ClaudeDesktop }
        'claude-code'    { Unregister-ClaudeCode }
    }
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
