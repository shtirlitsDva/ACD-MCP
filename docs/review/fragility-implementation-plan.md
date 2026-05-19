<doc-overview>
Implementation plan addressing the seven findings in
`docs/review/fragility-review-2026-05-18.md`.

Goal: stable, bug-free bridge + plugin lifecycle. Code and architecture
quality are the primary factor; work effort is not. Working branch is
already `claude/autocad-pipeline-stability-q7go3`.

Scope matches the source review — bridge + plugin lifecycle only; Batch/
Script execution engines are out of scope.
</doc-overview>

<guiding-principles>
- **Decouple lifetime from UI.** Pipe-level capability must be a function
  of plugin-load + listener-up, not "did the user click the palette icon."
- **Push tolerance to the transport.** Restart windows, lazy AutoCAD
  startup, and zombie processes are normal facts of life — the layer
  above the pipe should not see them as errors.
- **Single source of truth for state.** `ACDMCP_STATUS` becomes the
  authoritative description of "what works right now," and dispatcher
  errors share that vocabulary as structured codes.
- **Release hygiene.** Source, deployed plugin, design doc, and worktree
  converge before any further behavior change ships.
</guiding-principles>

<phase-0-release-hygiene>
Addresses finding-5.

Goal: eliminate the half-renamed state so the rest of the work happens
against one consistent vocabulary.

1. Decide and commit to one tool-name set. **Recommendation: keep the
   *new* in-source names** (`autocad_script_execute`,
   `autocad_script_propose`) and retire the deployed
   `autocad_execute_csharp` / `autocad_repl_propose_script`. Reasons:
   - The new names mirror the RPC method namespaces (`script.*`, `batch.*`).
   - The design doc `docs/design/script-editor-refactor-v1.md` is already
     written against them.
2. Single rename pass across:
   - `src/Acd.Mcp.Bridge/Tools/*.cs` — already done.
   - Plugin-deployed bin (`plugins/acd-mcp/.../bin`, `autocad-bundle/` if
     it has drift) — rebuild and re-deploy.
   - `README.md`, `docs/design/*`, `docs/handoff-*` — grep and update.
   - Skills the agent installs (`acd-mcp:script`, `acd-mcp:batch`) —
     they reference tool names literally, so the agent's behaviour
     depends on them being current.
3. Add a CI guard: a small repo-level test (under `tests/`) that greps
   the deployed plugin's bridge DLL/PE for the expected tool names and
   fails the build if any retired name reappears. Cheap belt-and-
   suspenders so a future drift cannot reach release.

Sequencing rationale: doing this first means every subsequent error
message, log line, and design doc references one set of names — no
migration debt is created by the later phases.
</phase-0-release-hygiene>

<phase-1-decouple-rpc-from-palette>
Addresses finding-1 (the core architectural fix).

**Architectural shape**

Current coupling: `_scriptRpc` and `_batchRpc` are constructed inside
`ShowPalette()` (`src/Acd.Mcp/McpPlugin.cs:309-316`). The dispatcher
(`McpPlugin.cs:328-340`) throws `InvalidOperationException` if they're
null. So the agent sees a hard failure whenever the listener is up but
the user hasn't opened the palette yet (the post-restart steady state).

The fix has two halves:

1. **Always-on RPC handlers.** Move construction of `_scriptRpc` and
   `_batchRpc` out of `ShowPalette()` and into `TryEnsureCore()` so they
   exist as soon as the pipe listens.
2. **Lazy UI binding.** The handlers must not require a live palette
   view-model to function. They take an `IPaletteHost` (new interface)
   that gives them, on demand:
   - access to the current `BatchViewModel` (or null if palette closed),
   - a hook to subscribe the palette to `ScriptProposed` events whenever
     it does open,
   - a way to request the palette open itself (marshalled to main thread).

**Detailed design**

- New interface `IPaletteHost` in `src/Acd.Mcp/Ui/`:
  ```csharp
  interface IPaletteHost {
      BatchViewModel? CurrentBatchViewModel { get; }
      bool IsOpen { get; }
      void EnsureVisible();        // creates + shows palette on main thread
  }
  ```
- A concrete singleton `PaletteHost` lives at the plugin level alongside
  `_palette`. `ShowPalette()` becomes the *one* place that constructs
  the palette; everywhere else asks the host. The host is created in
  `TryEnsureCore()`.
- `BatchRpcHandler` constructor takes `IPaletteHost` instead of
  `IBatchUiState`. Method handlers resolve the VM at dispatch time via
  `host.CurrentBatchViewModel`; for read-only methods (`batch.getSelection`),
  if the VM is absent, return a well-defined "palette not visible"
  structured result (see phase-6) rather than throwing.
- `script.proposeScript` and `batch.proposeScript`:
  - Buffer the proposed script even if the palette is closed. The
    `ScriptProposed` event keeps firing on the editor; the palette VM,
    when it later subscribes (on first open), reads the editor's current
    buffer and shows it. This is the "stage now, surface when UI opens"
    pattern.
  - Additionally, the propose handler calls `host.EnsureVisible()` so
    the user sees the proposal immediately on agent action. This kills
    the "silent staging into the void" concern from the original gating
    rationale without making it a precondition.
- All cross-thread UI work (`EnsureVisible`, VM property reads) is
  marshalled via `_mainSync.Post` / `Send` — the host is the central
  place this happens, so RPC handlers don't have main-thread knowledge.

**Backwards-incompatible side effects**

None for end users. Today an agent call before `ACDMCP_PALETTE` fails;
after the change it succeeds and opens the palette as a side effect of
the propose call.

**Tests**

- New unit fixture under `tests/Acd.Mcp.Tests/Ui` with a
  `FakePaletteHost` that toggles `IsOpen` mid-test, verifying:
  - `script.proposeScript` succeeds when `IsOpen == false`.
  - The next `IsOpen → true` transition surfaces the staged proposal
    (mock palette receives `ScriptProposed`).
  - `batch.getSelection` returns the "palette closed" structured
    payload when `IsOpen == false`.
- Integration smoke: load plugin, run `ACDMCP_START` only (no palette),
  invoke `script.proposeScript` over pipe, assert it returns `ok=true`
  and the palette becomes visible.
</phase-1-decouple-rpc-from-palette>

<phase-2-pid-becomes-preference-not-pin>
Addresses finding-2.

Goal: `--pid <N>` documents intent but does not break on restart.

**Changes in `src/Acd.Mcp.Bridge/AutoCadDiscovery.cs`:**

- `ResolvePid(int? explicitPid)` becomes layered:
  1. If `explicitPid` is provided AND the process exists AND it owns a
     reachable pipe (phase-3) → return it.
  2. Otherwise log a one-line warning ("pinned PID N is no longer
     reachable, falling back to discovery") and run the discovery path.
  3. Discovery path uses the pipe-probe rule (phase-3) to pick the
     right one.
- Add a structured result type
  `PidResolution { int Pid; PidResolutionReason Reason; }` so callers
  can record *why* a particular PID was chosen — useful for the status
  command and for logs.
- Replace `Process.GetProcessById(...).Id` (which races with process
  exit) with a wrapper that catches both `ArgumentException` AND
  `InvalidOperationException` (`HasExited`).

The bridge constructor stores the preference; resolution still happens
per call.

**Tests**

- `AutoCadDiscoveryTests` with mocked `IProcessEnumerator` + `IPipeProber`
  so the test can simulate each branch:
  - pinned PID dead, one healthy fallback,
  - pinned PID dead, no fallback,
  - two healthy fallbacks,
  - pinned PID alive but pipe down.
</phase-2-pid-becomes-preference-not-pin>

<phase-3-pipe-probing-multi-instance-discovery>
Addresses finding-3.

Goal: be the one piece of code on the workstation that can answer
"which acad.exe is *mine*."

**Design**

- New `PipeProber` in `src/Acd.Mcp.Bridge/`:
  ```csharp
  Task<bool> IsListening(int pid, TimeSpan timeout, CancellationToken ct);
  ```
  Implementation: open a `NamedPipeClientStream` for `acd-mcp-{pid}`,
  await `ConnectAsync(timeout)`. Any connect = listening. Use a tight
  150 ms timeout; the prober is called per candidate in parallel via
  `Task.WhenAll`.
- `AutoCadDiscovery.ResolvePid` after phase-2 changes:
  - Enumerate all `acad.exe` PIDs.
  - In parallel, probe each. Collect the set of "mine."
  - 1 mine → return it.
  - 0 mine, 1 raw → return it with
    `Reason=OnlyAcadProcessNoPipeYet` (caller treats this as worth
    retrying — see phase-4 backoff).
  - 0 mine, >1 raw → ambiguous-but-no-plugin error; mention `--pid`.
  - >1 mine → genuine multi-instance error; mention `--pid`.
- The probe used at discovery time is the *same* probe the connect
  retry loop uses, so one implementation backs both.

**Tests**

- `PipeProberTests` with a real listening server (`NamedPipeServerStream`)
  in-process to validate connect / timeout / cancel.
- `AutoCadDiscoveryTests` covering each branch above.
</phase-3-pipe-probing-multi-instance-discovery>

<phase-4-connect-retry-and-structured-transport-errors>
Addresses finding-4, partly finding-7.

Goal: the restart window (10–30 s) becomes invisible to the agent.

**Changes in `src/Acd.Mcp.Bridge/PipeClient.cs`:**

- `SendAsync` gains a `RetryPolicy` collaborator. Default policy:
  3 attempts at 200 ms / 800 ms / 2000 ms connect timeouts
  (cumulative ~3 s), then surface the failure. The *original* call-level
  `connectTimeoutMs` parameter is reinterpreted as the *total* budget;
  the policy splits it.
- A failed connect retries through the full PID-resolution path on each
  iteration — so if the bridge initially saw "0 mine, 1 raw" and the
  user types `ACDMCP_START` mid-retry, the next attempt picks up the
  now-listening pipe automatically.
- Errors that should NOT be retried:
  - `OperationCanceledException` from the caller-supplied CT.
  - `AcadRpcException` (the plugin replied — protocol-level failure).
- All retryable failures funnel through `AcadTransportException` with
  a structured `Reason` enum:
  `PipeNotListening`, `MultipleAutoCads`, `NoAutoCadFound`,
  `PinnedPidGone`, `ConnectTimeout`. The MCP tool wrappers map this to
  envelope errors with a stable `error_code` (today they just stringify
  `AcadRpcException.Code`).

**Tests**

- `PipeClientRetryTests` with a mock `IPipeTransport`:
  - "Listener up on attempt 2" → success.
  - "Listener never comes up" → final timeout surfaces with
    `Reason=PipeNotListening` and the right message.
  - Caller-supplied CT cancellation aborts mid-retry.
</phase-4-connect-retry-and-structured-transport-errors>

<phase-5-release-auto-start-parity>
Addresses finding-6.

Goal: kill the silent step that costs users a manual `ACDMCP_START`
per session.

**Changes in `src/Acd.Mcp/McpPlugin.cs`:**

- Promote the DEBUG-only `Application.Idle` auto-start hook to RELEASE.
- Add an opt-out: read `%LOCALAPPDATA%\Acd.Mcp\config.json` for an
  `auto_start: false` flag. Absence = auto-start. This is a *file*
  (not a registry / env var) so users can drop a one-liner without
  editing system state, and so it's reviewable on a workstation.
- On the first `EditorMessage` after load (DEBUG and RELEASE), print
  one explicit line:
  ```
  [ACD-MCP] listener up on 'acd-mcp-<pid>' (auto-start). Use ACDMCP_STOP
  to disable, or set auto_start:false in <path> to opt out.
  ```

**Tests**

- Plugin-side: a fast smoke test (run inside `acoreconsole.exe` if
  available) asserting `ACDMCP_STATUS` shows `Listener: running`
  immediately after `Initialize()` under both DEBUG and RELEASE builds.
</phase-5-release-auto-start-parity>

<phase-6-structured-status-and-error-surface>
Addresses finding-7, plus cross-cutting cleanup.

Goal: a single canonical description of "what works right now" that
agent, user, and logs all share.

**Changes:**

- Promote `ACDMCP_STATUS` to print a structured table that includes,
  for each capability (`pipe`, `script.execute`, `script.propose`,
  `batch.*`, `dto.*`), one of:
  - `ready`
  - `degraded(<reason>)`
  - `unavailable(<reason>)`
  After phase-1, everything except literal palette-only features is
  `ready` once the listener is up.
- Add a new RPC method `acdmcp.status` returning the same shape as
  JSON. The bridge exposes a read-only `acd-mcp://status` MCP resource
  (not a tool) backed by it, so the agent can self-diagnose without
  firing a side-effectful call.
- Every dispatcher error from `ExtraRpcMethodHandler` carries a
  structured `error_code` string from a finite enum
  (`PALETTE_CLOSED`, `DTO_NOT_READY`, `PLUGIN_NOT_INITIALIZED`, etc.)
  instead of a free-text message. The propose tool wrappers (which
  already swallow `AcadRpcException` into a `ProposeScriptResult`)
  propagate the code directly so the agent's skill can branch on it.

**Tests**

- `StatusResourceTests` against a running listener — assert the JSON
  shape and each enum value.
</phase-6-structured-status-and-error-surface>

<phase-7-resource-manager-invariants-and-unload-safety>
Review-adjacent, code-quality. None of these are reported bugs today,
but they shorten the blast radius of future changes.

- The static fields in `McpPlugin` (`_scriptRpc`, etc.) are nulled in
  `Terminate()` *after* `_resources!.Dispose()`. Today that's fine
  because nothing inside `Dispose` touches the statics. After phase-1,
  `_scriptRpc.Dispose()` (if added) might. **Tighten the invariant:**
  every static is registered with `_resources` to be nulled by a
  `RegisterAction` step, so `Terminate` becomes the one-liner
  `_resources?.Dispose()`. Removes a class of "did you remember to
  add it to the Terminate list?" bugs.
- `ResolvePid` per-call is fine but the *enumeration*
  (`Process.GetProcessesByName`) is also called per agent call after
  phase-3. Hold a `Process[]` only inside one method (don't cache;
  correctness > microseconds).
- `PipeClient.SendAsync` allocates a fresh client per call (documented
  as deliberate). Keep that; document that the retry loop also
  allocates fresh clients each attempt — necessary because
  `NamedPipeClientStream` cannot be reconnected after `ConnectAsync`
  fails.
</phase-7-resource-manager-invariants-and-unload-safety>

<phase-8-documentation-skills-and-design-doc>
- Update `README.md` "Architecture" section to reflect the new
  "listener-up = fully functional" guarantee.
- Rewrite the "First run" subsection: a single `ACDMCP_START` line
  (or just "open AutoCAD, plugin auto-starts").
- Update `docs/design/script-editor-refactor-v1.md` to mark the
  palette-coupling decision as superseded; cross-link to the new doc.
- Add `docs/design/lifecycle-and-discovery-v2.md` capturing the new
  contract: pipe probe, retry policy, structured error codes, status
  resource. This is the document a future reviewer should read first.
- Skills (`acd-mcp:script`, `acd-mcp:batch`): update them to instruct
  the agent to consult `acd-mcp://status` if a transport error
  surfaces, and to recover (rather than escalate to the user) on
  `Reason=ConnectTimeout` since the retry will already have fired.
</phase-8-documentation-skills-and-design-doc>

<suggested-merge-order>
1. **Phase 0** (release hygiene) — small, isolating change; everything
   else assumes the new names.
2. **Phase 3 + Phase 4** (pipe prober, retry, structured errors) —
   they share `PipeProber`, ship together. Deliver finding-3 and
   finding-4 wholesale; bridge-only.
3. **Phase 2** (`--pid` as preference) — depends on phase-3's prober.
4. **Phase 1** (decouple RPC from palette) — plugin-only, biggest
   functional win.
5. **Phase 5** (RELEASE auto-start) — small, ships after phase-1 so
   first-call success is guaranteed.
6. **Phase 6** (status surface) — uses everything above.
7. **Phase 7** (resource invariants) — janitorial; can ride with phase-1.
8. **Phase 8** (docs / skills) — last so the docs describe shipped
   behaviour.
</suggested-merge-order>

<out-of-scope>
Intentionally excluded from this plan:

- Batch / Script engine internals (the source review explicitly
  excluded them).
- Switching the wire protocol away from one-shot JSON-RPC over named
  pipes. The current shape survives plugin hot-reload and is well-
  matched to the workload.
- A persistent connection or push-from-plugin channel (would be
  needed for `notifications/tools/list_changed`, but phase-1 removes
  the *need* for that notification).
</out-of-scope>
</content>
</invoke>