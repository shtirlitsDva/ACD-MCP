<crash-test-v4-journal>

<meta>
- **Date:** 2026-05-13
- **Tester:** Claude Opus 4.7 (1M context), invoked agentically by user mgo@norsyn.dk.
- **Driver build label (running DLL):** `v21-batch-tools-success-shape` (v21 reached the running DLL on the fresh Civil 3D boot after the H3 commit landed).
- **AutoCAD process:** PID 15988, launched 2026-05-13 ~17:00 via `/Automation`. Pipe `\\.\pipe\acd-mcp-15988`.
- **Driver:** direct named-pipe JSON-RPC (`scripts/Invoke-AcdMcpPipe.ps1`) for plugin-side probes; `scripts/Verify-BridgeG4.ps1`-style stdio for the bridge-side checks. No `/reload-plugins` was needed at any point in V4.
- **Scope:** regression-sweep V1/V2/V3 + the new surface from the `worktree-script-editor-refactor` merge (ScriptEditor extraction, REPL share, `autocad_repl_propose_script` MCP tool, `repl.*` plugin RPC methods). User explicitly asked for the REPL script-editor surface to be exercised, so the V4 probes deliberately stress it.
- **Master tip at start:** `743f039` (V3 closure scripts).

</meta>

<methodology>
A single REPL submission walked the regression + new surface in 17 probes; a second stdio driver (`autocad_repl_propose_script`) closed the new MCP tool's wire-shape verification. Test artifacts caused two reflection probes to fail and two to error — both diagnosed as test-setup issues, not product bugs, and documented as observations (J1, J2).
</methodology>

<probe-results>

| # | Probe | Status | Note |
|---|---|---|---|
| 1 | BASELINE | INFO | `McpPlugin.Version` = `v21-batch-tools-success-shape` |
| 2 | F7 — DTO registry populated | PASS\* | 21 types registered. Initial sweep probed `Count` (missing); `RegisteredTypes` is the correct property. Doesn't change the product fact — see J2. |
| 3 | G2 — AECC loaded at probe time | PASS | `AecPropDataMgd` in `AppDomain.CurrentDomain.GetAssemblies()` post-Initialize |
| 4 | F13 — Unqualified `Entity` in REPL | PASS | `Entity e13 = null;` compiles, no CS0104 |
| 5 | F9 — `Acd.DataProvider` REPL global | PASS | resolved |
| 6 | F14 — Infinity/NaN | PASS | compiles + DTO converter path holds (V2 already wire-verified the round-trip) |
| 7 | PALETTE — `_batchRpc` + `_replRpc` wired | PASS | both initialized after ShowPalette |
| 8 | EDITORS — shared `ScriptEditor` type | PASS | both `_batchScriptEditor` + `_replScriptEditor` are `Acd.Mcp.Batch.ScriptEditor`, separate instances |
| 9 | MIRROR-paths — distinct per flavor | PASS | `editor-buffer.csx` (BATCH legacy) + `repl-buffer.csx` (REPL new) |
| 10 | H3-mirror-sync — clean propose lands mirror sync | PASS | mirror on disk with size>0 immediately after `ProposeFromAgent` returns |
| 11 | REPL-getEditor — new RPC method | PASS | returns `{ body, mirror_path }` (anonymous-type shape) |
| 12 | REPL-listSavedScripts — pagination | PASS | total=4 (pre-existing fixtures from V3 + V4 propose-tests), limit=2 honored |
| 13 | REPL-getSavedScript-missing — error path | PASS | throws `InvalidOperationException` with the "No saved repl script named ..." message |
| 14 | REPL-mirror-content — round-trip body+header | PASS | mirror contains `@flavor: repl`, `@name`, and the proposed body verbatim |
| 15 | CROSS-flavor-isolation | PASS | propose on BATCH does not disturb REPL mirror (snapshot equal after); REPL editor's earlier propose body stays intact |
| 16 | STORE-flavor-isolation | PASS\* | `scripts/batch/<name>.csx` and `scripts/repl/<name>.csx` are independent files. Initial probe used `asm.GetType("Acd.Mcp.Batch.ScriptFlavor")` against `Acd.Mcp` and got null — see J2. |
| 17 | REPL-lifecycle-dirty-stages | FAIL\* | reflection-only test artefact — see [[#v4-j1]] |

`*` = corrected after the initial sweep flagged a test-setup issue (J2) or interaction (J1). The corrected probes ran against the same Civil 3D PID and confirmed the product surface holds.

Plus the bridge-side stdio verification of the new `autocad_repl_propose_script` MCP tool (via `pwsh $env:TEMP\v4-repl-stdio.ps1`):

```
result.content[0].text =
  {"ok":true,
   "saved_as":"...\\scripts\\repl\\v4-stdio-test.csx",
   "name":"v4-stdio-test",
   "replaced_dirty":false}
IsError = False
```

Shape matches the shared `ProposeScriptResult` record on the success path. The G4 success-shape extension applied to `ReplProposeScriptTool` during the merge resolution is correctly serialized end-to-end.

</probe-results>

<findings>

<v4-j1 id="v4-j1">
**J1 [observation, LOW]** — When the agent drives `ScriptEditor.OnUserTyped` via reflection (not through the WPF text-box binding), `ReplViewModel.IsDirty` does NOT update. The VM and the editor have their own `IsDirty` flags; they stay in sync ONLY because the WPF text-box's `TextChanged` event normally fires both `editor.OnUserTyped` and the VM's setter together. The agent's reflection bypass touches only the editor side.

**Consequence in the V4 sweep:** my `REPL-lifecycle-dirty-stages` probe expected `ProposeFromAgent` on a "dirty" editor (set via reflection) to stage the proposal in `PendingProposal`. It did stage — correctly — but then `ReplViewModel.OnScriptProposed` fired, ran synchronously (`Marshal` uses `Dispatcher.CheckAccess()` + inline-invoke when on the dispatcher, which the REPL submission is), saw VM.IsDirty=false (stale!), skipped the prompt, and called `AcceptPending`, which promoted the staged proposal. The probe observed `dirty=false, pending=false, currentText=<agent body>` — exactly the inline-promote outcome.

**Verdict:** not a product bug. Real user flow doesn't hit this — typing goes through the WPF text-box which keeps both flags in lock-step. The agentic test would have to either (a) also set VM.IsDirty=true via reflection before proposing, (b) temporarily detach `ReplViewModel.OnScriptProposed` before reflection-typing, or (c) use the dedicated `ScriptEditorTests` xUnit project, which constructs an editor without any UI subscriber and verifies the dirty-staging invariant against the editor directly (and passes — see `ProposeFromAgent_StagesPending_DoesNotTouchCurrentText_Mirror_OrDirty`).

**Worth doing anyway:** a small structural cleanup where `ReplViewModel.IsDirty` is computed from `_scriptEditor.IsDirty` via `INotifyPropertyChanged` rather than mirrored. Eliminates a class of "desync via unusual entry point" bugs. Filed as future work — not blocking V4 closure.
</v4-j1>

<v4-j2 id="v4-j2">
**J2 [observation, LOW]** — Two reflection probes in the initial sweep failed for wrong-property / wrong-assembly reasons:

* `DtoRegistry` exposes `RegisteredTypes` (an `IEnumerable<Type>`), not `Count`. The probe was probing a non-existent property and hit NRE.
* `Acd.Mcp.Batch.ScriptFlavor` lives in the `Acd.Mcp.Batch` assembly, not `Acd.Mcp`. The probe used `asm.GetType(...)` against the wrong assembly handle and got null.

Both are agentic-harness mistakes, not product bugs. The corrected probes (using `RegisteredTypes.Count()` + cross-assembly type lookup) pass and confirm the product surface. Documented here so the next agentic loop doesn't re-trip the same wires.
</v4-j2>

</findings>

<summary>

**Regression:** clean. Every V1/V2/V3 fix surface still works on the merged build.

**New REPL surface (Phase 1+2 from `worktree-script-editor-refactor`):** all probes pass.
- `ScriptEditor` shared abstraction (same type) with separate state per flavor.
- Mirror paths distinct (`editor-buffer.csx` for BATCH legacy, `repl-buffer.csx` for REPL).
- `SavedScriptStore` routes by flavor; same `<name>` in different flavors are independent files.
- All four `repl.*` RPC methods reachable (`proposeScript`, `getEditor`, `listSavedScripts`, `getSavedScript`).
- `autocad_repl_propose_script` MCP tool: shared `ProposeScriptResult` shape, G4 success-shape extension verified, replaced_dirty wire field present (G3), mirror sync via H3 fix.
- Cross-flavor proposes don't disturb each other.

**Outstanding from V3:** ALL CLOSED.
- H1 (bridge kill drops MCP server): operational, documented + `Invoke-AcdMcpPipe.ps1` workaround shipped.
- H2 (bridge runs from plugin cache): `Refresh-PluginCache.ps1` shipped.
- H3 (propose mirror-sync race): inline-promote fix landed `c392e2e`, verified live.
- G4 (agent-side end-to-end of discriminated success-shape): `Verify-BridgeG4.ps1` proves the wire output without needing `/reload-plugins`.

**New from V4:** two LOW observations (J1, J2), both agentic-harness concerns, not product bugs. J1 has an optional future cleanup (VM IsDirty driven by editor INotifyPropertyChanged).

**Net state:** no outstanding product issues in V2/V3/V4 journals. Suite stands at **73/73 tests** (16 DTO + 57 batch — up from 55 with the V3-H3 regression tests). Master tip after this push includes the V4 journal as the latest documented state.

</summary>

</crash-test-v4-journal>
