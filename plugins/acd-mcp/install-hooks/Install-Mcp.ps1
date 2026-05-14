#Requires -Version 7.0
<#
.SYNOPSIS
  Register the acd-mcp stdio MCP server with Codex, GitHub Copilot, and
  Claude Desktop.

.DESCRIPTION
  Idempotent. Re-running updates entries in place.

  Claude Code is intentionally NOT in scope here — Claude Code users
  install via `/plugin install acd-mcp@acd-mcp` which wires the MCP
  through the plugin's .mcp.json. Don't double-register.

  Companion script: Install-Bundle.ps1 (deploys the AutoCAD plugin DLLs).
  Run that one too — the MCP roundtrip needs both ends.

.PARAMETER Clients
  Which clients to register.
    'auto'           — auto-detect every installed client (default)
    'codex'          — Codex CLI / VS Code extension / Desktop app
    'copilot'        — VS Code / Insiders (GitHub Copilot)
    'claude-desktop' — Claude Desktop app
    'none'           — register nothing (use when you only want path validation)

.PARAMETER BridgePath
  Absolute path to Acd.Mcp.Bridge.exe. Defaults to
  ..\bin\Acd.Mcp.Bridge.exe relative to this script.

.EXAMPLE
  pwsh .\Install-Mcp.ps1
  # Auto-detect installed clients and register acd-mcp with each.

.EXAMPLE
  pwsh .\Install-Mcp.ps1 -Clients codex,copilot
  # Register only with Codex + Copilot.

.NOTES
  Windows-only (AutoCAD is Windows-only). Run from pwsh 7+.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('auto','codex','copilot','claude-desktop','none')]
    [string[]] $Clients = @('auto'),

    [string] $BridgePath
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

if (-not $BridgePath) { $BridgePath = Join-Path $pluginRoot 'bin\Acd.Mcp.Bridge.exe' }
$BridgePath = [System.IO.Path]::GetFullPath($BridgePath)

# Server name in every client's config — kept identical across clients so a
# user looking at one config can find the same name in the others.
$serverName = 'acd-mcp'

# ─── helpers ────────────────────────────────────────────────────────────────

function Test-Command([string] $name) {
    [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

# Codex ships in three flavors that all read the same ~/.codex/config.toml:
# CLI (`codex`), VS Code extension (openai.chatgpt), and macOS desktop app.
# Detect any of them — the file-edit fallback writes to the shared config
# either way.
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

# Upsert a property into a JSON object: creates parent if missing,
# replaces or appends the leaf key.
function Set-JsonProperty {
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)][string] $Parent,
          [Parameter(Mandatory)][string] $Key, [Parameter(Mandatory)] $Value)

    if (-not $Object.PSObject.Properties[$Parent]) {
        $Object | Add-Member -NotePropertyName $Parent -NotePropertyValue ([pscustomobject]@{})
    }
    if ($Object.$Parent.PSObject.Properties[$Key]) {
        $Object.$Parent.$Key = $Value
    } else {
        $Object.$Parent | Add-Member -NotePropertyName $Key -NotePropertyValue $Value
    }
}

# Read a JSON file or return an empty object. Robust to BOM and to a
# corrupted file (logs and falls back to empty).
function Read-Json([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return [pscustomobject]@{} }
    try {
        $raw = Get-Content -LiteralPath $path -Raw
        if ([string]::IsNullOrWhiteSpace($raw)) { return [pscustomobject]@{} }
        return $raw | ConvertFrom-Json -Depth 32
    } catch {
        Write-Warn2 "Failed to parse $path ($($_.Exception.Message)) — starting from empty"
        return [pscustomobject]@{}
    }
}

function Write-Json([string] $path, $object) {
    $dir = Split-Path -Parent $path
    if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $object | ConvertTo-Json -Depth 32
    Set-Content -LiteralPath $path -Value $json -Encoding UTF8
}

# Find and remove a TOML table (and its body) from a TOML string. A table
# body is everything from the header line up to (but not including) the next
# table header at column 0. This is robust against `[` characters that
# appear inside string values or arrays — those never appear at the start
# of a line (after optional whitespace).
function Remove-TomlTable([string] $content, [string] $tableName) {
    $escaped = [regex]::Escape($tableName)
    $pattern = "(?m)^\s*\[$escaped\][^\r\n]*\r?\n(?:(?!^\s*\[).*\r?\n?)*"
    return [regex]::Replace($content, $pattern, '')
}

function Set-TomlTable([string] $content, [string] $tableName, [string] $body) {
    $stripped = Remove-TomlTable $content $tableName
    $sep = if ($stripped -and -not $stripped.EndsWith("`n")) { "`n" } else { '' }
    $stripped + $sep + "`n[$tableName]`n$body"
}

# ─── client registrations ───────────────────────────────────────────────────
# Each function follows the same shape:
#   1. Try the client's official CLI for an idempotent upsert.
#   2. Fall back to a file edit if the CLI isn't available.
#   3. Log clearly which path was taken.

function Register-Codex {
    Write-Step "Codex"

    if (Test-Command 'codex') {
        # Documented CLI: `codex mcp add <name> [-- <command>]`. No documented
        # idempotency, so remove-then-add — `remove` is forgiving when the
        # entry doesn't exist (we suppress non-zero exit).
        if ($PSCmdlet.ShouldProcess($serverName, "codex mcp remove (idempotent upsert)")) {
            & codex mcp remove $serverName 2>$null | Out-Null
        }
        if ($PSCmdlet.ShouldProcess($serverName, "codex mcp add -- $BridgePath")) {
            & codex mcp add $serverName -- $BridgePath
            if ($LASTEXITCODE -eq 0) { Write-Ok "Registered via codex CLI"; return }
            Write-Warn2 "codex mcp add exited $LASTEXITCODE — falling back to file edit"
        }
    } else {
        Write-Skip2 "codex CLI not on PATH — using file edit"
    }

    # File-edit fallback.
    $cfg = Join-Path $env:USERPROFILE '.codex\config.toml'
    $existing = if (Test-Path -LiteralPath $cfg) { Get-Content -LiteralPath $cfg -Raw } else { '' }
    $body = 'command = "' + ($BridgePath -replace '\\','\\') + '"' + "`n"
    $updated = Set-TomlTable $existing "mcp_servers.$serverName" $body

    if ($PSCmdlet.ShouldProcess($cfg, "Write [mcp_servers.$serverName] block")) {
        New-Item -ItemType Directory -Path (Split-Path $cfg) -Force | Out-Null
        Set-Content -LiteralPath $cfg -Value $updated -NoNewline -Encoding UTF8
        Write-Ok "Registered via file edit at $cfg"
        Write-Warn2 "Heads-up: the Codex VS Code extension sometimes fails to pick up config.toml changes (openai/codex#6465). If it doesn't see the server, reload the window with Ctrl+Shift+P -> 'Developer: Reload Window'."
    }
}

function Register-Copilot {
    Write-Step "GitHub Copilot (VS Code)"

    if (Test-Command 'code') {
        # `code --add-mcp '<json>'` is the documented upsert. Doc doesn't say
        # whether it deduplicates, but VS Code merges into mcp.json keyed by
        # `name`, so re-running is safe.
        $entry = @{
            name    = $serverName
            command = $BridgePath
            type    = 'stdio'
        } | ConvertTo-Json -Compress
        if ($PSCmdlet.ShouldProcess($serverName, "code --add-mcp")) {
            & code --add-mcp $entry
            if ($LASTEXITCODE -eq 0) { Write-Ok "Registered via code CLI"; return }
            Write-Warn2 "code --add-mcp exited $LASTEXITCODE — falling back to file edit"
        }
    } else {
        Write-Skip2 "code CLI not on PATH — using file edit"
    }

    # File-edit fallback. Write to every detected edition.
    $candidates = @(
        Join-Path $env:APPDATA 'Code\User\mcp.json'
        Join-Path $env:APPDATA 'Code - Insiders\User\mcp.json'
    )
    $wrote = $false
    foreach ($cfg in $candidates) {
        $dir = Split-Path -Parent $cfg
        if (-not (Test-Path -LiteralPath $dir)) {
            Write-Skip2 "Not installed: $dir"
            continue
        }
        $json = Read-Json $cfg
        Set-JsonProperty -Object $json -Parent 'servers' -Key $serverName -Value ([pscustomobject]@{
            type    = 'stdio'
            command = $BridgePath
        })
        if ($PSCmdlet.ShouldProcess($cfg, "Write servers.$serverName")) {
            Write-Json $cfg $json
            Write-Ok "Registered via file edit at $cfg"
            $wrote = $true
        }
    }
    if (-not $wrote) {
        Write-Skip2 "No VS Code edition detected"
    }
}

function Register-ClaudeDesktop {
    Write-Step "Claude Desktop"

    # Claude Desktop has no CLI add command — file edit is the only path.
    $cfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $cfg))) {
        Write-Skip2 "Claude Desktop not installed"
        return
    }
    $json = Read-Json $cfg
    Set-JsonProperty -Object $json -Parent 'mcpServers' -Key $serverName -Value ([pscustomobject]@{
        command = $BridgePath
    })
    if ($PSCmdlet.ShouldProcess($cfg, "Write mcpServers.$serverName")) {
        Write-Json $cfg $json
        Write-Ok "Registered at $cfg"
    }
}

# ─── main flow ──────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "ACD-MCP MCP-client installer" -ForegroundColor White
Write-Host "  Bridge: $BridgePath"
Write-Host ""

if (-not (Test-Path -LiteralPath $BridgePath)) {
    Write-Warn2 "Acd.Mcp.Bridge.exe not found at $BridgePath."
    Write-Warn2 "Build with: pwsh scripts\Build-Release.ps1 (or dotnet publish src\Acd.Mcp.Bridge)"
    Write-Warn2 "MCP registration will write paths that don't resolve until the file exists."
}

# Resolve 'auto' / 'none' to a concrete client list.
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
    if (-not $detected) {
        Write-Warn2 "Auto-detect found no clients. Pass -Clients explicitly or install one."
    }
    $detected
} else {
    $Clients
}

foreach ($client in ($effective | Select-Object -Unique)) {
    switch ($client) {
        'codex'          { Register-Codex }
        'copilot'        { Register-Copilot }
        'claude-desktop' { Register-ClaudeDesktop }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor White
Write-Host "  1. Restart your AI client so it picks up the new MCP server."
Write-Host "  2. Deploy the AutoCAD plugin bundle if you haven't: pwsh .\Install-Bundle.ps1"
Write-Host "  3. In AutoCAD: ACDMCP_START to open the pipe."
Write-Host ""
