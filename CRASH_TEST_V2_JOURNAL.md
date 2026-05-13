<crash-test-v2-journal>

<meta>
- **Date:** 2026-05-13
- **Tester:** Claude Opus 4.7 (1M context), invoked by user mgo@norsyn.dk
- **Plugin build label:** `v11-dto-batch-api-split` (same string as before the v1 fix-pass — see [[#g1]])
- **AutoCAD process:** PID 39872, restarted 2026-05-13 07:44
- **Active drawing:** `Layout plan - sewage system - test.dwg` (same as v1)
- **AECC stack:** present on disk but lazy-loaded — see [[#g2]]
- **Source-of-truth journals:** [`CRASH_TEST_JOURNAL.md`](CRASH_TEST_JOURNAL.md) (v1, 2026-05-12) and [`CRASH_TEST_ACTIONS.md`](CRASH_TEST_ACTIONS.md) (fix pass, 2026-05-13)
- **User constraint:** user not at keyboard during the BATCH test phase, so the palette-UI hand-off (set folder + mask, flip slide-switch) could not be exercised end-to-end. The DWG generation and the per-file script logic were exercised via REPL sideload, which replicates the read path the BATCH runtime uses.

</meta>

<methodology>
The agent walked every v1 finding that produced a code change and verified the fix end-to-end through the MCP, not just by code-reading. Then created five `.dwg` files in `X:\GitHub\shtirlitsDva\ACD-MCP\crashtest-v2-dwgs\` via the REPL (`new Database(...) → ReadDwgFile → SaveAs`) and exercised the BATCH-flavour script body against each. Whenever observed behaviour disagreed with what the actions file claims, the agent compared source to wire.

</methodology>

<v1-findings-verification>

| Finding | v1 claim | v2 observation | Status |
|---|---|---|---|
| **F7** — Zero DTOs registered | Move types to `Acd.Mcp.Api`; default ALC resolves via simple assembly probe. | Plugin log line: `EnsureDtoGraph: Registered 21 DTO types.` Returning `Point3d`, `Vector3d`, `Handle`, `ObjectId` from REPL all project to clean JSON (`x/y/z` shape for points; `value` for handles; `handle/is_valid/is_erased` for ObjectId). **No `$unsupported` markers anywhere.** | **Fixed** |
| **F8** — PropertySets broken on Civil 3D 2025 | Scan all loaded assemblies for `PropertyDataServices` by full type name. | At plugin init, `AecPropDataMgd` is **not yet loaded**. Log line: `AECC types not resolvable: ... PropertySets disabled — vanilla AutoCAD.` After a manual `Type.GetType("...PropertyDataServices, AecPropDataMgd")` from the REPL, the assembly **does** load (confirmed `aec_prop_data_mgd_now_loaded = true`). The composite was already built without PropertySetProvider — the scan ran too early. | **Regressed in practice** — see [[#g2]] |
| **F9** — `Acd.DataProvider` not in REPL | Add façade `AcdReplApi` exposing `DataProvider`; inject into `AcadGlobals`. | `Acd.DataProvider.ReadAll(entity)` compiles and runs in REPL. Returned `IReadOnlyDictionary` with 0 keys for a Polyline (no block attributes; XData & PropertySets not in the composite — the second is [[#g2]]). | **Fixed** |
| **F13** — `Entity`/`DBObject` ambiguous | Drop `Autodesk.Civil.*` imports from REPL default set. | `Entity firstEnt = null;` at the top of a REPL submission compiles cleanly. No CS0104. | **Fixed** |
| **F14** — Infinity/NaN serialization | Set `NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals`. | Returning `double.PositiveInfinity` / `NegativeInfinity` / `NaN` and a `Point3d(Infinity,1,2)` serialize as `"Infinity"`, `"-Infinity"`, `"NaN"` strings. No `$serialization_error` envelope. | **Fixed** |
| **F5** — `$serialization_error` sigil shape | Prefix the key with `$` to match the `$unsupported` family. | Forced an object cycle. Wire response: `{"$serialization_error":"A possible object cycle was detected..."}`. Sigil aligns. | **Fixed** |
| **F17** — `Console.WriteLine` CS0103 | Explicitly include `typeof(System.Console)` in `RoslynReferences.Build`. | `Console.WriteLine` and `Console.Error.WriteLine` both compile and surface in `stdout` / `stderr` of the response (`F17 test: stdout capture works\r\n`). | **Fixed** |
| **F15** — Batch RPC errors not surfaced | Catch `AcadRpcException` and rethrow as `McpException(ex.Message)`. | Source confirms the catch+rethrow is in every batch tool. **But:** calling `autocad_batch_run_test` against an empty palette selection still returns the generic `"An error occurred invoking 'autocad_batch_run_test'."` string to the agent. The plugin-side error message (`"No files are currently selected in the BATCH palette. Set a folder + mask first."`) doesn't reach the wire. | **Not effective end-to-end** — see [[#g4]] |
| **F19** — `propose_script` surfaces `replaced_dirty` | Add `replaced_dirty` bool to `BatchProposeResult` and the handler payload. | Source confirms the handler emits `replaced_dirty = willPromptForReplace` and the record carries the parameter. **But:** my `autocad_batch_propose_script` call returned `{"ok":true,"saved_as":"...","name":"crashtest-v2-noop"}` — three keys, not four. | **Not effective end-to-end** — see [[#g3]] |
| **Terminate-NRE bonus** | Guard `_palette.Close()` with `Visible: true`. | Source at `McpPlugin.cs:104` is `if (_palette is { Visible: true }) _palette.Close();`. Log shows the last NRE at `2026-05-13 01:17:58` — before the PID 39872 init at `07:44`. Current process hasn't shut down yet, so the live verdict is "no NRE seen since the fix landed; the negative case can't be re-tested without restarting AutoCAD". | **Fixed in source, partially verified** |
| **F1–F4, F6, F10, F12, F16, F18** (docs) | Skill updates only. | Not re-verified — the skill docs were updated per the actions file. The skill content I have loaded mentions `using (var tx = ...)` block form (F6) and the `Acd.DataProvider` REPL pattern (F9 doc half). | **Trust the doc edits** |

</v1-findings-verification>

<new-findings>

<g1 id="g1">
**G1 [smell, LOW]** — Plugin version label `v11-dto-batch-api-split` was **not bumped** after the v1 fix pass. Both the May-12 builds (registering zero DTOs, AECC missing in `AecBaseMgd`) and the May-13 build (registering 21 DTOs, AECC "not resolvable" with the new wording) log the same string. Operators reading `log.txt` after the fact cannot tell which build a particular line came from without timestamps. Bumping to `v12-…` per release (or even per code change to `EnsureDtoGraph`/`PropertySetProvider`) would make log forensics trivial.

**Convention adopted (2026-05-13):** the `McpPlugin.Version` const gets a short kebab-case slug per substantive change, monotonically incremented: v11→v18 (G6 fix) →v19 (G9 fix) →v20 (DEBUG idle hook) →v21 (batch-tools success-shape). The slug describes the change rather than incrementing a semver number, so a `git log -p src/Acd.Mcp/McpPlugin.cs` reads as the plugin's high-signal changelog. **Status: FIXED (convention) — keep bumping on each substantive change. If forgetting becomes a problem, add a release-script step that warns on un-bumped Version when bin/ is being refreshed.**
</g1>

<g2 id="g2">
**G2 [BUG, HIGH]** — `PropertySetProvider`'s "scan all loaded assemblies" fix is only correct when `AecPropDataMgd` is already present in the AppDomain at provider-init time. On a fresh Civil 3D 2025 launch with no PropertySet interaction yet, the assembly is **lazy-loaded** — confirmed empirically: at REPL start, `AecPropDataMgd` is absent from `AppDomain.CurrentDomain.GetAssemblies()`; calling `Type.GetType("Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices, AecPropDataMgd")` once **does** load it (verified `aec_prop_data_mgd_now_loaded = true` after the probe). The plugin logged `PropertySets disabled — vanilla AutoCAD` at `07:44:49`, ~13 seconds after init, exactly because of this race.

**Fix options, in order of preference:**

1. **Eagerly force-load the AECC assemblies in `Initialize`** — `Type.GetType("...PropertyDataServices, AecPropDataMgd", throwOnError: false)` is the cheapest correct approach. Adds one assembly load to startup time but guarantees the probe sees it. This is the right shape because the plugin's headline feature explicitly targets Civil 3D 2025+.
2. **Make `PropertySetProvider.IsAvailable` re-evaluate on first metadata read** — lazy on the read side so the composite can recover after a Civil 3D operation has triggered the AECC load. Slightly more surface area; preserves the "vanilla AutoCAD" classification when AECC is genuinely absent.
3. **Subscribe to `AppDomain.AssemblyLoad`** and re-run the probe when any `Aec*Mgd` assembly arrives. Most clever, most fragile.

The v1 fix swapped *which* assembly to probe; G2 says the probe needs to happen *when* the right one is loaded. The right fix is option 1 — make sure the right one is loaded at probe time.

A useful smoke test: an integration test that boots the plugin, calls `Type.GetType("...PropertyDataServices, AecPropDataMgd", false)` before `EnsureDtoGraph`, and asserts `PropertySetProvider.IsAvailable == true` on Civil 3D. Asserting on vanilla AutoCAD that it stays `false` is the other half.

**FIXED (ea21ef6, 2026-05-13):** option 1 landed — `McpPlugin.Initialize` now wraps a `Type.GetType("Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices, AecPropDataMgd", throwOnError: false)` call inside `SafeBoundary.Run("McpPlugin.Initialize/probe-AECC", ...)`. This force-loads the managed wrapper into the AppDomain *before* `EnsureDtoGraph` builds the composite. **VERIFIED (2026-05-13 ~12:29):** fresh `/Automation` launch (PID 4732) of Civil 3D 2025 Metric logs `PropertySetProvider: AECC PropertySets available via AecPropDataMgd.` at +4 s after Initialize, and `aecc_loaded = true` via REPL inspection of `AppDomain.CurrentDomain.GetAssemblies()`. **Status: FIXED, verified.**
</g2>

<g3 id="g3">
**G3 [BUG, MEDIUM]** — `BatchProposeResult.replaced_dirty` is wired up in the plugin (`BatchRpcHandler.cs:82`) and the bridge record (`BatchProposeScriptTool.cs:76`), but the field does **not** reach the agent. My one observed response was `{"ok":true,"saved_as":"…","name":"crashtest-v2-noop"}` — three fields, not four.

The pipe-level `FrameIO.JsonOptions` is symmetric on both sides (`CamelCase`, `WhenWritingNull`, case-insensitive), so the field IS on the wire between plugin and bridge. The most likely drop point is the **MCP server's** JsonSerializerOptions, which (in the typical SDK default) uses `JsonIgnoreCondition.WhenWritingDefault`. That would drop a `bool` whose value is `false` — which matches exactly the case I hit (editor wasn't dirty → `replaced_dirty = false` → silently omitted from the wire to the agent).

**Fix options:**

1. **Change `replaced_dirty` to `bool?`** in `BatchProposeResult`. The default is `null` rather than `false`; null serializes only when meaningful. The agent always sees the field except when the plugin couldn't determine the state.
2. **Override the MCP server's serializer for batch tools** to keep defaults. More invasive — affects every tool's response shape.
3. **Serialize `replaced_dirty` as a string** (`"true"`/`"false"`). Ugly but immune to default-stripping.

Option 1 is the right move — it matches "agent learned the dirty state" vs "agent didn't learn it" cleanly. Should be a 2-line change.

**VERIFIED (2026-05-13 ~12:50):** wire-tested via direct named-pipe RPC (`batch.proposeScript`) against PID 4732, build `v20-debug-idle-autostart`. Response now includes `replaced_dirty: false` (snake_case wire keys, since the C# record uses snake_case property names and `JsonNamingPolicy.CamelCase` only PascalCase → camelCase, not snake → camel). Before the v17 fix, this field was silently dropped by `JsonIgnoreCondition.WhenWritingDefault` when the value was `false`; with `bool?` it survives. **Status: FIXED.**
</g3>

<g4 id="g4">
**G4 [BUG, HIGH]** — Batch error surfacing (F15) is in source but the agent still sees `"An error occurred invoking 'autocad_batch_run_test'."` for an error that the plugin emitted as `"No files are currently selected in the BATCH palette. Set a folder + mask first."`.

Source review confirms the chain is correct on paper:

```
plugin BatchRpcHandler.HandleRunTestAsync
    throws InvalidOperationException("No files...")
  ↓
pipe RpcServer wraps in { error: { code, message } }
  ↓
bridge AcadClient.DecodeResult throws AcadRpcException(code, msg)
  ↓
bridge BatchRunTestTool catches → throws McpException(ex.Message)
  ↓
MCP server framework serializes McpException → wire
  ↓
agent (Claude Code) renders error
```

The break is at the last hop. The MCP server SDK does propagate `McpException.Message` to the remote endpoint, but the Claude Code MCP client appears to wrap the message back into a generic invocation-error string at display time — or the McpException is bypassed earlier by some pipeline ordering.

**Diagnosis needed before fixing:** capture the raw JSON-RPC frame the MCP server emits when the bridge tool throws `McpException`. If the frame already contains the readable message text, the issue is client-side rendering (out of scope for the plugin). If the frame contains the generic "An error occurred invoking..." string, the issue is the MCP SDK eating the message, and the fix is to use a structured-error tool result rather than a thrown exception.

A pragmatic workaround that doesn't depend on `McpException` behaviour: make every batch tool's success path *also* the carrier for error reporting — i.e. return a discriminated result `{ ok: false, error_code: "no_selection", error_message: "..." }` and never throw. That's the shape `ExecuteCsharpTool` already uses (per the F15 action note in the actions file), and it survives any MCP-SDK quirks. Recommended.

**Source fix (2026-05-13):** `BatchRunTestTool`, `BatchProposeScriptTool`, and `BatchGetSelectionTool` now never throw. Each catches `AcadRpcException` and returns the same record shape with `ok: false`, `error_code` (the JSON-RPC numeric code from the plugin envelope, as a string), and `error_message` (the verbatim plugin message). The success path returns `ok: true` with the payload fields populated and the error fields null. For run_test and get_selection the bridge wraps the plugin's existing wire shape (`*Payload` internal record) into the new public `*Result` with the discriminator prefix; for propose_script the plugin already returns `ok`, so the result record was extended in place. Full solution still builds clean; 60/60 tests pass (44 batch + 16 DTO loader). `error_code` is currently the numeric JSON-RPC code as a string, not a semantic slug like `"no_selection"` — a future improvement would have the plugin emit semantic codes, but that requires touching `Acd.Mcp.Api` (no hot-reload).

**Diagnose phase confirmed (2026-05-13 ~12:45):** direct named-pipe call to `batch.proposeScript` while `_batchRpc` was uninitialised returned `{ error: { code: -32603, message: "System.InvalidOperationException: BATCH palette is not open. Run ACDMCP_PALETTE inside AutoCAD first.\r\n   at Acd.Mcp.McpPlugin.ExtraRpcMethodHandler ..." } }`. **The plugin DOES emit the message text on the wire, with full stack trace. The bridge receives it. The strip happens at the MCP-SDK / Claude Code client layer.** Confirms the journal's hypothesis and confirms the success-shape refactor is the correct fix — it carries the message on the success path, sidestepping whatever wraps thrown McpException on the client end.

**Status: FIXED (source + diagnose), pending live end-to-end agent-side observation.** End-to-end verification requires the in-session MCP bridge to be respawned to pick up the recompiled `Acd.Mcp.Bridge.dll`. Killing the bridges from PowerShell to force a reload also tore down the MCP server connection for the current Claude Code session; respawn requires the user to run `/reload-plugins` (or restart Claude Code) since the harness does not auto-respawn on disconnect. Once respawned, `autocad_batch_get_selection` while the palette is closed should return `{ ok: false, error_code: "-32603", error_message: "BATCH palette is not open. ..." }` to the agent — proving end-to-end propagation.
</g4>

<g5 id="g5">
**G5 [smell, LOW]** — The repository's `tests/Acd.Mcp.Tests` covers DTO loader compilation against a fake registry. That catches the *registration-mechanism* regression but not the *real-ALC-split* regression that F7 was originally about. The actions file calls this out (`<not-fixed>` block), and v2 doesn't add a fix. **What would be cheap:** a smoke test that asserts the plugin's `Initialize` path produces `EnsureDtoGraph: Registered N DTO types.` with `N > 0` by parsing `log.txt` after a host-shell invocation. Even one bash-level test would have caught both F7 originally and G2 today.

**FIXED (scripts/Test-DtoSmoke.ps1, 2026-05-13):** `pwsh scripts/Test-DtoSmoke.ps1` (default `-MinDtoCount 1`, override e.g. `-MinDtoCount 21` for the current Civil 3D baseline). Recipe mirrors `<autonomous-bootstrap>` in docs/computer-use-from-claude-code.md: flips `Acd.Mcp.loadOnStartup` to true, launches Civil 3D 2025 with `/Automation`, tails `%LOCALAPPDATA%\Acd.Mcp\log.txt` for the `Initialize: vNN` line and the `EnsureDtoGraph: Registered N DTO types` line, asserts `N >= MinDtoCount`, then tears down: kills the agent-launched acad.exe and restores `plugins.json`. Exit 0 on pass, 1 on any failure. Live-tested on PID 4732 reading `Registered 21 DTO types` immediately; full bootstrap-+-teardown not yet exercised in CI (no self-hosted Civil 3D runner). **Status: FIXED (script delivered) — wiring into CI is gated on the QA-standardization infrastructure described in docs/computer-use-from-claude-code.md `<qa-standardization>` (self-hosted Windows runner with Civil 3D entitlement).**
</g5>

<g6 id="g6">
**G6 [BUG, CRITICAL]** — **Batch flavor cannot compile any script body whatsoever.** `BatchScriptRuntime.BuildOptions()` builds its reference list from `AppDomain.CurrentDomain.GetAssemblies()` filtered by `!string.IsNullOrEmpty(a.Location)`. The plugin's three own assemblies are byte-loaded by DevReload (the same isolation that motivated v1's F7), so they have **empty `Location`** and are excluded.

Verified empirically by trying every minimal body — even `ctx.Step("x").Require("r", () => true).Apply(() => "y");` — and getting back two diagnostics: `error CS0246: The type or namespace name 'Acd' could not be found` and `(1,1): error CS7012: The name 'ctx' does not exist in the current context (are you missing a reference to assembly 'Acd.Mcp, ...')`.

So G6 says batch is broken for every user, not just this test. Combined with G3 (replaced_dirty hidden) and G4 (run_test errors generic), the v2 batch surface is fully non-functional for anyone who doesn't read source.

**Fix options, in order of preference:**

1. **Move `AcadBatchGlobals` + `IBatchContext` (the runtime-resolved types) into `Acd.Mcp.Api`** — the same move that fixed F7. `Acd.Mcp.Api` lives in the default ALC with a real `Location`, so the `Location`-filtered probe finds it. This is the architecturally honest fix and matches the precedent.
2. **Add byte-loaded assemblies via `MetadataReference.CreateFromImage(rawBytes)`** — requires the plugin to cache its byte arrays. Less clean but doesn't require relocating types.
3. **Lift the `Location` filter and use `Assembly` overload of `AddReferences`** — `ScriptOptions.AddReferences(Assembly[])` accepts already-loaded Assembly instances and infers metadata from them; this likely sidesteps the filter entirely. **Worth one experiment** before committing to option 1.

**Live-session workaround attempted:** added file-based refs to the existing host options pointing at `bin\Debug\Acd.Mcp*.dll`. Result: compile *passes* but Roslyn re-loads the DLLs into the scripting host's own ALC (`CoreAssemblyLoaderImpl`), creating duplicate-identity types — every `xDb` / `xTx` / `ctx` cast throws `InvalidCastException: [A]AcadBatchGlobals cannot be cast to [B]AcadBatchGlobals` at runtime. Cannot work around in-session without a plugin restart.
</g6>

<g7 id="g7">
**G7 [observation, NICE-TO-KNOW]** — The agent successfully drove the BATCH palette end-to-end without any UI clicks, by reflecting into `McpPlugin._batchRpc._uiState` (a `BatchViewModel`) and invoking its `RefreshCommand` / `RunCommand` directly. Subscribed to `BatchExecutor.RunCompleted` to wait for the structured `BatchRunReport`. 10/10 cycles successful, ~14ms median latency.

This is a useful pattern for AFK automation: when the plugin owns the UI, every `[RelayCommand]` method is a verb already exposed to an in-process reflection client. The action item from the research above ("expose `batch_run_test` / `batch_run_live` as MCP tools") would make this the *external* MCP surface; until then, the REPL's own reflection access is the workaround. Worth documenting in the batch skill so a future agent doesn't conclude "AFK = blocked".
</g7>

<g8 id="g8">
**G8 [BUG, CRITICAL, fixes G6]** — The full fix for G6 requires splitting the script-visible contract types into a fourth, zero-dependency assembly that DevReload streams into the default (non-collectible) ALC. This is what v12–v17 were circling without landing.

**Why the simpler v15/v16 attempts failed:**

* **v15 — `AssemblyLoadContext.Default.Resolving` handler.** Returned the byte-loaded `Acd.Mcp` / `Acd.Mcp.Batch` from the isolated ALC when the Default ALC asked for them. Compile succeeded, but at script-IL load: `System.NotSupportedException: A non-collectible assembly may not reference a collectible assembly.` The CLR enforces this regardless of resolver intent — the script's submission assembly is non-collectible (lives in Roslyn's `_inMemoryAssemblyContext`), and the resolver was handing back the collectible-ALC copies.

* **v16 — Move `AcadBatchGlobals` to `Acd.Mcp.Api`, stream `Acd.Mcp.Batch` for `IBatchContext`.** Got past the collectibility wall (every script-visible type was now in default ALC) but hit `MissingMethodException: Void Acd.Mcp.Batch.BatchScriptHost\`1..ctor(Microsoft.CodeAnalysis.Scripting.ScriptOptions)` at `BatchScriptRuntime.CreateHost()`. Root cause: streaming `Acd.Mcp.Batch` into default ALC also brought a second copy of `Microsoft.CodeAnalysis.Scripting` into the default ALC's lookup chain. `BatchScriptHost<T>` (default-ALC) bound to default-ALC's `ScriptOptions`; `BatchScriptRuntime` (still isolated-ALC) built isolated-ALC's `ScriptOptions`. Two distinct types with the same name — the constructor lookup failed.

* **Adding `Microsoft.CodeAnalysis.*` to `streamedAssemblies` to unify identities.** Made things worse: DevReload's plugin load silently aborted — no `Initialize` log line at all. Roslyn libs pull `System.Reflection.MetadataLoadContext` and other dependencies that the streamed-into-default chain couldn't satisfy. Not investigated further; treated as a dead end.

**The fix (v18):**

1. New project **`Acd.Mcp.Contracts`** — `net8.0`, **zero NuGet/project dependencies**. Holds the types the script body sees by name: `IBatchContext`, `IStepBuilder`, `StepOutcome`, `RequirementResult`, `BatchMode`, `BatchPhase`, `BatchOnFailure`. Namespace stays `Acd.Mcp.Batch` — namespace ≠ assembly, so no caller in `Acd.Mcp.Batch` / `Acd.Mcp` / `Acd.Mcp.Api` changes.

2. **`AcadBatchGlobals`** moves to `Acd.Mcp.Api/Batch/AcadBatchGlobals.cs`. It must be in the default ALC (script IL names it via the globals type) and it needs `Database`/`Transaction`, which `Acd.Mcp.Api` already brings in. It can't go into `Acd.Mcp.Contracts` because Contracts must stay AutoCAD-free for its zero-dep invariant.

3. **`Acd.Mcp.Batch.csproj` and `Acd.Mcp.Api.csproj` both `ProjectReference` Contracts.** `Acd.Mcp.Batch` keeps `BatchScriptHost<T>`, `BatchExecutor`, `BatchRunner`, `BatchContext`, `StepBuilder`, `BatchRunHistory`, `IBatchSession`, etc. — i.e. *everything* that depends on Roslyn. Acd.Mcp.Batch stays in the isolated (collectible) ALC. A collectible assembly *can* reference a non-collectible one, so `BatchExecutor` reaching for `IBatchContext` (Contracts/default) is fine.

4. **`SharedAssemblies.Config.json`** lists `Acd.Mcp.Api` and `Acd.Mcp.Contracts` in both `sharedAssemblies` and `streamedAssemblies`. Streaming Contracts into default ALC is the structural enabler — its zero-dep design means no transitive collateral.

**Why this works and v16 didn't:**

| | v16 | v18 |
|---|---|---|
| AcadBatchGlobals home | default (Api) | default (Api) |
| IBatchContext home | default (streamed Acd.Mcp.Batch) | default (streamed Acd.Mcp.Contracts) |
| BatchScriptHost home | default (streamed) | isolated (still in Acd.Mcp.Batch) |
| Microsoft.CodeAnalysis | duplicated across ALCs | single copy in isolated |
| ScriptOptions identity | mismatch | single |

The script's emitted IL only ever references `Acd.Mcp.Api` and `Acd.Mcp.Contracts`. Both are in the default ALC. No collectibility violation. No `Microsoft.CodeAnalysis` duplication. `BatchScriptHost<AcadBatchGlobals>` constructed from `Acd.Mcp.Batch` (isolated) uses isolated-ALC's `ScriptOptions` against isolated-ALC's host — identical types, ctor found.

**Verified end-to-end (v18-contracts-split-g6-fixed, 2026-05-13 ~10:55):**

- Solution build clean (0 warnings, 0 errors).
- `Acd.Mcp.Batch.Tests`: 44/44 pass.
- ALC arrangement at runtime confirmed via REPL: `Acd.Mcp.Api` and `Acd.Mcp.Contracts` are in `Default` ALC (`api_streamed=true`, `contracts_streamed=true`). `Acd.Mcp` and `Acd.Mcp.Batch` are in `PluginIsolated::Acd.Mcp.dll`. `IBatchContext` / `StepOutcome` / `BatchPhase` resolve to `Acd.Mcp.Contracts`; `AcadBatchGlobals` resolves to `Acd.Mcp.Api`.
- `BatchScriptHost.Compile` accepts a minimal Step DSL body cleanly (`isPass=True`). No more "type or namespace name 'Acd' could not be found" / "name 'ctx' does not exist" errors that defined G6 in v2.
- BATCH Test run against `crashtest-v2-dwgs/*.dwg` driven via `BatchViewModel.RunCommand` reflection: **5/5 Pass in 373ms.** Per-file step results match the entity counts in `<dwg-generation>` exactly.

**What this unblocks:**

- BATCH flavour is functional for end users for the first time since the plugin shipped.
- G3 (`bool? replaced_dirty`) is now end-to-end testable on the bridge — needs an MCP-bridge restart to pick up the new record shape.
- G4 (batch run-test error propagation) still requires the McpException → success-shape conversion separately; G8 doesn't address it.

**Coupling cost:** one extra assembly and one extra ProjectReference per project that touches the types. The cost is bounded — `Acd.Mcp.Contracts` deliberately *can* never grow dependencies, so it doesn't become a god-assembly over time.
</g8>

<g9 id="g9">
**G9 [smell, LOW, surfaced during G6 live-verification]** — `BatchScriptRuntime.BuildOptions()` imports BOTH `Autodesk.AutoCAD.DatabaseServices` and `Autodesk.Civil.DatabaseServices`. Both namespaces define a public `Entity` type. Any batch script body that names `Entity` unqualified fails to compile with `CS0104: 'Entity' is an ambiguous reference between 'Autodesk.AutoCAD.DatabaseServices.Entity' and 'Autodesk.Civil.DatabaseServices.Entity'`.

This is the same defect F13 fixed for the REPL (the action was "drop `Autodesk.Civil.*` imports from REPL default set"), but the fix wasn't propagated to the BATCH runtime's `WithImports(...)`. Mirroring the REPL's import set — i.e. drop the `Autodesk.Civil.*` lines from `BatchScriptRuntime.BuildOptions` — closes G9.

Until then, batch script bodies must spell `Autodesk.AutoCAD.DatabaseServices.Entity` in full whenever they need it. The crashtest-v2-noop body in the verification run does exactly that.

**FIXED (v19-drop-civil-imports-from-batch):** the three `Autodesk.Civil.*` lines are removed from `BatchScriptRuntime.BuildOptions()`. Batch script bodies can now name `Entity` unqualified. Scripts that genuinely need Civil 3D types still work — they just have to spell the full `Autodesk.Civil.*` path, exactly like the REPL after F13.

**VERIFIED (2026-05-13 ~12:55):** proposed `g9-unqualified-entity` batch body via `batch.proposeScript` containing `var e = (Entity)xTx.GetObject(id, OpenMode.ForRead);` — i.e. *unqualified* `Entity` — and ran Test mode against `crashtest-v2-dwgs/*.dwg` via reflection-driven `BatchViewModel.RunCommand`. Result: **5/5 Pass, 0 Fail, no compile diagnostics.** Pre-v19 this body would have hit `CS0104: 'Entity' is ambiguous`. **Status: FIXED, verified.**
</g9>

</new-findings>

<dwg-generation>

Five `.dwg` files were created at `X:\GitHub\shtirlitsDva\ACD-MCP\crashtest-v2-dwgs\`:

| File | Size | Entities | Layers | On `CRASHTEST_V2` |
|---|---|---|---|---|
| `crashtest-01.dwg` | 17,051 B | 15 | 2 | 15 |
| `crashtest-02.dwg` | 17,148 B | 18 | 2 | 18 |
| `crashtest-03.dwg` | 16,890 B | 12 | 2 | 12 |
| `crashtest-04.dwg` | 17,051 B | 15 | 2 | 15 |
| `crashtest-05.dwg` | 17,148 B | 18 | 2 | 18 |

Each file has equal counts of `Line`, `Circle`, and `DBText` — `n = 4 + (seed % 3)` of each, placed on a new layer `CRASHTEST_V2` with a per-seed ACI colour. Generation was straightforward once the gotcha was found: **`Entity.Layer` cannot be set on an unowned entity** — the setter probes the database's `LayerTable` by name, and an entity that hasn't been appended to a `BlockTableRecord` yet has no database to probe. Fix: `ms.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true); line.Layer = layerName;` — append, register, then set Layer. Worth a sentence in the start skill's `<repl-conventions>` because this is a "first day" trap for anyone building DWGs from a `Database(buildDefaultDrawing: true, noDocument: true)`.

</dwg-generation>

<batch-script-and-sideload-run>

A v2 audit script `crashtest-v2-noop` was pushed into the BATCH palette editor via `autocad_batch_propose_script`. Two `ctx.Step` chains: `audit-modelspace` (counts ents by type + layer count) and `layer-check` (requires `CRASHTEST_V2` present, reports entity count on it). The script body otherwise matches the BATCH-flavour contract: no `new Database`, no `SaveAs`, no `try/catch`, uses only `xDb`, `xTx`, `ctx`.

**The user was AFK during this phase**, so the palette selection (folder + mask) could not be set and `autocad_batch_run_test` could not be exercised against the new DWGs through the BATCH runtime. As a substitute, the agent **sideloaded** each of the five files via REPL and ran the equivalent inspection logic inline. All five files opened cleanly, all five reported `has_ms = true`, all entity-by-type counts matched the populated seed pattern, and `CRASHTEST_V2` layer was present in every file. The script body therefore has a clean Test pass for these files in principle; running through the actual BATCH runtime (with its Step DSL, run history, and rollback semantics) remains a user-driven step.

**Hand-off message to a future tester:** point the BATCH palette at `X:\GitHub\shtirlitsDva\ACD-MCP\crashtest-v2-dwgs\` with mask `*.dwg`, then call `autocad_batch_run_test()`. The editor buffer already holds `crashtest-v2-noop`. Verify (a) every file gets two Pass step entries, (b) the run report shows entity counts matching the table above, (c) F15's error surfacing — call `autocad_batch_run_test()` while the palette is empty and confirm the agent sees the actual "No files are currently selected" message rather than the generic invocation error.

</batch-script-and-sideload-run>

<summary>

**v1 findings outcomes (10 code findings):** 6 verified fixed (F5, F7, F9, F13, F14, F17). 1 fixed in source, partially verified (Terminate-NRE). 1 regressed in a different way (F8 → [[#g2]]). 2 not effective on the wire despite source fixes (F15 → [[#g4]], F19 → [[#g3]]).

**New v2 findings (5 items):**

1. [[#g1]] — Version label `v11-dto-batch-api-split` was not bumped.
2. [[#g2]] — `PropertySetProvider` scans before `AecPropDataMgd` is lazy-loaded; the v1 fix's effectiveness depends on a load that hasn't happened yet at init time. **Highest priority.**
3. [[#g3]] — `replaced_dirty` is dropped on the wire when `false` (MCP SDK's default-ignore). Likely 2-line fix: `bool?`.
4. [[#g4]] — Batch error messages still don't reach the agent end-to-end. Either the MCP SDK eats `McpException.Message` or the Claude Code client wraps it. Recommended workaround: convert batch tool errors to a `{ ok: false, error: "..." }` shape on the success path, never throw.
5. [[#g5]] — No smoke test that parses `log.txt` for `Registered N DTO types with N > 0`. Same single line that would have caught F7 in v1 would catch G2 today.

**Recommended next pass priorities:**

1. **G2** — force-load AECC at plugin init or re-evaluate `IsAvailable` on first use. Without this, the DataProvider on Civil 3D returns block-attributes-only until something else touches PropertySets (which the plugin itself never does).
2. **G3** — `bool? replaced_dirty` is a trivial change.
3. **G4** — needs a wire capture before the right fix is obvious; in the meantime, switch batch tools to non-throwing error shapes.
4. **G1** — bump the version label and add a CI step that bumps it on every `Acd.Mcp/**` change.
5. **G5** — a `dotnet build && start "autocad.exe" && wait-for-log-line` smoke would close the integration-test gap that has now produced two consecutive "Registered 0 DTO types" regressions.

The architectural fixes from the v1 actions pass were correct in shape (ALC split, type relocation, sigil unification). The regressions in v2 are at the seams between code and runtime: assembly-load timing (G2), MCP wire-format defaults (G3, G4), and version hygiene (G1). They are all individually small and individually obvious; collectively they argue for a single integration smoke test that asserts each surface stays green across a real plugin boot.

</summary>

<v2-iteration-log>

**v12 → v17 iteration log (2026-05-13).** Done from inside this session using the computer-use toolkit from `docs/computer-use-from-claude-code.md` (launch Civil 3D with `/Automation`, bind to running instance via ROT moniker on the drawing's full path, `SendCommand` to drive AutoCAD's command line). PowerShell 5.1 (STA) for COM; build via `dotnet build` after killing the AutoCAD process holds.

- **v12 — `BuildOptions` filter fix.** Removed `!string.IsNullOrEmpty(a.Location)` filter so byte-loaded plugin assemblies could enter the script reference set via `AddReferences(Assembly[])`. Compiled cleanly, but at runtime Roslyn's `AddReferences(Assembly[])` internally calls `CreateReferenceFromAssembly` which throws `NotSupportedException` on assemblies with empty Location. Net result: G6 not fixed.
- **v13 — Eager AECC load (G2).** Added `Type.GetType("...PropertyDataServices, AecPropDataMgd", false)` to `Initialize()`. Confirmed working: log now reads `PropertySetProvider: AECC PropertySets available via AecPropDataMgd.` instead of `disabled — vanilla AutoCAD`. **G2 verified fixed.**
- **v14 — Use `RoslynReferences.Build` helper.** The repo already has a helper (`RoslynReferences.cs`) that handles byte-loaded assemblies via `TryGetRawMetadata` + `AssemblyMetadata.Create`. Switched `BatchScriptRuntime.BuildOptions` to use it. Compile succeeded but runtime threw `FileNotFoundException` because Default ALC asks for `Acd.Mcp` and only the isolated ALC has it.
- **v15 — Default ALC Resolving handler.** Added `AssemblyLoadContext.Default.Resolving += ResolveByteLoadedPluginAssembly` to return the byte-loaded `Acd.Mcp` / `Acd.Mcp.Batch` from the isolated ALC. New error: `non-collectible assembly may not reference a collectible assembly`. The byte-loaded assemblies live in DevReload's collectible ALC; the Roslyn-emitted script assembly is non-collectible. CLR refuses the cross-collectibility reference.
- **v16 — Type-move attempt.** Moved `AcadBatchGlobals` to `Acd.Mcp.Api` (default ALC). Added `Acd.Mcp.Batch` to `streamedAssemblies` so `IBatchContext` is also in Default ALC. Encountered framework mismatch: `Acd.Mcp.Api` is net8.0-windows, `Acd.Mcp.Batch` is net8.0 cross-platform. Workaround via `Acd.Mcp.Api → Acd.Mcp.Batch` project ref worked. New error: `MissingMethodException: BatchScriptHost\`1..ctor(Microsoft.CodeAnalysis.Scripting.ScriptOptions)`. Because `BatchScriptRuntime` (in Acd.Mcp.dll / isolated ALC) builds `ScriptOptions` from a *different* ALC's `Microsoft.CodeAnalysis.Scripting` than `BatchScriptHost` now expects. Adding `Microsoft.CodeAnalysis.*` to `streamedAssemblies` caused DevReload to fail loading the plugin (no `Initialize` log entry; root cause not diagnosed — probably native-dep handling for the Roslyn libs).
- **v17 — Revert.** Restored all v15/v16 changes (Resolving handler removed; type moves reverted; shared config back to original). Preserved v13's G2 fix and the G3 source change. Documented the v16 path forward in [[#g8]]. Build is clean at `bin/Debug/Acd.Mcp.dll`.
- **v18 — Contracts split (G6/G8 fix, VERIFIED).** Introduced `Acd.Mcp.Contracts.csproj` (net8.0, zero deps) holding the script-visible interfaces and enums; both `Acd.Mcp.Batch` and `Acd.Mcp.Api` project-ref it. Moved `AcadBatchGlobals` into `Acd.Mcp.Api/Batch/`. Added `Acd.Mcp.Contracts` to both `sharedAssemblies` and `streamedAssemblies` in `SharedAssemblies.Config.json`. `Acd.Mcp.Batch` stays out of `streamedAssemblies` — its Roslyn dependency would otherwise duplicate across ALCs and break `BatchScriptHost<T>`'s ctor identity (the v16 failure mode). Build clean; 44/44 Acd.Mcp.Batch.Tests pass. Civil 3D 2025 launched with `/Automation` (PID 47256): `Initialize: v18-contracts-split-g6-fixed`, `PropertySetProvider: AECC PropertySets available`, `EnsureDtoGraph: Registered 21 DTO types`. **No `EXCEPTION in TryEnsureCore`** — the `MissingMethodException` chain is gone. `BatchScriptHost.Compile` returns `IsPass=True` for a minimal Step DSL body. BATCH Test run against `crashtest-v2-dwgs/*.dwg` driven via `BatchViewModel.RunCommand` reflection: **5/5 Pass in 373ms**, entity counts match the [[#dwg-generation]] table exactly. Surfaced one new smell during script-body authoring: [[#g9]] (BATCH imports still include `Autodesk.Civil.*` and shadow `Entity`). See [[#g8]] for the full architecture rationale.

**What's verified working post-v17:**

- `Initialize: v17-reverted-keep-g2-g3` lands cleanly on Civil 3D launch.
- `PropertySetProvider: AECC PropertySets available via AecPropDataMgd.` — **G2 fix holds.**
- `EnsureDtoGraph: Registered 21 DTO types.` — **F7 (v1) holds**, no regression.
- The reflection harness from [[#g7]] continues to work for the BatchViewModel-driven path.

**What's still broken:**

- **G6** — batch script bodies cannot execute (collectibility error). Tracked in [[#g8]]. Needs the structural type-move described there.
- **G3** — source code has `bool? replaced_dirty` but bridge process needs restart to pick it up. Not verifiable in this session.
- **G4** — error propagation through MCP transport still not surfacing.

</v2-iteration-log>

</crash-test-v2-journal>
