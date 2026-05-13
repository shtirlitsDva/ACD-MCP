#Requires -Version 7.0
<#
.SYNOPSIS
  End-to-end verification that the bridge's discriminated success-shape
  for batch tool errors (V2-G4) reaches the wire correctly. Closes V3-G4
  in CRASH_TEST_V3_JOURNAL.md.

.DESCRIPTION
  Spawns Acd.Mcp.Bridge.exe directly with stdio redirection, performs
  the MCP initialize handshake, sends a tools/call for
  `autocad_batch_get_selection` against an AutoCAD process whose plugin
  is loaded but whose BATCH palette is intentionally NOT open. The
  plugin returns an InvalidOperationException; the bridge catches the
  resulting AcadRpcException and converts to the discriminated shape
  on the success path.

  Pre-fix observable: the wire response would have been an MCP tool
  error envelope and the agent would see the generic "An error occurred
  invoking ..." string. Post-fix: the wire response is a normal tool
  result (IsError=False) carrying JSON `{ ok: false, error_code,
  error_message }`.

  This verification does NOT require Claude Code to be running — the
  bridge process is spawned directly, so the workflow is reproducible
  in any agentic/CI loop. Closes V3-H1 for this specific verification.

.PARAMETER AcadPid
  PID of the AutoCAD process to drive. If omitted, reads
  $env:TEMP\acdmcp-agent-acad.pid (set by the agentic bootstrap), then
  falls back to the first Get-Process acad result.

.PARAMETER BridgeExe
  Override the bridge executable path. Default: repo's bin/.

.PARAMETER TimeoutSeconds
  Per-frame read timeout. Default 15 s.

.OUTPUTS
  Exit code 0 on PASS, 1 on FAIL. Prints the wire response + per-
  assertion result lines for debugging.

.EXAMPLE
  # After the agentic bootstrap (which sets $env:TEMP\acdmcp-agent-acad.pid):
  pwsh scripts/Verify-BridgeG4.ps1
#>
param(
    [int]    $AcadPid,
    [string] $BridgeExe = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'bin/Acd.Mcp.Bridge.exe'),
    [int]    $TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'

# Resolve AcadPid.
if (-not $AcadPid) {
    $pidFile = Join-Path $env:TEMP 'acdmcp-agent-acad.pid'
    if (Test-Path $pidFile) {
        $AcadPid = [int](Get-Content $pidFile)
    } else {
        $acad = Get-Process acad -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $acad) { Write-Host "No AutoCAD process found." -ForegroundColor Red; exit 1 }
        $AcadPid = $acad.Id
    }
}
Write-Host "AcadPid = $AcadPid; BridgeExe = $BridgeExe" -ForegroundColor Cyan

if (-not (Test-Path $BridgeExe)) {
    Write-Host "Bridge exe not found at $BridgeExe. Run scripts/Refresh-PluginCache.ps1 -Publish first." -ForegroundColor Red
    exit 1
}
if (-not (Get-Process -Id $AcadPid -ErrorAction SilentlyContinue)) {
    Write-Host "AutoCAD PID $AcadPid is not running." -ForegroundColor Red
    exit 1
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $BridgeExe
$psi.Arguments = "--pid $AcadPid"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$bridge = [System.Diagnostics.Process]::Start($psi)
Write-Host "Bridge spawned, PID = $($bridge.Id)" -ForegroundColor Cyan

$stdinUtf8 = New-Object System.IO.StreamWriter(
    $bridge.StandardInput.BaseStream,
    [System.Text.UTF8Encoding]::new($false))
$stdinUtf8.AutoFlush = $true

function Send-Frame($obj) {
    $json = $obj | ConvertTo-Json -Depth 16 -Compress
    $stdinUtf8.WriteLine($json)
}
function Read-Frame($timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ($bridge.HasExited) { throw "Bridge exited unexpectedly (code $($bridge.ExitCode))." }
        $line = $bridge.StandardOutput.ReadLine()
        if ($null -eq $line) { Start-Sleep -Milliseconds 50; continue }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        return $line | ConvertFrom-Json -Depth 32
    }
    throw "Timeout reading frame."
}

$exitCode = 1
try {
    # MCP handshake
    Send-Frame @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = @{ name = 'verify-bridge-g4'; version = '1.0' }
        }
    }
    Read-Frame ($TimeoutSeconds * 1000) | Out-Null
    Send-Frame @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    Start-Sleep -Milliseconds 200

    # The verification call.
    Send-Frame @{
        jsonrpc = '2.0'; id = 2; method = 'tools/call'
        params = @{ name = 'autocad_batch_get_selection'; arguments = @{} }
    }
    $rsp = Read-Frame ($TimeoutSeconds * 1000)

    Write-Host "----- raw response -----" -ForegroundColor Yellow
    $rsp | ConvertTo-Json -Depth 16 | Out-Host
    Write-Host "------------------------" -ForegroundColor Yellow

    # Extract payload — newer SDK uses structuredContent; older sends
    # JSON in content[0].text. We accept either.
    $payload = $null
    if ($rsp.result.structuredContent) {
        $payload = $rsp.result.structuredContent
    } elseif ($rsp.result.content) {
        try { $payload = $rsp.result.content[0].text | ConvertFrom-Json -Depth 32 } catch { }
    }
    if (-not $payload) {
        Write-Host "FAIL: cannot extract structured payload from result." -ForegroundColor Red
        return
    }

    $passed = $true
    if ($payload.PSObject.Properties.Name -notcontains 'ok') {
        Write-Host "FAIL: payload missing 'ok' field (pre-fix shape?)." -ForegroundColor Red
        $passed = $false
    } elseif ($payload.ok) {
        Write-Host "FAIL: ok=true but palette was supposed to be closed." -ForegroundColor Red
        $passed = $false
    } else {
        Write-Host "  + ok=false (discriminator)" -ForegroundColor Green
    }
    if ($payload.error_message -match 'BATCH palette is not open') {
        Write-Host "  + error_message carries the plugin's verbatim text" -ForegroundColor Green
    } else {
        Write-Host "FAIL: error_message wrong or missing: $($payload.error_message)" -ForegroundColor Red
        $passed = $false
    }
    if ($payload.error_code) {
        Write-Host "  + error_code = $($payload.error_code)" -ForegroundColor Green
    } else {
        Write-Host "  ! error_code missing (non-fatal but unexpected)" -ForegroundColor Yellow
    }

    if ($passed) {
        Write-Host "G4 END-TO-END VERIFIED: discriminated shape on the wire." -ForegroundColor Green
        $exitCode = 0
    } else {
        Write-Host "G4 VERIFICATION FAILED." -ForegroundColor Red
    }
}
finally {
    try { $stdinUtf8.Close() } catch { }
    if (-not $bridge.HasExited) {
        try { $bridge.WaitForExit(2000) | Out-Null } catch { }
        if (-not $bridge.HasExited) { try { $bridge.Kill() } catch { } }
    }
}
exit $exitCode
