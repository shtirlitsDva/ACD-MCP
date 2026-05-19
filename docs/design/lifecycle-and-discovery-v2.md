<doc-overview>
Lifecycle + discovery contract for the bridge ↔ plugin link, post
fragility-fix v2 (2026-05-19). Supersedes the palette-coupling
decision in `script-editor-refactor-v1.md` and the discovery shape
described in early `architecture.md`.

The source review that triggered this rewrite lives at
`docs/review/fragility-review-2026-05-18.md`. The matching
implementation plan is `docs/review/fragility-implementation-plan.md`.

Read this first when touching:
- `src/Acd.Mcp.Bridge/AutoCadDiscovery.cs`
- `src/Acd.Mcp.Bridge/PipeProber.cs`
- `src/Acd.Mcp.Bridge/PipeClient.cs` / `AcadClient.cs`
- `src/Acd.Mcp/Ui/PaletteHost.cs`
- `src/Acd.Mcp/McpPlugin.cs` (Initialize / TryEnsureCore / ShowPalette)
</doc-overview>

<one-line-contract>
**Listener-up = fully functional.** The user never has to "open the
palette" or "run a command" for the agent's tools to work. The plugin
auto-starts on idle; agent calls auto-open the palette when they
need it.
</one-line-contract>

<bridge-side-discovery>
<process-resolution>
`AutoCadDiscovery.ResolveAsync(explicitPid)` follows this order:

1. **Explicit pid hint.** If `--pid <N>` was passed and PID `N`
   exists, probe `acd-mcp-N`:
   - listener up → return `ExplicitPidVerified` (happy path).
   - listener down → return `ExplicitPidPipeNotReady` (transient).
2. **Pinned PID dead.** Fall through to discovery rather than welding
   the bridge to a now-defunct process. This is the post-restart
   recovery path.
3. **Sole acad.exe.** Probe it:
   - listener up → `SoleAutoCadWithPlugin`.
   - listener down → `SoleAutoCadPipeNotReady` (transient).
4. **Multiple acad.exes.** Probe each in parallel:
   - exactly one listener → `DisambiguatedByPipe`.
   - zero listeners → `AmbiguousAutoCads` (hard error; needs --pid).
   - multiple listeners → `MultipleAutoCadPlugins` (hard error; needs --pid).
5. **No acad.exe.** `NoAutoCadFound` (hard error; start AutoCAD).

`PidResolution.IsTransient` is true for the two "pipe not ready"
reasons; the connect retry loop uses this to wait + retry.
</process-resolution>

<pipe-prober>
`PipeProber.IsListeningAsync(pid, timeout)` opens a
`NamedPipeClientStream` for `acd-mcp-{pid}`, awaits `ConnectAsync`.
Any connect = listening. Tight 150 ms timeout so probing 3-4
candidates in parallel stays well under one second.

Used at discovery time (disambiguation) AND inside the connect
retry loop, so one implementation backs both.
</pipe-prober>

<connect-retry>
`AcadClient.SendAsync` runs a retry loop driven by
`ConnectRetryPolicy.Default = (200, 800, 2000)` ms. Each attempt:

1. Re-resolve PID (so a listener coming up mid-retry is picked up).
2. If the resolution is transient (`SoleAutoCadPipeNotReady` etc.),
   delay this attempt's quantum and continue.
3. Otherwise connect at the attempt's deadline.
4. Send the request frame, read the response frame.

Retryable failures: `PipeNotListening`, `PipeBroken`.
**Non-retryable**: caller-supplied CT cancellation,
`AcadRpcException` (plugin replied with an error).
</connect-retry>
</bridge-side-discovery>

<plugin-side-lifecycle>
<auto-start>
`McpPlugin.Initialize` hooks `Application.Idle` to call `Start()`
on first idle. Reads `%LOCALAPPDATA%\Acd.Mcp\config.json` for an
`auto_start: false` opt-out. Was DEBUG-only before fragility-fix v2 —
promoted because the manual `ACDMCP_START` was a silent tax with
no benefit.
</auto-start>

<palette-host>
The `PaletteHost` (`src/Acd.Mcp/Ui/PaletteHost.cs`) is the single
binding point between the RPC handlers and the WPF palette:

- Constructed in `TryEnsureCore`, **not** in `ShowPalette`. So
  `_scriptRpc` and `_batchRpc` exist as soon as the pipe listens.
- `CurrentBatchUiState` returns null when the palette isn't open;
  handlers that need user-owned UI state return a structured
  `PALETTE_CLOSED` error rather than throwing a free-text exception.
- `EnsureVisible()` marshals to the main thread and shows the
  palette. Propose-script handlers call this so the user sees the
  staged proposal even if they never opened the palette themselves.
- `ACDMCP_PALETTE` is now exactly what its name implies — show the
  palette. Both the command and `EnsureVisible` arrive at
  `GetOrCreateOnMainThread` in one path.
</palette-host>

<rpc-dispatcher>
`McpPlugin.ExtraRpcMethodHandler` routes by prefix:

- `script.*` → `_scriptRpc` (wired in `TryEnsureCore`).
- `batch.*`  → `_batchRpc`  (wired in `TryEnsureCore`).
- `dto.*`    → `_dtoRpc`    (wired in `EnsureDtoGraph`).
- `acdmcp.*` → `_statusRpc` (wired in `TryEnsureCore`, also self-heals).

The handlers' null-checks are last-resort guards — if any fire, the
plugin is half-initialised and the error code (`PLUGIN_NOT_INITIALIZED`,
`DTO_NOT_READY`) tells the agent's skill to surface the failure
instead of retrying.
</rpc-dispatcher>
</plugin-side-lifecycle>

<error-codes>
The bridge tool wrappers swallow `AcadTransportException` and
`AcadRpcException` into `ok=false` result records carrying a stable
`error_code` string. The finite enum:

| `error_code`                | Source                       | Agent should              |
| --------------------------- | ---------------------------- | ------------------------- |
| `NO_AUTOCAD_FOUND`          | Bridge: discovery            | Ask user to start AutoCAD |
| `AMBIGUOUS_AUTOCADS`        | Bridge: discovery (no pipe)  | Ask user to run ACDMCP_START or pass --pid |
| `MULTIPLE_AUTOCAD_PLUGINS`  | Bridge: discovery (multiple) | Ask user to pass --pid    |
| `PINNED_PID_GONE`           | Bridge: --pid dead, no fallback | Ask user to restart AutoCAD |
| `PIPE_NOT_LISTENING`        | Bridge: retries exhausted    | Read `acd-mcp://status`, retry once |
| `PIPE_BROKEN`               | Bridge: mid-call I/O failure | Read `acd-mcp://status`, retry once |
| `PALETTE_CLOSED`            | Plugin: batch.runTest / batch.getSelection | Ask user to open palette and set selection |
| `PLUGIN_NOT_INITIALIZED`    | Plugin: half-load            | Surface to user — check AutoCAD log |
| `DTO_NOT_READY`             | Plugin: dto.* before init    | Wait for ACDMCP_START, retry once |
</error-codes>

<status-resource>
`acd-mcp://status` returns a JSON snapshot of every capability:

```json
{
  "version": "v27-codex-cwd-fix",
  "pid": 12345,
  "pipe": "acd-mcp-12345",
  "script_execute":      { "status": "ready",       "reason": null },
  "script_propose":      { "status": "ready",       "reason": null },
  "batch_propose":       { "status": "ready",       "reason": null },
  "batch_run_test":      { "status": "degraded",    "reason": "PALETTE_CLOSED" },
  "batch_get_selection": { "status": "degraded",    "reason": "PALETTE_CLOSED" },
  "dto":                 { "status": "ready",       "reason": null }
}
```

`status` is `ready` / `degraded` / `unavailable`; `reason` matches
the error-codes table above. The agent's skill reads this when a
tool returns a transport error to decide between retry, wait, and
escalation.
</status-resource>

<test-coverage>
- `tests/Acd.Mcp.Bridge.Tests/AutoCadDiscoveryTests.cs` —
  the five-way branch in `ResolveAsync` with a fake prober.
- `tests/Acd.Mcp.Bridge.Tests/PipeProberTests.cs` —
  live `NamedPipeServerStream` round-trip.
- `tests/Acd.Mcp.Tests/ToolNameRegressionTests.cs` —
  guards against retired tool names reappearing.
- Integration smoke: load plugin via DevReload, run
  `script.proposeScript` before opening the palette,
  assert it returns `ok=true` and the palette becomes visible.
</test-coverage>
