<handoff>

<tldr>
ACD-MCP V2 + V3 + V4 crash tests all closed. Master tip `1b66db0` on `origin/master`. Working tree clean modulo two untracked items by design (`crashtest-v2-dwgs/` gitignored fixtures; `docs/design/script-editor-refactor-v1.md` WIP that predates this session).

Highlights of what landed today (after the prior `docs/handoff-2026-05-13.md`):

- **V2 closure**: G2/G3/G4/G9 verified end-to-end. G1 (version bump) and G5 (`scripts/Test-DtoSmoke.ps1` log-tail harness) fixed.
- **Merged `worktree-script-editor-refactor`** (Phase 1+2 — ScriptEditor extraction + REPL share + new `autocad_repl_propose_script` MCP tool). Conflicts resolved to preserve master's G4 success-shape on the new shared `ProposeScriptResult` record. Plugin version bumped to `v21-batch-tools-success-shape`.
- **V3 (post-merge crash test)**: regression sweep clean; three operational gremlins found and **all three resolved**:
  - **H1** (bridge kill drops in-session MCP server, no auto-respawn) — workaround documented in `docs/computer-use-from-claude-code.md` `<bridge-side-iteration>` + `scripts/Invoke-AcdMcpPipe.ps1` ships the direct named-pipe client.
  - **H2** (running bridge serves plugin cache, not repo `bin/`) — `scripts/Refresh-PluginCache.ps1 [-Publish]` ships the cache-refresh path.
  - **H3** (propose RPC returns ok ~300 ms before mirror flush) — `ScriptEditor.ProposeFromAgent` inline-promotes on clean editor + `AcceptPending` adds `FlushNow`. Verified <20 ms mirror-on-disk post-propose vs ~395 ms pre-fix.
  - **G4 end-to-end** — `scripts/Verify-BridgeG4.ps1` spawns the bridge with stdio redirected and asserts the discriminated success-shape arrives on the wire. No `/reload-plugins` needed.
- **V4 crash test**: regression sweep + REPL-specific surface walk; 17 plugin-side probes + 1 stdio bridge probe; all real-product checks PASS. Two LOW observations (J1/J2) are agentic-harness artifacts, not product bugs.
- **Skill audit**: 5 drift items found, all 5 fixed (3 HIGH + 2 MEDIUM). HIGH: stale `<the-staging-model>` in `skills/repl/SKILL.md`, missing G4 response-shape docs in `skills/batch` + `skills/repl`, conflated error sources in `skills/start/<initial-checks>`. MEDIUM: mirror debounce semantics + ACDMCP_START DEBUG vs Release note.

Test suite: **73/73 pass** (16 DTO + 57 batch — the worktree merge added `ScriptEditorTests.cs` with 11 tests; V3-H3 fix added 2 more).
</tldr>

<commit-graph>
This session's commits, oldest first:

```
aca9259  docs: trim hot-reload + license caveats from computer-use doc
ad52415  G4: batch tools return discriminated success-shape on error
86fe797  v21 + close V2 journal: G3/G4/G9 wire-verified, G1 version bump, G5 DTO smoke harness
fcf5d1b  V3 journal: V2 closure verified, regression sweep clean, two operational gremlins
3fdd9b2  Merge worktree-script-editor-refactor: Phase 1-2 ScriptEditor extraction + REPL share
15ea350  V3 journal: post-merge script-editor verification + new H3
c392e2e  V3-H3: close propose_script -> mirror-on-disk race via clean-editor inline promote
743f039  V3 closure: H1, H2, G4-e2e — three scripts + bridge-side-iteration docs
9186546  V4 crash test: regression sweep clean, REPL surface verified, two LOW observations
1b66db0  skills: fix 5 drift items found in V4 audit
```

All pushed to `origin/master`. Master == origin/master at `1b66db0`.
</commit-graph>

<where-to-look>
- **Journals (chronological narrative + verified status):**
  - `CRASH_TEST_V2_JOURNAL.md` — G1–G9 all marked FIXED + the verification evidence each carries.
  - `CRASH_TEST_V3_JOURNAL.md` — H1/H2/H3 operational findings + RESOLVED notes pointing at the new scripts; G4 end-to-end VERIFIED note.
  - `CRASH_TEST_V4_JOURNAL.md` — regression sweep + REPL-specific probes + J1/J2 observations.
- **New scripts (all under `scripts/`):**
  - `Refresh-PluginCache.ps1` — dev-loop refresh of `~/.claude/plugins/cache/acd-mcp/.../bin/` from repo `bin/`. `-Publish` chains `dotnet publish` first.
  - `Invoke-AcdMcpPipe.ps1` — productionized direct named-pipe JSON-RPC client. Sidesteps the MCP bridge entirely when needed.
  - `Verify-BridgeG4.ps1` — spawns the bridge with stdio redirected, MCP handshake, `tools/call autocad_batch_get_selection` with palette closed, asserts the discriminated shape.
  - `Test-DtoSmoke.ps1` — log-tail smoke that boots Civil 3D `/Automation`, asserts `EnsureDtoGraph: Registered N DTO types` with N>=MinDtoCount, restores `plugins.json` on teardown.
- **Updated docs:**
  - `docs/computer-use-from-claude-code.md` — new `<bridge-side-iteration>` section with two gotchas (cache-vs-bin, kill-disconnects-session) and the three workarounds.
  - `skills/{start,batch,repl}/SKILL.md` — drift fixes per V4 audit. `skills/add-dto/SKILL.md` audited but no drift found.
- **Existing reference (unchanged, still authoritative):**
  - `docs/computer-use-from-claude-code.md` `<autonomous-bootstrap>` (steps 1–4) and `<repl-alc-typeof-trap>`.
</where-to-look>

<open-items>

<item-1-v4-j1-optional>
**V4-J1 cleanup (LOW, optional)** — `ReplViewModel.IsDirty` is a mirror of `ScriptEditor._isDirty`, kept in sync only because the WPF text-box's TextChanged handler fires both. When an agent reflects past WPF (e.g. calls `editor.OnUserTyped` directly), the VM stays stale. Not visible in real user flow; only surfaces in agentic testing.

Suggested fix: VM exposes `IsDirty` as a computed get-only property over `_scriptEditor.IsDirty`, and `ScriptEditor` raises `INotifyPropertyChanged` on its IsDirty changes (or an `IsDirtyChanged` event). VM subscribes and re-raises. ~20 LOC across `ReplViewModel.cs` + `BatchViewModel.cs` + `ScriptEditor.cs`. Adds a `ScriptEditor` xUnit test pinning the event fires on user-typed → propose → accept transitions.

Not blocking anything. Pure agentic-test ergonomics improvement.
</item-1-v4-j1-optional>

<item-2-design-doc-decision>
**`docs/design/script-editor-refactor-v1.md`** is an untracked WIP design doc that predates this session and survives across handoffs. Decide whether to commit, refresh, or delete. I left it alone.
</item-2-design-doc-decision>

<item-3-h1-upstream>
**H1 structural fix is an Anthropic-side bug report** — Claude Code's MCP transport currently does not auto-respawn a child server when the process dies. Workarounds shipped (see H1 in V3 journal + `<bridge-side-iteration>` docs section). If you want to chase the structural fix upstream, the report material is in `<v3-h1>` of `CRASH_TEST_V3_JOURNAL.md`.
</item-3-h1-upstream>

</open-items>

<next-machine-bootstrap>
On the new machine, to reach the same state:

1. **Clone or pull master.** Tip should be `1b66db0`.
   ```powershell
   git clone https://github.com/shtirlitsDva/ACD-MCP
   git -C ACD-MCP log -1 --oneline   # expect 1b66db0 skills: fix 5 drift items...
   ```

2. **Repoint DevReload to the new clone path.** Edit `%APPDATA%\DevReload\plugins.json`, find the `Acd.Mcp` entry, set its `dllPath` to your local clone's `src\Acd.Mcp\bin\Debug\Acd.Mcp.dll`. Keep `loadOnStartup: false` for normal use; an agentic loop flips it to `true` then restores on teardown.

3. **Build once** so the local `bin/Debug/` artefacts exist:
   ```powershell
   dotnet build Acd.Mcp.sln -c Debug
   ```

4. **(If you'll do agentic loops with MCP tools)** install the plugin via Claude Code's `/plugin` workflow OR push the repo's `bin/` into the plugin cache via:
   ```powershell
   pwsh scripts/Refresh-PluginCache.ps1 -Publish
   /reload-plugins   # in Claude Code
   ```

5. **For autonomous/AFK agentic loops**, follow `<autonomous-bootstrap>` in `docs/computer-use-from-claude-code.md` step-by-step. For bridge-side iteration (changes to `src/Acd.Mcp.Bridge/`), follow `<bridge-side-iteration>` in the same doc.

6. **Civil 3D path expectations baked into the scripts:** `C:\Program Files\Autodesk\AutoCAD 2025\acad.exe`, profile `<<C3D_Metric>>`. Each script accepts an override parameter if your install is elsewhere.

7. **Regenerate test fixtures** (the `crashtest-v2-dwgs/` folder is gitignored). The REPL recipe is in `CRASH_TEST_V2_JOURNAL.md#dwg-generation` (5 files, 15/18/12/15/18 entities, layer `CRASHTEST_V2`).
</next-machine-bootstrap>

<misc-gotchas>
- **`Acd.Mcp.Api.dll` does NOT hot-reload.** It lives in the Default ALC. Changes to types in `src/Acd.Mcp.Api/` require a full Civil 3D restart, NOT a DevReload ACDMCPUNLOAD/LOAD. The v21 → v22 path stays hot-reloadable as long as only `Acd.Mcp` / `Acd.Mcp.Batch` change.
- **Civil 3D licensing is one-instance-per-Windows-session.** Launching `/Automation` while the user has a visible Civil 3D open silently blocks on a license popup the agent cannot see. Always check `Get-Process acad` first; match by PID to avoid killing the user's instance.
- **Killing the bridge to swap a fresh dll drops the in-session MCP server** (V3-H1). Avoid it; use `Refresh-PluginCache.ps1` + `/reload-plugins` instead, or fall back to `Invoke-AcdMcpPipe.ps1` for AFK loops that can't `/reload-plugins`.
- **The plugin cache (`~/.claude/plugins/cache/acd-mcp/...`) is a snapshot taken at `/plugin install` time** (V3-H2). A fresh `dotnet publish` to repo `bin/` does NOT update the running bridge; use `Refresh-PluginCache.ps1` to sync.
- **DEBUG / DevReload builds auto-open the pipe** on first idle after Initialize (v20 hook). Release builds still require `ACDMCP_START` typing.
- **`Test-Path \\.\pipe\NAME` is unreliable** for named-pipe readiness. Use `[System.IO.Directory]::GetFiles("\\\\.\\pipe\\")` and match by name.
- **`SharedAssemblies.Config.json`** is now a tracked template at `src/Acd.Mcp/SharedAssemblies.Config.json` (committed at `71f4ef9`). MSBuild copies it to `bin/Debug/` per build via `PreserveNewest`. Don't hand-edit the `bin/Debug/` copy.
- **Plugin cache copy of `bin/`** at this session's machine includes the post-merge bridge dll (60928 B `Acd.Mcp.Bridge.dll`). On a fresh machine, `Refresh-PluginCache.ps1 -Publish` produces an equivalent.
</misc-gotchas>

</handoff>
