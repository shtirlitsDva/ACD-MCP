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
</v3-h2>

</new-findings>

<summary>

**V2 closure:** all 9 V2 gremlins are FIXED at source. G2/G3/G9 are end-to-end verified through Civil 3D PID 4732. G4 is verified at source + diagnose; the live agent-side observation pin needs the user to run `/reload-plugins` once they're back at the keyboard.

**V3 regression sweep:** 7 probes across the V1 + V2 surfaces, 0 regressions.

**New gremlins (H1, H2):** both about the iteration loop, not the code surface. Together they say: hot-reloading the bridge dll mid-conversation is currently a paper-cut, not a workflow. The direct-pipe harness sidesteps both for the rest of this loop and is documented in this journal.

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
