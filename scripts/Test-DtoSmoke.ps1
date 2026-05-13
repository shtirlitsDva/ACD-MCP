#Requires -Version 7.0
<#
.SYNOPSIS
  Smoke test that ACD-MCP's DTO registration produces N > 0 types on a
  real Civil 3D launch. Closes G5 from CRASH_TEST_V2_JOURNAL.md — a cheap
  log-tail check that would have caught both F7 originally and G2 today.

.DESCRIPTION
  The plugin logs `EnsureDtoGraph: Registered N DTO types.` once the DTO
  graph is built (after the AECC probe + provider composite). N == 0 means
  the registration mechanism is broken or the ALC split is wrong; N > 0
  on Civil 3D 2025 with the metric template is the healthy case (currently
  21 types).

  This script bootstraps the same recipe the agentic harness uses
  (docs/computer-use-from-claude-code.md <autonomous-bootstrap>):
   1. Flip DevReload's Acd.Mcp loadOnStartup to true (backup first).
   2. Launch Civil 3D 2025 with /Automation (hidden COM-server mode).
   3. Tail %LOCALAPPDATA%\Acd.Mcp\log.txt up to 180 s for both the
      Initialize: vNN line and the EnsureDtoGraph: Registered N line.
   4. Assert N > 0 (configurable via -MinDtoCount).
   5. Restore plugins.json. Kill ONLY the process this script launched.

  Hard failures (exit 1):
    - Civil 3D process exited before Initialize landed.
    - 180 s deadline elapsed without both markers.
    - N <= MinDtoCount.

.PARAMETER MinDtoCount
  Smallest acceptable count. Default 1 (catches the F7-style "zero DTOs"
  regression). Set higher (e.g. 21) to also assert against the current
  expected baseline.

.PARAMETER AcadExe
  Override the AutoCAD executable path. Default: AutoCAD 2025 Metric.

.PARAMETER TimeoutSeconds
  Bootstrap deadline. Default 180 s.

.EXAMPLE
  pwsh scripts/Test-DtoSmoke.ps1
  # Asserts N >= 1 within 180 s.

.EXAMPLE
  pwsh scripts/Test-DtoSmoke.ps1 -MinDtoCount 21
  # Asserts current baseline (21 DTOs for Civil 3D Metric).
#>
[CmdletBinding()]
param(
    [int]    $MinDtoCount = 1,
    [string] $AcadExe = 'C:\Program Files\Autodesk\AutoCAD 2025\acad.exe',
    [int]    $TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  + $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  ! $msg" -ForegroundColor Red }

# --- 0. preflight ---------------------------------------------------------

if (-not (Test-Path $AcadExe)) {
    Write-Fail "acad.exe not found at $AcadExe"
    exit 1
}

$pluginsPath = "$env:APPDATA\DevReload\plugins.json"
if (-not (Test-Path $pluginsPath)) {
    Write-Fail "DevReload plugins.json not found at $pluginsPath"
    exit 1
}

$logPath = "$env:LOCALAPPDATA\Acd.Mcp\log.txt"
$logSizeBefore = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }

# --- 1. flip loadOnStartup ------------------------------------------------

$backup = "$pluginsPath.dto-smoke-backup"
Copy-Item $pluginsPath $backup -Force
$json = Get-Content $pluginsPath -Raw | ConvertFrom-Json
$acd = $json.plugins | Where-Object { $_.name -eq 'Acd.Mcp' }
if (-not $acd) {
    Write-Fail "DevReload plugins.json has no Acd.Mcp entry."
    Copy-Item $backup $pluginsPath -Force
    Remove-Item $backup -Force
    exit 1
}
$origLoad = $acd.loadOnStartup
$acd.loadOnStartup = $true
$json | ConvertTo-Json -Depth 10 | Set-Content $pluginsPath -Encoding UTF8
Write-Step "Flipped Acd.Mcp.loadOnStartup: $origLoad -> True"

$acadProc = $null
$exitCode = 1

try {
    # --- 2. launch ----------------------------------------------------------

    $argList = @(
        '/ld', '"C:\Program Files\Autodesk\AutoCAD 2025\AecBase.dbx"',
        '/p',  '"<<C3D_Metric>>"',
        '/product', 'C3D',
        '/language', 'en-US',
        '/Automation'
    )
    Write-Step "Launching Civil 3D /Automation"
    $acadProc = Start-Process -FilePath $AcadExe -ArgumentList $argList -PassThru
    Write-Ok "PID $($acadProc.Id)"

    # --- 3. tail log --------------------------------------------------------

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $initSeen = $false
    $dtoCount = $null

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Get-Process -Id $acadProc.Id -ErrorAction SilentlyContinue) -eq $null) {
            Write-Fail "acad.exe exited before Initialize landed"
            break
        }
        if (Test-Path $logPath) {
            $tail = Get-Content $logPath -Tail 200 -ErrorAction SilentlyContinue
            if (-not $initSeen) {
                $m = $tail | Select-String -Pattern 'Initialize: v\d+' | Select-Object -Last 1
                if ($m) { $initSeen = $true; Write-Ok "Initialize line: $($m.Line.Trim())" }
            }
            if ($null -eq $dtoCount) {
                $m = $tail | Select-String -Pattern 'EnsureDtoGraph: Registered (\d+) DTO types' | Select-Object -Last 1
                if ($m) {
                    $dtoCount = [int]$m.Matches[0].Groups[1].Value
                    Write-Ok "DTO graph line: $($m.Line.Trim()) (N=$dtoCount)"
                }
            }
        }
        if ($initSeen -and $null -ne $dtoCount) { break }
    }

    # --- 4. assert ----------------------------------------------------------

    if (-not $initSeen) {
        Write-Fail "Timed out waiting for 'Initialize: v...' line in $logPath"
    } elseif ($null -eq $dtoCount) {
        Write-Fail "Timed out waiting for 'EnsureDtoGraph: Registered N DTO types' line"
    } elseif ($dtoCount -lt $MinDtoCount) {
        Write-Fail "DTO count $dtoCount < MinDtoCount $MinDtoCount"
    } else {
        Write-Ok "PASS: registered $dtoCount DTO types (>= $MinDtoCount)"
        $exitCode = 0
    }
}
finally {
    # --- 5. teardown -------------------------------------------------------

    if ($acadProc -and -not $acadProc.HasExited) {
        Write-Step "Killing PID $($acadProc.Id)"
        try { Stop-Process -Id $acadProc.Id -Force -ErrorAction Stop } catch { }
    }
    Copy-Item $backup $pluginsPath -Force
    Remove-Item $backup -Force
    Write-Step "Restored Acd.Mcp.loadOnStartup -> $origLoad"
}

exit $exitCode
