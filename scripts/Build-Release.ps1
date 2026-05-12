#Requires -Version 7.0
<#
.SYNOPSIS
  Build, assemble, and (optionally) publish an ACD-MCP plugin release.

.DESCRIPTION
  This script is the release pipeline. GitHub Actions can't build
  Acd.Mcp.dll (it references AutoCAD 2025 managed APIs that don't exist
  on a stock CI runner), so the release is built on a maintainer machine
  with AutoCAD 2025 installed.

  What it does:
    1. dotnet publish Acd.Mcp.Bridge → bin/Acd.Mcp.Bridge.exe
    2. dotnet build  Acd.Mcp.sln    → Acd.Mcp.dll + transitive deps
    3. Assemble Deploy/acd-mcp-plugin/ with the full plugin layout:
         .claude-plugin/, skills/, .mcp.json, install-hooks/,
         bin/ (Bridge.exe), autocad-bundle/ACD-MCP.bundle/Contents/
         (Acd.Mcp.dll + deps)
    4. Zip it to Deploy/acd-mcp-plugin-v<X.Y.Z>.zip
    5. (Optional) Create a GH Release and upload the zip with gh CLI.

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
    $manifest = Get-Content '.claude-plugin/plugin.json' -Raw | ConvertFrom-Json
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

if (-not $SkipBuild) {
    Write-Step "dotnet publish Acd.Mcp.Bridge"
    dotnet publish 'src/Acd.Mcp.Bridge/Acd.Mcp.Bridge.csproj' `
        -c $Configuration `
        -o (Join-Path $deployRoot 'bridge-publish') `
        --self-contained false `
        -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { Fail "Bridge publish failed" }
    Write-Ok "Bridge published"

    Write-Step "dotnet build Acd.Mcp.sln"
    dotnet build 'Acd.Mcp.sln' -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { Fail "Plugin build failed" }
    Write-Ok "Plugin built"
}

# ─── stage plugin layout ────────────────────────────────────────────────────

Write-Step "Assembling plugin layout at $pluginStage"

# 1. Manifest + plugin-level configs (everything Claude Code reads directly).
Copy-Item '.claude-plugin'  (Join-Path $pluginStage '.claude-plugin')  -Recurse
Copy-Item '.mcp.json'       $pluginStage
Copy-Item 'skills'          (Join-Path $pluginStage 'skills')          -Recurse
Copy-Item 'install-hooks'   (Join-Path $pluginStage 'install-hooks')   -Recurse
Copy-Item 'README.md'       $pluginStage
Write-Ok "Plugin metadata copied"

# 2. Bridge.exe + its publish deps → bin/
$pluginBin = Join-Path $pluginStage 'bin'
New-Item -ItemType Directory -Path $pluginBin -Force | Out-Null
Copy-Item (Join-Path $deployRoot 'bridge-publish\*') $pluginBin -Recurse
Write-Ok "Bridge.exe staged"

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
Write-Host "Users install with one of:" -ForegroundColor White
Write-Host "  Claude Code: /plugin install <repo>@<marketplace>     # via marketplace"
Write-Host "               claude --plugin-url <release-zip-url>     # one-off"
Write-Host "  Others:      Download zip, extract, run install-hooks\Install-AcdMcp.ps1"
Write-Host ""
