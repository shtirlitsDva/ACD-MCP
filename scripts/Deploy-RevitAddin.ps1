# Deploy-RevitAddin.ps1 — build Rvt.Mcp (Release) and install it the
# standard Revit way: binaries copied to
#   %APPDATA%\Autodesk\Revit\Addins\<year>\Rvt.Mcp\
# plus a Rvt.Mcp.addin manifest with a RELATIVE assembly path.
#
# Only the Revit-process side lives here. The LLM side (MCP bridge +
# revit_script_execute tool) installs through the Claude/Codex plugin
# system (plugins/rvt-mcp); the two halves meet on the rvt-mcp-{pid} pipe.
#
# Usage: pwsh scripts\Deploy-RevitAddin.ps1 [-RevitYear 2025] [-Configuration Release] [-Remove]

param(
    [int]$RevitYear = 2025,
    [string]$Configuration = 'Release',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
$targetDir = Join-Path $addinsDir 'Rvt.Mcp'
$manifest  = Join-Path $addinsDir 'Rvt.Mcp.addin'

if ($Remove) {
    if (Test-Path $manifest)  { Remove-Item $manifest;                  Write-Host "removed $manifest" }
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force; Write-Host "removed $targetDir" }
    exit 0
}

if ($RevitYear -lt 2025) {
    throw "Rvt.Mcp targets Revit 2025+ (net8). Revit $RevitYear is .NET Framework — not supported yet."
}

# The loader project pulls in the engine via ProjectReference, so one build
# produces the complete folder (loader + engine + Roslyn).
$csproj = Join-Path $repoRoot 'src\Revit\Rvt.Mcp.Loader\Rvt.Mcp.Loader.csproj'
dotnet build $csproj -c $Configuration -p:Platform=x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Rvt.Mcp.Loader build failed' }

$buildOut = Join-Path $repoRoot "src\Revit\Rvt.Mcp.Loader\bin\$Configuration"
if (-not (Test-Path "$buildOut\Rvt.Mcp.Loader.dll")) { throw "build output not found: $buildOut" }

if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
New-Item -ItemType Directory -Force $targetDir | Out-Null
Copy-Item "$buildOut\*" $targetDir -Recurse

@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
    <AddIn Type="Application">
        <Name>Rvt.Mcp</Name>
        <Assembly>Rvt.Mcp/Rvt.Mcp.Loader.dll</Assembly>
        <FullClassName>Rvt.Mcp.Loader.LoaderApp</FullClassName>
        <AddInId>f4ac6a14-27e4-442f-a254-300c83e2b55a</AddInId>
        <VendorId>DVRL</VendorId>
        <VendorDescription>Norsyn, https://github.com/shtirlitsDva/ACD-MCP</VendorDescription>
    </AddIn>
</RevitAddIns>
"@ | Set-Content $manifest -Encoding utf8

Write-Host "installed -> $targetDir"
Write-Host "manifest  -> $manifest"
