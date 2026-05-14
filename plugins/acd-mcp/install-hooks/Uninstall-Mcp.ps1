#Requires -Version 7.0
<#
.SYNOPSIS
  Deregister the acd-mcp stdio MCP server from Codex, GitHub Copilot,
  and Claude Desktop.

.DESCRIPTION
  Inverse of Install-Mcp.ps1. CLI-first like the installer; file-edit
  fallback when no CLI is available.

  Claude Code is intentionally NOT in scope here — Claude Code users
  remove via `/plugin uninstall acd-mcp@acd-mcp`.

  This script does NOT touch user content (dto-user, scripts, logs).
  Use Uninstall-Bundle.ps1 -Purge for that.

.PARAMETER Clients
  Which clients to deregister from. 'auto' detects installed clients.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('auto','codex','copilot','claude-desktop','none')]
    [string[]] $Clients = @('auto')
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

# Same detection as the installer — Codex CLI / VS Code extension /
# Desktop app all share ~/.codex/config.toml.
function Test-CodexInstalled {
    if (Test-Command 'codex') { return $true }
    if (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.codex')) { return $true }
    $extDir = Join-Path $env:USERPROFILE '.vscode\extensions'
    if (Test-Path -LiteralPath $extDir) {
        $hit = Get-ChildItem -LiteralPath $extDir -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -like 'openai.chatgpt*' -or $_.Name -like 'openai.codex*' } |
               Select-Object -First 1
        if ($hit) { return $true }
    }
    return $false
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

# ─── main flow ──────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "ACD-MCP MCP-client uninstaller" -ForegroundColor White
Write-Host ""

$effective = if ($Clients -contains 'none') {
    @()
} elseif ($Clients -contains 'auto') {
    $detected = @()
    if (Test-CodexInstalled) {
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
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
