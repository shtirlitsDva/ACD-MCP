#Requires -Version 7.0
<#
.SYNOPSIS
  Install ACD-MCP: deploy the AutoCAD plugin bundle and register the
  Acd.Mcp.Bridge stdio MCP server with each supported AI client.

.DESCRIPTION
  This is the second-path installer for non-Claude-Code clients (Codex,
  GitHub Copilot, Claude Desktop) and for the AutoCAD-side .bundle.

  Claude Code users do NOT need this for MCP wiring — `/plugin install`
  reads .mcp.json from the plugin and wires Bridge.exe automatically.
  Claude Code users still need this script for the AutoCAD bundle
  (the Claude plugin host cannot write into %APPDATA%\Autodesk\).

  Idempotent: re-running updates entries in place rather than duplicating.

.PARAMETER Clients
  Which MCP clients to register the server with. Defaults to every
  client whose config file is detected on the machine.
    'codex'           — ~/.codex/config.toml
    'copilot'         — %APPDATA%\Code\User\mcp.json (and Insiders)
    'claude-desktop'  — %APPDATA%\Claude\claude_desktop_config.json
  Pass 'none' to skip MCP registration entirely (bundle-only install).

.PARAMETER BridgePath
  Absolute path to Acd.Mcp.Bridge.exe. Defaults to
  ..\bin\Acd.Mcp.Bridge.exe relative to this script.

.PARAMETER BundleSource
  Path to the ACD-MCP.bundle folder to deploy. Defaults to
  ..\autocad-bundle\ACD-MCP.bundle relative to this script.

.PARAMETER SkipBundle
  Skip the AutoCAD bundle deployment. Use when re-registering MCP
  clients without touching the AutoCAD-side plugin.

.PARAMETER Force
  Overwrite the AutoCAD bundle even if the on-disk version is newer
  or equal. Skips the AutoCAD-running check too. Use with care.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1
  # Deploys bundle + registers MCP with every client found.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1 -Clients codex,copilot -SkipBundle
  # Only registers MCP for those two clients.

.EXAMPLE
  pwsh .\Install-AcdMcp.ps1 -Clients none
  # Bundle-only — useful for Claude Code users who already ran /plugin install.

.NOTES
  Run from PowerShell 7+ (the script uses pipeline-chain operators).
  Windows-only — AutoCAD is Windows-only.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('codex','copilot','claude-desktop','none','auto')]
    [string[]] $Clients = @('auto'),

    [string] $BridgePath,

    [string] $BundleSource,

    [switch] $SkipBundle,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─── paths ──────────────────────────────────────────────────────────────────

$scriptRoot = Split-Path -Parent $PSCommandPath
$pluginRoot = Split-Path -Parent $scriptRoot

if (-not $BridgePath) {
    $BridgePath = Join-Path $pluginRoot 'bin\Acd.Mcp.Bridge.exe'
}
if (-not $BundleSource) {
    $BundleSource = Join-Path $pluginRoot 'autocad-bundle\ACD-MCP.bundle'
}

$BridgePath   = [System.IO.Path]::GetFullPath($BridgePath)
$BundleSource = [System.IO.Path]::GetFullPath($BundleSource)

$bundleTargetRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$bundleTarget     = Join-Path $bundleTargetRoot 'ACD-MCP.bundle'

# ─── pretty printing ────────────────────────────────────────────────────────

function Write-Step($msg)  { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Skip($msg)  { Write-Host "  · $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg) { Write-Host "  ! $msg" -ForegroundColor Yellow }
function Fail($msg)        { Write-Host "  ✗ $msg" -ForegroundColor Red; throw $msg }

# ─── AutoCAD-running guard ──────────────────────────────────────────────────

function Test-AutoCadRunning {
    @('acad','acadlt','accoreconsole') |
        ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue } |
        Where-Object { $_ } | Select-Object -First 1
}

# ─── bundle version parsing ─────────────────────────────────────────────────
# PackageContents.xml carries <ApplicationPackage AppVersion="x.y.z" ...>

function Get-BundleVersion([string] $bundleDir) {
    $pkg = Join-Path $bundleDir 'PackageContents.xml'
    if (-not (Test-Path -LiteralPath $pkg)) { return $null }
    try {
        $xml = [xml](Get-Content -LiteralPath $pkg -Raw)
        return [version]$xml.ApplicationPackage.AppVersion
    } catch {
        return $null
    }
}

# ─── bundle deployment ──────────────────────────────────────────────────────

function Install-Bundle {
    Write-Step "AutoCAD bundle"

    if (-not (Test-Path -LiteralPath $BundleSource)) {
        Fail "Bundle source not found: $BundleSource"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $BundleSource 'PackageContents.xml'))) {
        Fail "Bundle source has no PackageContents.xml: $BundleSource"
    }

    $sourceVer = Get-BundleVersion $BundleSource
    $existingVer = if (Test-Path -LiteralPath $bundleTarget) { Get-BundleVersion $bundleTarget } else { $null }

    if ($existingVer -and -not $Force) {
        if ($sourceVer -le $existingVer) {
            Write-Skip "Installed bundle ($existingVer) is newer or equal to source ($sourceVer). Use -Force to overwrite."
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

    if ($PSCmdlet.ShouldProcess($bundleTarget, "Deploy ACD-MCP.bundle")) {
        if (Test-Path -LiteralPath $bundleTarget) {
            Remove-Item -LiteralPath $bundleTarget -Recurse -Force
        }
        New-Item -ItemType Directory -Path $bundleTargetRoot -Force | Out-Null
        Copy-Item -LiteralPath $BundleSource -Destination $bundleTargetRoot -Recurse -Force
        Write-Ok "Deployed bundle v$sourceVer to $bundleTarget"
    }
}

# ─── client config writers ──────────────────────────────────────────────────
# Each writer is idempotent: it updates an existing acd-mcp entry in place
# or creates one if missing. Other entries in the file are preserved.

function Register-Codex {
    $cfg = Join-Path $env:USERPROFILE '.codex\config.toml'
    Write-Step "Codex CLI ($cfg)"

    New-Item -ItemType Directory -Path (Split-Path $cfg) -Force | Out-Null

    # TOML editing without a TOML library: locate the table header
    # `[mcp_servers.acd-mcp]`, drop everything until the next table header
    # or EOF, then write the replacement block.
    $existing = if (Test-Path -LiteralPath $cfg) { Get-Content -LiteralPath $cfg -Raw } else { '' }

    $block = @"
[mcp_servers.acd-mcp]
command = "$($BridgePath.Replace('\','\\'))"
"@

    $pattern = '(?ms)^\s*\[mcp_servers\.acd-mcp\][^\[]*'
    if ($existing -match $pattern) {
        $updated = [regex]::Replace($existing, $pattern, ($block + "`n"))
    } else {
        $sep = if ($existing -and -not $existing.EndsWith("`n")) { "`n" } else { '' }
        $updated = $existing + $sep + "`n" + $block + "`n"
    }

    if ($PSCmdlet.ShouldProcess($cfg, "Write [mcp_servers.acd-mcp] block")) {
        Set-Content -LiteralPath $cfg -Value $updated -NoNewline -Encoding UTF8
        Write-Ok "Registered with Codex"
    }
}

function Register-Copilot {
    # VS Code user-level mcp.json. Path varies by edition; write the entry
    # to every flavor of Code we detect.
    $candidates = @(
        Join-Path $env:APPDATA 'Code\User\mcp.json',
        Join-Path $env:APPDATA 'Code - Insiders\User\mcp.json'
    )
    Write-Step "GitHub Copilot (VS Code)"

    $wrote = $false
    foreach ($cfg in $candidates) {
        $dir = Split-Path $cfg
        if (-not (Test-Path -LiteralPath $dir)) {
            Write-Skip "VS Code edition not installed: $dir"
            continue
        }

        $json = if (Test-Path -LiteralPath $cfg) {
            try { Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json -Depth 32 } catch { [pscustomobject]@{} }
        } else { [pscustomobject]@{} }

        if (-not $json.PSObject.Properties['servers']) {
            $json | Add-Member -NotePropertyName 'servers' -NotePropertyValue ([pscustomobject]@{})
        }

        $entry = [pscustomobject]@{
            type    = 'stdio'
            command = $BridgePath
        }

        if ($json.servers.PSObject.Properties['acd-mcp']) {
            $json.servers.'acd-mcp' = $entry
        } else {
            $json.servers | Add-Member -NotePropertyName 'acd-mcp' -NotePropertyValue $entry
        }

        if ($PSCmdlet.ShouldProcess($cfg, "Write servers.acd-mcp entry")) {
            $json | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $cfg -Encoding UTF8
            Write-Ok "Registered with $(Split-Path -Leaf (Split-Path $dir))"
            $wrote = $true
        }
    }
    if (-not $wrote) {
        Write-Skip "No VS Code edition found — Copilot registration skipped"
    }
}

function Register-ClaudeDesktop {
    $cfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
    Write-Step "Claude Desktop ($cfg)"

    if (-not (Test-Path -LiteralPath (Split-Path $cfg))) {
        Write-Skip "Claude Desktop not installed — skipping"
        return
    }

    $json = if (Test-Path -LiteralPath $cfg) {
        try { Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json -Depth 32 } catch { [pscustomobject]@{} }
    } else { [pscustomobject]@{} }

    if (-not $json.PSObject.Properties['mcpServers']) {
        $json | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([pscustomobject]@{})
    }

    $entry = [pscustomobject]@{ command = $BridgePath }

    if ($json.mcpServers.PSObject.Properties['acd-mcp']) {
        $json.mcpServers.'acd-mcp' = $entry
    } else {
        $json.mcpServers | Add-Member -NotePropertyName 'acd-mcp' -NotePropertyValue $entry
    }

    if ($PSCmdlet.ShouldProcess($cfg, "Write mcpServers.acd-mcp entry")) {
        $json | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $cfg -Encoding UTF8
        Write-Ok "Registered with Claude Desktop"
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
    Write-Warn2 "Build it with: dotnet publish src\Acd.Mcp.Bridge -c Release -o '<path>\bin'"
    Write-Warn2 "MCP client registration will write paths that don't resolve until you do."
}

if (-not $SkipBundle) {
    Install-Bundle
}

# Expand 'auto' / 'none' into a concrete list
$effective = if ($Clients -contains 'none') {
    @()
} elseif ($Clients -contains 'auto') {
    $auto = @()
    if (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.codex')) { $auto += 'codex' }
    if ((Test-Path -LiteralPath (Join-Path $env:APPDATA 'Code\User')) -or
        (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Code - Insiders\User'))) { $auto += 'copilot' }
    if (Test-Path -LiteralPath (Join-Path $env:APPDATA 'Claude')) { $auto += 'claude-desktop' }
    if (-not $auto) {
        Write-Warn2 "No supported MCP clients detected. Pass -Clients explicitly to override."
    }
    $auto
} else {
    $Clients
}

foreach ($client in ($effective | Select-Object -Unique)) {
    switch ($client) {
        'codex'           { Register-Codex }
        'copilot'         { Register-Copilot }
        'claude-desktop'  { Register-ClaudeDesktop }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Launch AutoCAD 2025. The bundle autoloads."
Write-Host "  2. Type ACDMCP_START in AutoCAD to open the named pipe."
Write-Host "  3. (Optional) Type ACDMCP_PALETTE for the REPL/BATCH dockable palette."
Write-Host "  4. Restart your AI client(s) so they pick up the new MCP server."
Write-Host ""
