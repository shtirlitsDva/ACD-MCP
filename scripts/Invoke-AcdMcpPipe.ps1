#Requires -Version 7.0
<#
.SYNOPSIS
  Direct JSON-RPC client for the ACD-MCP plugin's named pipe.
  Bypasses the MCP bridge entirely. Closes V3-H1 in
  CRASH_TEST_V3_JOURNAL.md as a documented workaround.

.DESCRIPTION
  The plugin opens a named pipe at \\.\pipe\acd-mcp-<PID> once it
  initializes (DEBUG builds auto-open via the Application.Idle hook
  added in v20). The bridge process talks to that pipe; this script
  talks to the SAME pipe directly, sidestepping the MCP bridge.

  Use this when:

  * You're iterating on bridge-side code (Acd.Mcp.Bridge.dll changes)
    and want to verify the plugin RPC layer's behaviour without
    relying on Claude Code's MCP transport. Killing the bridge to
    swap a fresh dll permanently disconnects the in-session MCP
    server (V3-H1) — recovery requires the user to /reload-plugins,
    which an agentic loop cannot self-trigger.

  * You're driving an agentic test loop against the plugin from
    PowerShell/CI and don't want to depend on Claude Code at all.

  * You need to call a plugin RPC method that isn't exposed as an
    MCP tool (e.g. `batch.getSelection` while the palette is closed,
    to observe the raw error envelope — V3-G4 diagnose phase used
    exactly this).

  Wire format (per src/Acd.Mcp/Pipe/Protocol.cs):
    [4-byte big-endian length][UTF-8 JSON payload]
  JSON-RPC 2.0 frames. JsonOptions: camelCase on PascalCase fields,
  WhenWritingNull ignore, case-insensitive read.

.PARAMETER AcadPid
  PID of the AutoCAD/Civil 3D process whose plugin to talk to. Find
  it with `Get-Process acad | Select-Object Id` or by reading
  $env:TEMP\acdmcp-agent-acad.pid (set by the agentic bootstrap).

.PARAMETER Method
  JSON-RPC method name. Common values:
    execute              — run a C# REPL snippet (params: code, timeout_ms)
    batch.proposeScript  — params: name, script_body, input_summary?
    batch.runTest        — params: name?
    batch.getSelection   — no params
    repl.proposeScript   — params: name, script_body, input_summary?
    repl.getEditor       — no params
    dto.list             — list registered DTO types

.PARAMETER Params
  Hashtable of method parameters. Omit / pass $null for parameterless
  methods.

.PARAMETER TimeoutSeconds
  Soft client-side timeout. The pipe-level connect timeout is hard-
  coded at 5 s; once connected, the response read uses this for the
  overall budget.

.OUTPUTS
  PSCustomObject with `jsonrpc`, `id`, and either `result` or `error`.

.EXAMPLE
  pwsh scripts/Invoke-AcdMcpPipe.ps1 -AcadPid 4732 -Method 'execute' `
       -Params @{ code = '1+1'; timeout_ms = 5000 }

.EXAMPLE
  $rsp = pwsh scripts/Invoke-AcdMcpPipe.ps1 -AcadPid 4732 `
              -Method 'batch.proposeScript' `
              -Params @{ name='x'; script_body='// noop' }
  $rsp.result.replaced_dirty
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]      $AcadPid,
    [Parameter(Mandatory)][string]   $Method,
    [hashtable]                      $Params,
    [int]                            $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pipeName = "acd-mcp-$AcadPid"
$client = $null
try {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', $pipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)
    $client.Connect(5000)

    # --- request frame ---------------------------------------------------
    $req = [ordered]@{
        jsonrpc = '2.0'
        id      = 1
        method  = $Method
    }
    if ($Params) { $req.params = $Params }
    $json = $req | ConvertTo-Json -Depth 16 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $len = $bytes.Length
    $lenBuf = [byte[]]@(
        ($len -shr 24) -band 0xFF,
        ($len -shr 16) -band 0xFF,
        ($len -shr  8) -band 0xFF,
        ($len       ) -band 0xFF
    )
    $client.Write($lenBuf, 0, 4)
    $client.Write($bytes, 0, $bytes.Length)
    $client.Flush()

    # --- response frame --------------------------------------------------
    $hdr = [byte[]]::new(4)
    $read = 0
    while ($read -lt 4) {
        $n = $client.Read($hdr, $read, 4 - $read)
        if ($n -eq 0) { throw "Pipe closed before response header read." }
        $read += $n
    }
    $rlen = ([int]$hdr[0] -shl 24) -bor
            ([int]$hdr[1] -shl 16) -bor
            ([int]$hdr[2] -shl  8) -bor
            [int]$hdr[3]
    if ($rlen -le 0 -or $rlen -gt (16 * 1024 * 1024)) {
        throw "Bad response length: $rlen"
    }
    $body = [byte[]]::new($rlen)
    $read = 0
    while ($read -lt $rlen) {
        $n = $client.Read($body, $read, $rlen - $read)
        if ($n -eq 0) { throw "Pipe closed mid-frame." }
        $read += $n
    }
    $text = [System.Text.Encoding]::UTF8.GetString($body)
    return $text | ConvertFrom-Json -Depth 32
}
finally {
    if ($client) {
        try { $client.Dispose() } catch { }
    }
}
