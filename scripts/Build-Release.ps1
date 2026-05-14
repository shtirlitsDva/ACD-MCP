#Requires -Version 7.0
<#
.SYNOPSIS
  Build, assemble, and (optionally) publish an ACD-MCP plugin release.

.DESCRIPTION
  This script is the release pipeline. Runs locally or in CI — AutoCAD 2025
  and Civil 3D 2025 reference assemblies come from NuGet (AutoCAD.NET 25.0.1
  + Speckle.Civil3D.API 2025.0.0, ExcludeAssets=runtime), so no AutoCAD
  install is required on the build machine. The .github/workflows/ci.yml
  "release" job invokes this script on tag push.

  What it does:
    1. dotnet publish Acd.Mcp.Bridge → plugins/acd-mcp/bin/ (THE committed
       binaries — Claude Code's /plugin install launches Bridge from here
       via .mcp.json's ${CLAUDE_PLUGIN_ROOT}/bin/Acd.Mcp.Bridge.exe; Codex's
       /plugin install uses codex.mcp.json's ./bin/Acd.Mcp.Bridge.exe).
    2. dotnet build  Acd.Mcp.sln    → Acd.Mcp.dll + transitive deps
    3. Assemble Deploy/acd-mcp-plugin/ from the plugins/acd-mcp/ folder
       + autocad-bundle/ACD-MCP.bundle/Contents/ (Acd.Mcp.dll + deps).
    4. Zip it to Deploy/acd-mcp-plugin-v<X.Y.Z>.zip
    5. (Optional) Create a GH Release and upload the zip with gh CLI.

  After running locally, REMEMBER TO:
    git add plugins/acd-mcp/bin/
    git commit -m "Refresh Bridge binary for v<X.Y.Z>"
    git tag v<X.Y.Z>
    git push --tags
  CI then auto-uploads the zip to the GitHub Release (.github/workflows/ci.yml).

.PARAMETER Configuration
  dotnet build configuration. Default: Release.

.PARAMETER Version
  Override the version. Defaults to the "version" field in plugin.json.

.PARAMETER Publish
  Also create a GitHub Release tag "v<Version>" and upload the zip.
  Requires the `gh` CLI to be authenticated.

.PARAMETER SkipBuild
  Skip dotnet build/publish — only re-assemble + zip from existing bin/.

.EXAMPLE
  pwsh ./scripts/Build-Release.ps1
  # Build + assemble + zip. Result in Deploy/.

.EXAMPLE
  pwsh ./scripts/Build-Release.ps1 -Publish
  # Same, then `gh release create vX.Y.Z` and upload the zip.
#>
[CmdletBinding()]
param(
    [string]   $Configuration = 'Release',
    [string]   $Version,
    [switch]   $Publish,
    [switch]   $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Fail($msg)       { Write-Host "  ✗ $msg" -ForegroundColor Red; throw $msg }

# ─── version ────────────────────────────────────────────────────────────────

if (-not $Version) {
    $manifest = Get-Content 'plugins/acd-mcp/.claude-plugin/plugin.json' -Raw | ConvertFrom-Json
    $Version  = $manifest.version
}
if (-not $Version) { Fail "Could not determine version (no -Version, no plugin.json 'version' field)." }
Write-Step "Building release v$Version ($Configuration)"

# ─── deploy paths ───────────────────────────────────────────────────────────

$deployRoot   = Join-Path $repoRoot 'Deploy'
$pluginStage  = Join-Path $deployRoot 'acd-mcp-plugin'
$zipPath      = Join-Path $deployRoot "acd-mcp-plugin-v$Version.zip"

if (Test-Path $pluginStage) { Remove-Item $pluginStage -Recurse -Force }
New-Item -ItemType Directory -Path $pluginStage -Force | Out-Null

# ─── build ──────────────────────────────────────────────────────────────────

$pluginRoot = Join-Path $repoRoot 'plugins/acd-mcp'
$repoBinDir = Join-Path $pluginRoot 'bin'

if (-not $SkipBuild) {
    Write-Step "dotnet publish Acd.Mcp.Bridge → plugins/acd-mcp/bin/"
    # Publish directly into the committed plugins/acd-mcp/bin/. This is the
    # SAME bin/ that BOTH Claude Code and Codex /plugin install read (per
    # .mcp.json's ${CLAUDE_PLUGIN_ROOT}/bin/Acd.Mcp.Bridge.exe and
    # codex.mcp.json's ./bin/Acd.Mcp.Bridge.exe), so refreshing it here
    # keeps marketplace installs and the GitHub Release zip in lockstep.
    # Wipe-then-publish so removed transitive deps from a prior build don't
    # linger.
    if (Test-Path $repoBinDir) {
        Get-ChildItem $repoBinDir -File | Remove-Item -Force
    }
    dotnet publish 'src/Acd.Mcp.Bridge/Acd.Mcp.Bridge.csproj' `
        -c $Configuration `
        -o $repoBinDir `
        --self-contained false `
        -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { Fail "Bridge publish failed" }
    # Drop debug symbols from the committed bin/ — they bloat the repo
    # without helping users. dotnet doesn't have a publish flag to skip
    # the pdb cleanly, so prune after the fact.
    Get-ChildItem $repoBinDir -File -Filter '*.pdb' | Remove-Item -Force
    Write-Ok "Bridge published to $repoBinDir"

    Write-Step "dotnet build Acd.Mcp.sln"
    dotnet build 'Acd.Mcp.sln' -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { Fail "Plugin build failed" }
    Write-Ok "Plugin built"
}

# ─── stage plugin layout ────────────────────────────────────────────────────

Write-Step "Assembling plugin layout at $pluginStage"

# 1. Everything inside plugins/acd-mcp/ IS the plugin. Mirror it into the
#    zip stage root. That covers .claude-plugin/, .codex-plugin/, .mcp.json,
#    codex.mcp.json, skills/, install-hooks/, and bin/.
Copy-Item (Join-Path $pluginRoot '*') $pluginStage -Recurse
Copy-Item 'README.md' $pluginStage
Write-Ok "Plugin metadata + Bridge.exe staged"

# 3. AutoCAD bundle: copy structure + populate Contents/ from plugin bin.
$pluginBundleSrc = 'autocad-bundle/ACD-MCP.bundle'
$pluginBundleDst = Join-Path $pluginStage 'autocad-bundle\ACD-MCP.bundle'
New-Item -ItemType Directory -Path $pluginBundleDst -Force | Out-Null
Copy-Item (Join-Path $pluginBundleSrc 'PackageContents.xml') $pluginBundleDst

$contentsDst = Join-Path $pluginBundleDst 'Contents'
New-Item -ItemType Directory -Path $contentsDst -Force | Out-Null

$pluginBuildOut = "src/Acd.Mcp/bin/$Configuration"
if (-not (Test-Path $pluginBuildOut)) {
    Fail "Plugin build output not found at $pluginBuildOut. Did dotnet build succeed?"
}
# Copy every file in the plugin's bin output (Acd.Mcp.dll + its transitive
# deps that MSBuild already arranged for us). Skip pdb/xml docs to keep the
# bundle small; uncomment if you want symbols.
Get-ChildItem $pluginBuildOut -File |
    Where-Object { $_.Extension -in '.dll', '.pdb' } |
    Copy-Item -Destination $contentsDst -Force
Write-Ok "AutoCAD bundle contents staged ($((Get-ChildItem $contentsDst).Count) files)"

# ─── zip ────────────────────────────────────────────────────────────────────

Write-Step "Zipping to $zipPath"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $pluginStage '*') -DestinationPath $zipPath -Force
Write-Ok "Wrote $zipPath ($([math]::Round(((Get-Item $zipPath).Length / 1MB), 2)) MB)"

# ─── publish ────────────────────────────────────────────────────────────────

if ($Publish) {
    Write-Step "gh release create v$Version"
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "gh CLI not found. Install from https://cli.github.com or skip -Publish."
    }

    $tag = "v$Version"
    $existing = gh release view $tag 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Release $tag already exists — uploading asset only" -ForegroundColor Yellow
        gh release upload $tag $zipPath --clobber
    } else {
        gh release create $tag $zipPath `
            --title "ACD-MCP $tag" `
            --generate-notes
    }
    Write-Ok "Published $tag"
}

Write-Host ""
Write-Host "Release artifact: $zipPath" -ForegroundColor Green
Write-Host ""

# Nudge the maintainer if bin/ drifted from git. Without a refresh-and-commit,
# Claude Code's /plugin install would pull stale Bridge binaries from master.
if (Get-Command git -ErrorAction SilentlyContinue) {
    $dirtyBin = git status --porcelain -- plugins/acd-mcp/bin 2>$null
    if ($dirtyBin) {
        Write-Host "  ! plugins/acd-mcp/bin/ has uncommitted changes — commit them so /plugin install picks up the refresh:" -ForegroundColor Yellow
        Write-Host "    git add plugins/acd-mcp/bin/"
        Write-Host "    git commit -m `"Refresh Bridge binary for v$Version`""
        Write-Host "    git tag v$Version && git push --tags"
        Write-Host ""
    }
}

Write-Host "Users install with one of:" -ForegroundColor White
Write-Host "  Claude Code: /plugin marketplace add https://github.com/shtirlitsDva/ACD-MCP"
Write-Host "               /plugin install acd-mcp@acd-mcp                 # uses committed bin/"
Write-Host "               claude --plugin-url <release-zip-url>           # one-off, from release zip"
Write-Host "  Others:      Download zip, extract, then:"
Write-Host "                 pwsh install-hooks\Install-Bundle.ps1   # AutoCAD bundle"
Write-Host "                 pwsh install-hooks\Install-Mcp.ps1      # Codex/Copilot/Claude Desktop"
Write-Host ""
