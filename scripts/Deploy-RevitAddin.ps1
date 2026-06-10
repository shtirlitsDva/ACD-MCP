# Deploy-RevitAddin.ps1 — build Rvt.Mcp and register it with Revit 2025.
#
# Writes the .addin manifest into %APPDATA%\Autodesk\Revit\Addins\2025
# pointing at the repo build output (dev-loop style: rebuild + restart Revit
# picks up the new DLL; no file copying).
#
# Usage: pwsh scripts\Deploy-RevitAddin.ps1 [-RevitYear 2025] [-Configuration Debug] [-Remove]

param(
    [int]$RevitYear = 2025,
    [string]$Configuration = 'Debug',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
$manifestPath = Join-Path $manifestDir 'Rvt.Mcp.addin'

if ($Remove) {
    if (Test-Path $manifestPath) { Remove-Item $manifestPath; Write-Host "deleted $manifestPath" }
    else { Write-Host 'nothing to remove' }
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

$dll = Join-Path $repoRoot "src\Revit\Rvt.Mcp.Loader\bin\$Configuration\Rvt.Mcp.Loader.dll"
if (-not (Test-Path $dll)) { throw "build output not found: $dll" }

New-Item -ItemType Directory -Force $manifestDir | Out-Null
@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Rvt.Mcp</Name>
    <Assembly>$dll</Assembly>
    <AddInId>f4ac6a14-27e4-442f-a254-300c83e2b55a</AddInId>
    <FullClassName>Rvt.Mcp.Loader.LoaderApp</FullClassName>
    <VendorId>DVRL</VendorId>
    <VendorDescription>Rvt.Mcp — C# script REPL for agents</VendorDescription>
  </AddIn>
</RevitAddIns>
"@ | Set-Content $manifestPath -Encoding utf8

Write-Host "manifest written: $manifestPath"
Write-Host "assembly: $dll"
