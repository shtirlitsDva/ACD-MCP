#Requires -Version 7.0
<#
.SYNOPSIS
  Install ACD-MCP: deploy the AutoCAD plugin bundle and register the
  Acd.Mcp.Bridge stdio MCP server with each detected AI client.

.DESCRIPTION
  Idempotent installer. Re-running updates entries in place.

  Path 1 — Claude Code: install the plugin instead, via
    /plugin marketplace add https://github.com/shtirlitsDva/ACD-MCP
    /plugin install acd-mcp@acd-mcp
  Then run THIS script with -Clients none to deploy just the AutoCAD bundle.

  Path 2 — All other clients (Codex, Copilot, Claude Desktop):
  Run this script. It:
    1. Deploys ACD-MCP.bundle to %APPDATA%\Autodesk\ApplicationPlugins\
       (refuses if AutoCAD is running or the on-disk version is newer,
        unless -Force).
    2. Registers acd-mcp with each MCP client detected on the machine,
       using the client's official CLI (`codex mcp add`,
       `code --add-mcp`) where available — file-edit fallback only when
       the CLI is missing or has no idempotent CLI command.

.PARAMETER Clients
  Which clients to register.
    'auto'           — auto-detect every installed client (default)
    'codex'          — Codex CLI
    'copilot'        — VS Code / Insiders (GitHub Copilot)
    'claude-desktop' — Claude Desktop app
    'claude-code'    — Claude Code (only if you do NOT use /plugin install)
    'none'           — skip all MCP wiring (bundle-only run)

.PARAMETER BridgePath
  Absolute path to Acd.Mcp.Bridge.exe. Defaults to
  ..\bin\Acd.Mcp.Bridge.exe relative to this script.

.PARAMETER BundleSource
  Path to the ACD-MCP.bundle source folder. Defaults to
  ..\autocad-bundle\ACD-MCP.bundle relative to this script.

.PARAMETER SkipBundle
  Skip the AutoCAD bundle deployment.

.PARAMETER Force
  Overwrite the AutoCAD bundle even if on-disk is newer, and skip the
  AutoCAD-running check.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1
  # Default: deploys bundle + registers MCP with every detected client.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1 -Clients none
  # Bundle-only — for Claude Code users who already ran /plugin install.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1 -Clients codex -SkipBundle
  # Re-register just Codex without touching the AutoCAD bundle.

.NOTES
  Windows-only (AutoCAD is Windows-only). Run from pwsh 7+.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('auto','codex','copilot','claude-desktop','claude-code','none')]
    [string[]] $Clients = @('auto'),

    [string] $BridgePath,
    [string] $BundleSource,

    [switch] $SkipBundle,
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

if (-not $BridgePath)   { $BridgePath   = Join-Path $pluginRoot 'bin\Acd.Mcp.Bridge.exe' }
if (-not $BundleSource) { $BundleSource = Join-Path $pluginRoot 'autocad-bundle\ACD-MCP.bundle' }

$BridgePath   = [System.IO.Path]::GetFullPath($BridgePath)
$BundleSource = [System.IO.Path]::GetFullPath($BundleSource)

$bundleTargetRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$bundleTarget     = Join-Path $bundleTargetRoot 'ACD-MCP.bundle'

# Server name in every client's config — kept identical across clients so a
# user looking at one config can find the same name in the others.
$serverName = 'acd-mcp'

# ─── helpers ────────────────────────────────────────────────────────────────

function Test-Command([string] $name) {
    [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

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
    # ConvertTo-Json on PowerShell 7 emits no BOM via Set-Content -Encoding UTF8.
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

# ─── bundle deploy ──────────────────────────────────────────────────────────

function Install-Bundle {
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
    # Require at least one .dll — the in-repo Contents/ only has .gitkeep
    # until scripts\Build-Release.ps1 populates it. Without this guard the
    # installer happily deploys a non-functional bundle.
    $dllCount = @(Get-ChildItem (Join-Path $BundleSource 'Contents') -File -Filter '*.dll' -ErrorAction SilentlyContinue).Count
    if ($dllCount -eq 0) {
        Fail "Bundle Contents/ has no .dll files. Build first: pwsh scripts\Build-Release.ps1"
    }

    $sourceVer   = Get-BundleVersion $BundleSource
    $existingVer = if (Test-Path -LiteralPath $bundleTarget) { Get-BundleVersion $bundleTarget } else { $null }

    if ($existingVer -and -not $Force) {
        if ($sourceVer -le $existingVer) {
            Write-Skip2 "Installed bundle ($existingVer) is newer or equal to source ($sourceVer). -Force to overwrite."
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

function Register-ClaudeCode {
    Write-Step "Claude Code"

    if (-not (Test-Command 'claude')) {
        Write-Skip2 "claude CLI not on PATH"
        return
    }
    # Prefer the upsert via remove-then-add — `claude mcp` has its own
    # CLI matching codex's surface, and remove exits non-zero only when the
    # name is unknown (which we don't care about for upsert).
    if ($PSCmdlet.ShouldProcess($serverName, "claude mcp remove (idempotent upsert)")) {
        & claude mcp remove $serverName 2>$null | Out-Null
    }
    if ($PSCmdlet.ShouldProcess($serverName, "claude mcp add $BridgePath")) {
        & claude mcp add $serverName $BridgePath
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Registered via claude CLI"
            Write-Warn2 "Heads-up: if you also use '/plugin install acd-mcp', the plugin would register the same server. Use one or the other, not both."
        } else {
            Write-Warn2 "claude mcp add exited $LASTEXITCODE"
        }
    }
}

# ─── main flow ──────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "ACD-MCP installer" -ForegroundColor White
Write-Host "  Bridge: $BridgePath"
Write-Host "  Bundle: $BundleSource"
Write-Host ""

if (-not (Test-Path -LiteralPath $BridgePath)) {
    Write-Warn2 "Acd.Mcp.Bridge.exe not found at $BridgePath."
    Write-Warn2 "Build with: pwsh scripts\Build-Release.ps1 (or dotnet publish src\Acd.Mcp.Bridge)"
    Write-Warn2 "MCP registration will write paths that don't resolve until the file exists."
}

if (-not $SkipBundle) { Install-Bundle }

# Resolve 'auto' / 'none' to a concrete client list.
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
    # claude-code is NEVER in auto-detect — `/plugin install` is the canonical
    # path. Pass `-Clients claude-code` to opt in for standalone wiring.
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
        'claude-code'    { Register-ClaudeCode }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next:" -ForegroundColor White
Write-Host "  1. Launch AutoCAD 2025+. The bundle autoloads (look for 'ACDMCP' commands)."
Write-Host "  2. Run ACDMCP_START to open the named pipe."
Write-Host "  3. (Optional) Run ACDMCP_PALETTE for the dockable REPL/BATCH UI."
Write-Host "  4. Restart your AI client so it picks up the new MCP server."
Write-Host ""
