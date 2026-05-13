<crash-test-v3-journal>

<meta>
- **Date:** 2026-05-13
- **Tester:** Claude Opus 4.7 (1M context), invoked agentically (user AFK) by user mgo@norsyn.dk.
- **Driver build label (running DLL):** `v20-debug-idle-autostart`. Source is at `v21-batch-tools-success-shape` but `Acd.Mcp.dll` could not be re-baked this session — `Acd.Mcp.Api.dll` is in the Default ALC and locked by the live Civil 3D process; per @mgo's caveat, that file is not hot-reloadable. `v21` will land in the running DLL on the next full Civil 3D restart. The bridge DLL (`bin/Acd.Mcp.Bridge.dll`) WAS rebuilt and redeployed mid-session and carries the G4 success-shape fix.
- **AutoCAD process:** PID 4732, launched `2026-05-13 12:29` via `/Automation` from a hidden COM-server boot. Pipe `\\.\pipe\acd-mcp-4732`.
- **Driver:** direct named-pipe JSON-RPC (PowerShell helper at `$env:TEMP\acdmcp-pipe-client.ps1`). The MCP bridge was reachable for the first round of verifications; later in the session it was deliberately killed to redeploy the new dll, which disconnected the Claude Code MCP server (see [[#v3-h1]]) and the rest of the verifications were done through the pipe.
- **Fixtures:** `crashtest-v2-dwgs/crashtest-0[1-5].dwg` regenerated via the REPL recipe in `CRASH_TEST_V2_JOURNAL.md#dwg-generation`. Entity counts 15/18/12/15/18 — match the V2 table.
- **V2 closure state at session start:** master tip `aca9259`; closed during session up to `86fe797`.

</meta>

<methodology>
Walked every V2 finding (G1–G9) end-to-end through the pipe + driven via reflection into `McpPlugin._batchRpc._uiState`. Then a V3 regression sweep across the V1 finding set (F7/F9/F13/F14) to make sure nothing degraded while V2 was being closed. New findings (H1, H2) emerged from operational seams the V2 journal didn't anticipate.
</methodology>

<v2-findings-closure>

| ID | V2 status | V3 evidence | Verdict |
|---|---|---|---|
| **G1** — Version label not bumped | smell, LOW | `McpPlugin.Version` const bumped to `v21-batch-tools-success-shape`; convention noted in journal for future bumps. Source-only; baked into DLL on next restart. | **FIXED** |
| **G2** — AECC lazy-load race | BUG, HIGH | Source fix `ea21ef6` (force-load `AecPropDataMgd` in `Initialize` via `Type.GetType`). PID-4732 cold start logged `PropertySetProvider: AECC PropertySets available via AecPropDataMgd.` at +4 s post-Initialize; `aecc_loaded=true` via REPL inspection of `AppDomain.CurrentDomain.GetAssemblies()`. | **FIXED + VERIFIED** |
| **G3** — `replaced_dirty` hidden | BUG, MEDIUM | Source already `bool?` (v17). Direct-pipe `batch.proposeScript` response now includes `replaced_dirty: false` as a wire field (snake_case wire key — `JsonNamingPolicy.CamelCase` is PascalCase→camelCase, not snake→camel; record fields are already snake_case in source, so wire = source). | **FIXED + VERIFIED** |
| **G4** — Batch errors not surfaced to agent | BUG, HIGH | Bridge tools (`BatchRunTestTool`, `BatchProposeScriptTool`, `BatchGetSelectionTool`) converted to never-throw discriminated success-shape `{ ok, error_code, error_message, ...payload }` (commit `ad52415`). Diagnose phase confirmed the plugin DOES emit the readable message on the pipe wire — observed `{ error: { code: -32603, message: "BATCH palette is not open. ..." } }`. Bridge dll redeployed to repo `bin/` + plugin cache. Live agent-side end-to-end observation pending: killing the bridges to swap the dll also dropped the in-session MCP server, and Claude Code does not auto-respawn (see [[#v3-h1]]). Once a `/reload-plugins` happens, calling `autocad_batch_get_selection` while the palette is closed should now return `{ ok: false, error_code: "-32603", error_message: "..." }` to the agent. | **FIXED (source + diagnose); agent-side observation pending user `/reload-plugins`** |
| **G5** — No DTO smoke test | smell, LOW | `scripts/Test-DtoSmoke.ps1` delivered — boots Civil 3D `/Automation`, tails `log.txt` for `EnsureDtoGraph: Registered N DTO types` with `N >= MinDtoCount`, restores `plugins.json` on teardown. Wiring into CI is gated on a self-hosted Civil 3D-licensed Windows runner (see `docs/computer-use-from-claude-code.md` `<qa-standardization>`). | **FIXED (script delivered)** |
| **G6** — Batch can't compile any body | BUG, CRITICAL | Fixed by G8 (v18 contracts-split). V3 confirms via the G9 run path below — bodies referencing `IBatchContext`/`AcadBatchGlobals` compile and execute. | **FIXED (verified via G9)** |
| **G7** — Reflection-via-`_batchRpc._uiState` works for AFK | observation | Pattern used extensively this session — drives `Folder`/`Mask`/`Recurse`/`LiveSelected`/`RefreshCommand`/`RunCommand` through `BatchViewModel` reflection plus `BatchExecutor.RunCompleted` event subscription. Cycles run cleanly. | **HOLDS** |
| **G8** — Contracts-split | BUG, CRITICAL | Verified by V2 journal and re-confirmed today: `Acd.Mcp.Api` + `Acd.Mcp.Contracts` in Default ALC, `Acd.Mcp` + `Acd.Mcp.Batch` in `PluginIsolated`. | **FIXED** |
| **G9** — Civil 3D namespace collision on `Entity` | smell, LOW | v19 dropped `Autodesk.Civil.*` from `BatchScriptRuntime.WithImports`. Proposed `g9-unqualified-entity` body using `var e = (Entity)xTx.GetObject(...)` and ran Test against the fixtures — **5/5 Pass, 0 Fail, 0 diagnostics.** | **FIXED + VERIFIED** |

</v2-findings-closure>

<v1-regression-sweep>

| Finding | Probe | Verdict |
|---|---|---|
| **F7** — DTO registration | `_dtoRegistry` reachable from REPL via reflection; **21 types registered** — matches V2 baseline. | **OK** |
| **F9** — `Acd.DataProvider` in REPL | `Acd.DataProvider` resolves from inside REPL submission. | **OK** |
| **F13** — Unqualified `Entity` ambiguity in REPL | `Entity e = null;` at top of submission compiles, no CS0104. | **OK** |
| **F14** — Infinity/NaN serialization | `double.PositiveInfinity` / `NegativeInfinity` / `NaN` round-trip cleanly through the REPL value path; wire-side proof deferred since the running DLL's serializer config wasn't surfaced in this probe. (V2 already verified the wire shape; treated as held.) | **OK (held)** |
| **G2** — AECC at probe time | `AppDomain.CurrentDomain.GetAssemblies()` includes `AecPropDataMgd`. | **OK** |
| **REPL globals** — `Doc`/`Db`/`Ed` | `Doc.Name=Drawing1.dwg`, `Db.Filename=<Civil 3D Metric template>` — globals wire up. | **OK** |

7 probes, 0 fail, 0 err. No regressions detected from V2's closure work.

</v1-regression-sweep>

<new-findings>

<v3-h1 id="v3-h1">
**H1 [smell, MEDIUM]** — Killing the MCP bridge processes (`Acd.Mcp.Bridge.exe`) to redeploy a freshly-built `Acd.Mcp.Bridge.dll` **disconnects the in-session Claude Code MCP server permanently for the current conversation**. After kill, `ToolSearch` reports the four `mcp__plugin_acd-mcp_acd-mcp__*` tools as unavailable with a "no longer available (their MCP server disconnected)" hook message; subsequent calls to any of them fail with `No such tool available`. The bridges are not auto-respawned by the Claude Code harness on a tool-call attempt — recovery requires the user to run `/reload-plugins` (or restart Claude Code), neither of which an agent can self-trigger.

Observed concretely this session: at 12:34 the agent killed PIDs 49744+50516 to swap in the post-G4 Bridge.dll. The four acd-mcp tools dropped from the available-tool list at the next system-reminder, and remained gone for the rest of the session — verification work continued via the direct named-pipe helper (talking directly to `\\.\pipe\acd-mcp-4732`, bypassing the MCP server entirely).

**Why it matters:** the documented `<reload-the-plugin-procedure>` (docs/computer-use-from-claude-code.md) covers the **AutoCAD-side** reload (DevReload ACDMCPUNLOAD/LOAD). It doesn't cover the **bridge-side** reload. For an agentic loop that wants to iterate on bridge code (the entire `Acd.Mcp.Bridge/Tools/*.cs` surface and the JSON contracts it exposes), the loop is broken: change source → publish to cache → kill bridge → ... → MCP server stays dead until the human in the loop hits `/reload-plugins`. The diagnose half of G4 was reachable; the agent-side end-to-end half was not.

**Fix options, in order of preference:**

1. **Auto-respawn on disconnect in the Claude Code MCP transport.** Not actionable from this repo. Worth a bug report to Anthropic.
2. **Hot-reload at the bridge level — bridge polls its own dll mtime, exec-replace on change.** Architecturally awkward (a child process replacing itself), but doable with `Process.Start(...).WaitForExit()` + re-spawn-from-parent.
3. **Skip the kill — make the bridge re-load its `Acd.Mcp.Bridge.Tools.dll` from disk on each call.** Requires splitting bridge into a thin server + a hot-reloadable tools library. Significant.
4. **Document the constraint, work around with a direct-pipe harness for bridge-side iteration.** Cheap. The pipe-client recipe is already shown in this journal; a more durable home is `docs/computer-use-from-claude-code.md` under a new `<bridge-side-iteration>` subsection.

Option 4 is the right call short-term. Option 1 is the structural fix.

**RESOLVED via documented workaround (2026-05-13):** `scripts/Invoke-AcdMcpPipe.ps1` ships the productionized direct-pipe client. A new `<bridge-side-iteration>` section in `docs/computer-use-from-claude-code.md` documents the constraint and walks the iteration loop:
1. Edit bridge source.
2. `pwsh scripts/Refresh-PluginCache.ps1 -Publish` (combined publish + cache copy — closes [[#v3-h2]]).
3. `/reload-plugins` in Claude Code (the unavoidable user-side step — the harness still does not auto-respawn on bridge disconnect).
4. For AFK / agentic loops that can't `/reload-plugins`, drive the plugin via `scripts/Invoke-AcdMcpPipe.ps1` directly — same RPC surface as the MCP tools but bypasses the bridge entirely.

The structural fix (option 1, Claude Code transport auto-respawn) remains an open feature request against Anthropic; nothing about this codebase blocks it.
</v3-h1>

<v3-h2 id="v3-h2">
**H2 [smell, MEDIUM]** — The MCP bridge runs out of the **plugin cache** at `~/.claude/plugins/cache/acd-mcp/acd-mcp/0.1.0/bin/Acd.Mcp.Bridge.exe`, NOT out of the repo's `bin/Acd.Mcp.Bridge.exe`. The repo's `.mcp.json` resolves `${CLAUDE_PLUGIN_ROOT}` to the cache (per Claude Code's plugin-loader convention). The cache is a snapshot of the repo's `bin/` taken at `/plugin install` time; **a fresh `dotnet publish` or even a `git pull` of the repo does NOT update the running bridge.** The agent rediscovered this the hard way trying to verify a fresh G4 fix — the new dll sat in `bin/` and `src/Acd.Mcp.Bridge/bin/Debug/`, while the running bridge was still serving the old (52224-byte) dll from the cache.

**How to update the cache:**
```powershell
$cache = "$env:USERPROFILE\.claude\plugins\cache\acd-mcp\acd-mcp\0.1.0\bin"
$src   = "C:\path\to\repo\publish_temp_bridge"
Stop-Process -Name Acd.Mcp.Bridge -Force
Copy-Item "$src\Acd.Mcp.Bridge.dll" "$cache\Acd.Mcp.Bridge.dll" -Force
# Then user runs /reload-plugins
```

(...combined with [[#v3-h1]], this is an awkward iteration loop.)

**Fix options:**

1. **`.mcp.json` points at the repo `bin/` via an env-var override**, so plugin-installed users get the cache and developers get the working tree. Cleanest. Requires Claude Code MCP semantics for absolute-path overrides via env.
2. **`install-hooks/Install-Mcp.ps1` symlinks** `~/.claude/plugins/cache/acd-mcp/.../bin` → `<repo>/bin`. Symlinks survive in NTFS. Side-effect: `/plugin update` would have to be careful not to clobber the symlink.
3. **Build-Release.ps1 also refreshes the cache** when run on a dev machine where the cache exists. Easy. Doesn't cover the "I just built locally" case unless the developer remembers to also run the script.

Option 3 is the right minimum. Option 1 is the structural fix.

**FIXED (2026-05-13):** `scripts/Refresh-PluginCache.ps1` shipped. The script enumerates every `~/.claude/plugins/cache/acd-mcp/acd-mcp/<version>/bin/` and copies the repo's `bin/` over it. With `-Publish`, it also runs `dotnet publish src/Acd.Mcp.Bridge -c Release -o bin/` first, so a single command refreshes the iteration loop. Warns when live bridges are running under the refreshed cache (their old code stays in memory until `/reload-plugins`).

Build-Release.ps1 was NOT modified — its job is "build + assemble + zip a release", not "refresh local cache". `Refresh-PluginCache.ps1` is the dev-loop counterpart. Both share the underlying `dotnet publish bin/` step.
</v3-h2>

</new-findings>

<post-merge-script-editor-verification>

After the `worktree-script-editor-refactor` branch was merged to master (commit `3fdd9b2`, 2026-05-13 ~14:30), Civil 3D was relaunched with the merged build (PID 45220, plugin label `v21-batch-tools-success-shape` confirmed in `Initialize` log line). The new BATCH + REPL script-editor surface was exercised end-to-end through the same direct-pipe harness, since the killed in-session MCP bridge from earlier in the day was never respawned (see [[#v3-h1]]).

| Probe | Result | Notes |
|---|---|---|
| `_batchScriptEditor`, `_replScriptEditor` | both `Acd.Mcp.Batch.ScriptEditor` instances | shared abstraction holds — same type, separate state |
| `_batchRpc`, `_replRpc` | `BatchRpcHandler`, `ReplRpcHandler` | both wired by `ShowPalette` |
| `batch.proposeScript` | returns `{ ok, saved_as, name, replaced_dirty }` | shape unchanged from V2 — backward compatible |
| `repl.proposeScript` (new) | returns the same `{ ok, saved_as, name, replaced_dirty }` shape | DRY'd onto the shared `ProposeScriptResult` record (extended with G4 error fields during merge) |
| Saved-script store routes by flavor | `%APPDATA%\Acd.Mcp\scripts\batch\*.csx` + `…\scripts\repl\*.csx` | `SavedScriptStore` writes the right subfolder per flavor |
| Mirror files | `editor-buffer.csx` (BATCH, legacy path) + `repl-buffer.csx` (REPL, new) | both written; content matches the proposed body verbatim including the `// @flavor / @name / @summary` header |

Plus the new `autocad_repl_propose_script` MCP tool now ships with the same G4 success-shape error propagation as the BATCH proposer (catches `AcadRpcException`, returns `ok: false` with `error_code` + `error_message`). That extension landed during the merge conflict resolution since the worktree had authored the new tool against the original throwing pattern.

</post-merge-script-editor-verification>

<v3-h3 id="v3-h3">
**H3 [BUG, MEDIUM, post-merge]** — `propose_script` (both batch and repl) returns `ok: true` BEFORE the mirror file (`%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx` / `repl-buffer.csx`) is flushed to disk. Measured race: pipe call returned in **60 ms**; mirror file appeared on disk at **+395 ms** (so ~335 ms of "ok but not yet on disk").

**Reproducer:**
```powershell
$p = "$env:LOCALAPPDATA\Acd.Mcp\repl-buffer.csx"
Remove-Item $p -Force -ErrorAction SilentlyContinue
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$rsp = Invoke-AcdMcpPipe -AcadPid $pid -Method 'repl.proposeScript' `
    -Params @{ name='race'; script_body='return 1+1;' }
"Propose ok in $($sw.ElapsedMilliseconds)ms; mirror exists = $(Test-Path $p)"
# Observed: ok in 60 ms; mirror exists = False for the next ~300 ms.
```

**Why it matters:** the documented workflow (in `skills/repl/SKILL.md` and `skills/start/SKILL.md`) instructs the agent to READ `editor-buffer.csx` / `repl-buffer.csx` BEFORE calling `propose_script` so it can plan its update against the user's in-flight edits. If the agent iterates — propose, then read-back to verify, or propose-again with a follow-up — it can race the previous iteration's flush and read STALE content. Worse: on the first-ever propose for a flavor, the mirror doesn't exist until the flush completes; an agent that pre-checks existence as a precondition will see "no mirror" and may pick a wrong code path.

**Root cause (probable):** the mirror flush is dispatched off-thread (likely via the WPF dispatcher) — `EditorBuffer` writes when `ScriptEditor._currentText` changes, but the write isn't awaited by the RPC handler. The propose RPC returns as soon as the `_currentText` is set, not when the mirror flush completes.

**Fix options:**

1. **Await the mirror write inside the RPC handler.** Cleanest — the agent's mental model ("propose returned, so the mirror is up to date") matches the wire reality. Cost: ~few ms added to propose latency.
2. **Add a `mirror_synced: bool` field to `ProposeScriptResult`** that's set to `false` when the flush is enqueued vs `true` when awaited inline. Lets the agent poll if it cares. Pollution of the shape; the agent has to remember to check.
3. **Document the latency in the skill docs** and tell the agent to sleep N ms before reading the mirror. Brittle; defers the actual fix.

Option 1 is the right call — the propose contract is "writes to disk + mirrors", that should be atomic from the caller's POV.

**FIXED (2026-05-13 ~16:13):** `ScriptEditor.ProposeFromAgent` now branches on `IsDirty`:

- **Clean editor (the common agent-iteration path):** inline-promote. Sets `_currentText = saved.Body`, `_pendingProposal = null`, calls `_mirror.SetText(saved.Body)` + `_mirror.FlushNow()` synchronously while still holding `_lock`. The mirror file is durable on disk before `ProposeFromAgent` returns. The `ScriptProposed` event still fires so UI subscribers can refresh their display; their `AcceptPending` becomes a no-op (pending already cleared) — intentional convergence.

- **Dirty editor:** existing staging model preserved — body parked in `PendingProposal`, event fired, UI prompts the user, `AcceptPending`/`DiscardPending` resolves. The mirror reflects the user's typed body the whole time, so the agent's "read-before-propose" workflow is honest.

`AcceptPending` was also tightened to call `_mirror.FlushNow()` after `_mirror.SetText`, so the dirty-path accept doesn't leak the same race when it lands. The 250 ms debounce is preserved for `OnUserTyped` (where it's actually useful — keystroke flurries).

**VERIFIED end-to-end (PID 49432, build label `v21-batch-tools-success-shape`):**

| Probe | Pre-fix observation | Post-fix observation |
|---|---|---|
| `repl.proposeScript` on clean editor, time-to-mirror-on-disk | propose ok at 60 ms; mirror lands at +395 ms (race window ~335 ms) | propose ok at **18 ms**; mirror **already on disk** at return time (51 B, correct content with `// @flavor / @name` header) |
| `batch.proposeScript` on clean editor | (same race) | propose ok at **14 ms**; mirror already on disk (105 B) |
| `ProposeFromAgent_StagesPending_DoesNotTouchCurrentText_Mirror_OrDirty` (dirty path) | pass | pass — staging model preserved |

Two new tests pin the H3 invariants: `ProposeFromAgent_OnCleanEditor_InlinePromotes_AndSyncFlushesMirror` and `AcceptPending_FlushesMirror_Synchronously`. Suite is now 57/57 (was 55/55).
</v3-h3>

<summary>

**V2 closure:** all 9 V2 gremlins are FIXED at source. G2/G3/G9 verified end-to-end through Civil 3D PID 4732. **G4 verified end-to-end** via direct bridge stdio (`scripts/Verify-BridgeG4.ps1`) on PID 40972: wire response is a normal tool result (IsError=False) with `{ok:false, error_code:"-32603", error_message:"BATCH palette is not open. ..."}` in `result.content[0].text`. No `/reload-plugins` was needed — the bridge process is spawned directly so the check is reproducible in any agentic loop.

**V3 regression sweep:** 7 probes across the V1 + V2 surfaces, 0 regressions.

**Operational gremlins (H1, H2):** both RESOLVED.
- **H2**: `scripts/Refresh-PluginCache.ps1` (publish + copy bin/ → cache) closes the cache-drift gap.
- **H1**: `scripts/Invoke-AcdMcpPipe.ps1` (direct pipe client) ships the documented workaround. Docs section `<bridge-side-iteration>` added to `docs/computer-use-from-claude-code.md`. Structural Claude Code auto-respawn remains an upstream-only feature request and is captured here in case anyone files it.

**Post-merge gremlin (H3):** the new (and the existing batch) `propose_script` path returned ok ~300 ms before the mirror file was actually on disk. **FIXED and verified — clean-editor proposals inline-promote (sync flush); dirty-editor proposals retain the staging-and-prompt model.** Propose-to-mirror-on-disk shrunk from ~395 ms to <20 ms. See `<v3-h3>` for evidence and design.

**State at end of session:**
- Civil 3D PID 4732 still running (the agent's `/Automation` instance).
- DevReload `plugins.json` RESTORED — `Acd.Mcp.loadOnStartup = false` for the next normal launch.
- Master tip: `86fe797` (`v21 + close V2 journal: G3/G4/G9 wire-verified, G1 version bump, G5 DTO smoke harness`).
- `crashtest-v2-dwgs/` regenerated locally (still gitignored).
- Sibling worktree `script-editor-refactor` (Phase 1+1.5 Extract ScriptEditor deep module) remains mid-refactor — uncommitted changes on its branch. Not safe to merge this session.

**For the next live agent or developer:**
1. Run `/reload-plugins` (or restart Claude Code) to respawn the bridge with the new dll. Then call `autocad_batch_get_selection` while the BATCH palette is closed; observe the discriminated `{ ok: false, error_code, error_message }` envelope and append the result to G4 in this journal as "end-to-end VERIFIED".
2. Either restart Civil 3D and re-run the agentic harness, or `Stop-Process` the agent's PID 4732 — the v21 string only reaches the running DLL on a fresh boot.
3. When the `script-editor-refactor` worktree finishes (commit "Phase N done"-style marker, or a clean working tree), merge to master and re-run the V3 sweep against the new editor surface.

</summary>

</crash-test-v3-journal>
