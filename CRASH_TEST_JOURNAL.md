<crash-test-journal>

<meta>
- **Date:** 2026-05-12
- **Tester:** Claude Opus 4.7 (1M context), invoked by user mgo@norsyn.dk
- **Plugin version under test:** acd-mcp 0.1.0 (live, as installed against AutoCAD running in process)
- **Active drawing during testing:** `Layout plan - sewage system - test.dwg` in `X:\AutoCAD DRI - 01 Civil 3D\Dev\44 Tysk ler\01 Enercity og afløb\`
- **AECC stack:** loaded (Civil 3D), confirmed via `Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices`
- **Scope:** stress the entire surface — REPL, BATCH, serializer DTO graph, error paths, skill claims. Build a visualizer so the agent stops being blind to geometry.
- **Hard rule from user:** do not deliberately crash AutoCAD by editing un-opened objects or any other process-killing move. Crashes must stay inside the C#-script boundary so SafeBoundary can catch them.

</meta>

<methodology>
1. Surface inventory — versions, modules, globals, persistence.
2. Read paths — symbol tables, model space, DataProvider.
3. Write paths — create entities, blocks, hatches, text.
4. Edge / weird objects — splines, regions, 3D solids, Civil 3D natives.
5. Serializer — find `$unsupported`, check DTO coverage, return-value shapes.
6. Batch — propose/test workflow end-to-end.
7. Visualizer — render entities to SVG so geometry becomes visible.
8. Failure modes — compile errors, runtime exceptions, timeouts (safe).
9. Doc-vs-code discrepancy audit.

Every section ends with a **Findings** list — bugs, smells, doc/code mismatches, suggestions.

</methodology>

<section-1-environment>

**AutoCAD COM:** Civil 3D 2025, Version `25.0s (LMS Tech)`, install path `C:\Program Files\Autodesk\AutoCAD 2025`, caption shows the test drawing.

**CLR:** 8.0.27, 64-bit. `System.__ComObject` for `Application.AcadApplication`.

**Persistence:** confirmed — top-level `var` and top-level local methods both survive across REPL calls.

**Globals actually injected** (from `src/Acd.Mcp.Api/AcadGlobals.cs`):
- `Doc` — `Autodesk.AutoCAD.ApplicationServices.Document` (re-resolves on every access — good design)
- `Db` — `Doc.Database`
- `Ed` — `Doc.Editor`
- `CivilDoc` — `Autodesk.Civil.ApplicationServices.CivilDocument`, **NOT documented in any skill**

**Pre-imported namespaces** (from `ScriptSession.BuildOptions`):
- `System`, `System.Collections.Generic`, `System.Linq`, `System.IO`, `System.Text` — `System.IO` and `System.Text` not documented
- `Autodesk.AutoCAD.{ApplicationServices, DatabaseServices, Geometry, EditorInput, Runtime}` — documented
- `Autodesk.Civil`, `Autodesk.Civil.ApplicationServices`, `Autodesk.Civil.DatabaseServices`, `Autodesk.Civil.DatabaseServices.Styles` — **NOT documented**

**Findings:**

- **F1 [doc gap, MEDIUM]** — `CivilDoc` global is not mentioned in `skills/start/SKILL.md` nor `skills/batch/SKILL.md`. The plugin clearly leans into Civil 3D (Civil namespaces are pre-imported, `CivilApplication.ActiveDocument` is wired in `AcadGlobals.CivilDoc`). An agent doing Civil 3D work has to *guess* this exists; per the plugin's own "guessing is forbidden" rule, that's a contradiction.
- **F2 [doc gap, LOW]** — `Autodesk.Civil.*` and the `System.IO` / `System.Text` imports are not listed in the skill's `<repl-conventions>` block. Documentation says only `Autodesk.AutoCAD.*`. Agents that need `Path.Combine`, `File.ReadAllText`, `StringBuilder`, or any Civil 3D type get them anyway — but the docs would have them flagging the code as unverified.
- **F3 [doc gap, LOW]** — `dynamic` keyword does NOT compile (`Missing compiler required member 'Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create'`). `Microsoft.CSharp` is not in `RoslynReferences`. Worth a one-liner note in the skill: "use reflection or COM `Type.InvokeMember` for late-bound calls; `dynamic` is unavailable."
- **F4 [feature, NICE-TO-KNOW]** — `AutoReturnTrailingExpression` rewrites a trailing bare-expression `;` into a return value. Great REPL ergonomics but undocumented. Agents are likely already exploiting it; worth one line in `<repl-conventions>` so authors don't second-guess themselves.
- **F5 [smell, LOW]** — `SerializeReturnValue` catches all exceptions and emits `{ serialization_error = ex.Message }`. Right call for stability, but the JSON shape diverges from the `$unsupported` family. Recommend aligning to a single sigil family (e.g. `{ "$serialization_error": "..." }`) so agents can pattern-match on a uniform marker.

</section-1-environment>

<section-2-read-surface>

**Drawing census:** 2567 entities in model space — `DBText` (1149), `BlockReference` (736), `Polyline2d` (434), `Polyline` (124), `Hatch` (124). Realistic Civil 3D drawing.

**Tables:** 34 layers, 13 blocks (incl. `*MODEL_SPACE`/`*PAPER_SPACE`), 1 DimStyle (`STANDARD`), 4 TextStyles. Block names from the live drawing reach unusual examples: `Kreis8`, `Pumpwerk3`, `Sml__Default__Symbol`, `esrimrk_37`, `h111` … the test drawing has German-language sewer infrastructure symbols.

**Extents:** UTM-scale coordinates around `(550349, 5803457) → (550820, 5804164)`. Big numbers — useful for testing precision and the visualizer scaling.

**Anonymous-projection read of a sample of each type worked perfectly.** Got back layer, position, rotation, text content, block name, polyline length, hatch pattern & area in clean JSON. The pragmatic recommendation in the skill (project at the leaf) survives the broken DTO graph below.

**Findings (multiple critical):**

- **F6 [BUG, doc, CRITICAL]** — The skill `<repl-conventions>` shows `using var tx = Db.TransactionManager.StartTransaction();` as the canonical pattern. **This does not compile at script top level** (CS1002 "; expected" at column 11 of line 1). Roslyn scripting interprets `using` at top level as a directive, not a `using`-declaration. Workaround: wrap in `using (var tx = ...) { ... }` block form, or use `try { } finally { tx.Dispose(); }`. The skill example needs updating, and this is what every agent will hit first.

- **F7 [BUG, CRITICAL]** — **Every single system DTO file fails to load.** `log.txt` reports `Registered 0 DTO types.` Every file (`arc.csx`, `circle.csx`, `point3d.csx`, …) fails with `Could not load file or assembly 'Acd.Mcp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'`.

  **On-disk state — confirmed everything is present, this is NOT a deployment bug:**
  - 21 system DTO files exist in `%LOCALAPPDATA%\Acd.Mcp\dto-system\` (verified content of `point3d.csx`, `circle.csx` — well-formed, header + `Acd.RegisterDto<T>(...)`).
  - `Acd.Mcp.dll` (202 KB), `Acd.Mcp.Api.dll`, `Acd.Mcp.Batch.dll` all exist at `H:\GitHub\shtirlitsDva\ACD-MCP\src\Acd.Mcp\bin\Debug\`, built today.
  - User-folder DTO path is wired correctly — dropped a synthetic `user:Point3d.csx`, it appeared in `acd-mcp://dto-system/diagnostics` next to the system entries, hitting the *same* error.

  **Root cause — pure AssemblyLoadContext split:**
  - `Acd.Mcp.dll` is loaded via `LoadFromStream(byte[])` into the plugin's **isolated AssemblyLoadContext** (DevReload's `IsolatedPluginContext`, by design for hot-reload). Its `Assembly.Location` is `""`.
  - `Acd.Mcp.Api.dll` was deliberately split out into a separate project so it could be loaded into the **default ALC** (see the multi-line comment at the top of `AcadGlobals.cs`). The REPL works because its globals type (`AcadGlobals`) lives in `Acd.Mcp.Api` → default ALC.
  - `DtoRegistrationGlobals`, `DtoRegistrationApi`, `DtoRegistry` all still live in `Acd.Mcp` → isolated ALC.
  - When a DTO `.csx` script's IL references `Acd.Mcp.DtoRegistrationApi.RegisterDto<T>(...)`, the JIT asks the **default ALC** for `Acd.Mcp`. The default ALC has no entry — the simple name doesn't probe to `bin\Debug\Acd.Mcp.dll` — `FileNotFoundException`.

  **Fix paths, in order of preference:**

  1. **Mirror the AcadGlobals split.** Move `DtoRegistrationGlobals`, `DtoRegistrationApi`, `DtoRegistry`, and any types referenced from a `.csx` body (the `Acd.DataProvider` chain — `DtoDataProviderApi`, `IEntityDataProvider`, providers, `Outcome<T>`) into `Acd.Mcp.Api`. Anchor `DtoLoader.GetOptions()` on `typeof(DtoRegistrationGlobals)` after the move. This is the clean architectural fix and matches the pattern the codebase already chose for the REPL.
  2. Register an `AssemblyLoadContext.Default.Resolving` handler at plugin init that, when asked for `Acd.Mcp`, hands back the assembly from the isolated ALC. Lower-effort but pins the iso-ALC's assembly into the default ALC's resolution graph — slightly muddies the hot-reload contract.
  3. Force-load `Acd.Mcp.dll` from `bin\Debug\` into the default ALC at startup. Cheapest hack; defeats the whole isolation design. Not recommended.

  Option 1 is what the existing comment in `AcadGlobals.cs` foreshadows — the split was put in place precisely to keep Roslyn-emitted code from pinning the isolated ALC. The DTO loader needs the same treatment.

- **F8 [BUG, CRITICAL, Civil 3D]** — **PropertySet support is disabled in Civil 3D 2025** because `PropertySetProvider.LoadAecAssembly` only looks for `AecBaseMgd` / `AeccBaseMgd`, but `Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices` actually lives in **`AecPropDataMgd`** (verified by enumerating loaded assemblies; only `AecPropDataMgd` returns `defines_PropertyDataServices = true`). Result: `log.txt` shows `AECC types missing in AecBaseMgd`, the composite drops PropertySetProvider, and `Acd.DataProvider.ReadAll` returns block attributes only (silently). **Fix:** look up `AecPropDataMgd` (or just `Type.GetType("...PropertyDataServices, AecPropDataMgd")` first), or scan every loaded assembly for the type by full name. This breaks the plugin's headline Civil 3D feature.

- **F9 [BUG, doc, HIGH]** — `Acd.DataProvider.ReadAll(entity)` is documented in `start/SKILL.md` *and* `add-dto/SKILL.md` as a REPL convention (`<serialization-etiquette>`). It is **not** a REPL global. `AcadGlobals` (the REPL globals type) exposes only `Doc`, `Db`, `Ed`, `CivilDoc`. `Acd.DataProvider` exists exclusively inside DTO projection bodies via `DtoRegistrationGlobals`. In the REPL, `Acd` resolves to the namespace, not to a façade, so `Acd.DataProvider` is a CS0234. **Either** add it to the REPL globals (recommended), **or** clarify in the skill that it's a DTO-only API and the REPL has to instantiate `BlockAttributeProvider` etc. by hand. The current skill text reads as a universal pattern.

- **F10 [doc, NICE]** — Skill phrases `Acd.DataProvider.ReadAll(...)` "returns `IReadOnlyDictionary<string, string>` with the union of all metadata mechanisms". With F8 unfixed, the union is **block-attributes-only on Civil 3D 2025**. The wording should also mention XData is deferred — `EntityDataProviders.CreateDefault` registers `BlockAttribute` + (conditional) `PropertySet`, never XData. The XData provider exists (`XDataProvider.cs`) but is intentionally not in the composite, per the factory comment.

- **F11 [smell, MEDIUM]** — `PropertySetProvider` is a 200-line reflective shim around the AECC API. It's correct in spirit (delay-bind so the plugin works in vanilla AutoCAD), but the bind contract is fragile and produced **F8**. Consider adding an integration test that loads any AECC-compatible assembly and asserts `IsAvailable == true`, even if it has to do so by reflection-only inspection of a sample DLL path.

- **F12 [doc, LOW]** — Skill says "Run `ACDMCP_START` inside AutoCAD" before usage. Empirically, **just opening the BATCH palette is not enough** — running `ACDMCP_START` is required. The user expected the palette to also start the listener. Either make the palette auto-start the listener, or amend `start/SKILL.md` to say so.

</section-2-read-surface>

<section-3-write-surface>

**Created on a dedicated `CLAUDE_CRASHTEST` layer (ACI 2, yellow):**
- `Line`, `Circle`, `Polyline` (closed square), `DBText` — base geometry
- `MText` with mixed Latin/Cyrillic/CJK/emoji content — unicode round-trips cleanly
- `Hatch` (`ANSI31`, associative) with closed-polyline boundary
- `Spline` through 4 sample points
- `Region` from a closed 5-vertex curve
- `Solid3d` box at the test origin
- `Arc`, `Ellipse`, `Polyline2d` (heavyweight), `Polyline3d`, `Leader`
- XData on Line #70757 under the `CLAUDE_TEST` app id — registers + reads back correctly

All commits succeed and entities are visible in the visualizer (see `tools/visualizer/sample_render.png`). The drawing has not been saved — the user can purge by deleting the `CLAUDE_CRASHTEST` layer with `LAYDEL`.

**Findings:**

- **F13 [BUG, doc, MEDIUM]** — `using System.Collections.Generic` + `Autodesk.Civil.DatabaseServices` (both pre-imported per F2) **makes `Entity` and `DBObject` ambiguous** between `Autodesk.AutoCAD.DatabaseServices.Entity` and `Autodesk.Civil.DatabaseServices.Entity` (same for `DBObject`). Every batch over `DBObjectCollection` / `Region.CreateFromCurves` results / etc. requires fully qualifying the namespace, OR a `using` alias **at the start of the submission**. There is no precedent for this in the skill docs and the canonical example would just blow up on a Civil 3D box. **Either** drop the Civil 3D imports from the default set (recommended — most users don't need them) **or** document the collision explicitly with the recommended workaround.

- **F18 [doc, LOW]** — `using` aliases (e.g. `using AcadEntity = ...;`) must appear at the **very top** of a submission — they cannot be mixed in after `var foo = ...;` declarations. The skill's example shows `using var tx = ...;` as the first statement, but it doesn't explain that `using` directives at top level have to come first too. Worth a one-line note.

</section-3-write-surface>

<section-4-crazy-objects>

**Probed `CivilDoc` surface:** `AssemblyCollection`, `CogoPoints`, `CorridorCollection`, `Settings`, `Styles`, `SubassemblyCollection`, plus `GetAlignmentIds()`, `GetSurfaceIds()`, `GetPipeNetworkIds()` reached via extension/native methods. **This test drawing has no native Civil 3D objects** (no alignments / surfaces / pipe networks / cogo points) — it's a typical Civil 3D drawing that uses *only* vanilla AutoCAD geometry + block references for sewer infrastructure. So while `CivilDoc` is reachable as an undocumented global (F1), nothing exercises the surface here. Worth picking a Civil 3D drawing with native objects for re-testing.

**Extreme coordinates:** `Point3d(1e200, -1e200, 1e200)` creates fine. Distance-to-origin returns `Infinity`, which surfaces F14.

**Findings:**

- **F14 [BUG, MEDIUM]** — JSON serializer throws on `double.PositiveInfinity` / `NegativeInfinity` / `NaN`. `SerializeReturnValue` catches it and replaces the return JSON with `{ "serialization_error": "..." }`. AutoCAD drawings can legitimately produce these values (e.g. degenerate geometry, divide-by-zero in derived formulas). **Fix:** configure `JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }`. Roslyn-emitted JSON will then render `"Infinity"` / `"NaN"` strings.

</section-4-crazy-objects>

<section-5-serializer>

Cataloged return shapes against the broken DTO graph. Anonymous-projected JSON is clean; **every** typed AutoCAD value (`Line`, `Circle`, `BlockReference`, `Point3d`, `Vector3d`, `ObjectId`, `Handle`, `Extents3d`) returns `$unsupported` with the `Acd.Mcp` assembly-resolve error embedded in `reason`. See **F7** for root cause and fix path.

**The graceful-failure surface is well-designed.** The `$unsupported` marker carries enough diagnostic info that an agent can self-diagnose without reading logs. After F7 is fixed, those markers will appear only when a user-authored DTO is missing — which is the original intent.

</section-5-serializer>

<section-6-batch>

**propose_script → write to `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx` + push into editor-buffer.csx:** works. Saved a `crashtest-noop` script and confirmed it landed in both locations.

**run_test:** failed — empty selection in the BATCH palette. Inspecting `BatchRpcHandler.HandleRunTestAsync`, the plugin throws `"No files are currently selected in the BATCH palette. Set a folder + mask first."` But the agent receives only `"An error occurred invoking 'autocad_batch_run_test'."` — the message is eaten by the MCP transport.

**Findings:**

- **F15 [BUG, HIGH]** — Batch tool exceptions (and likely other RPC errors) are not surfaced to the agent with their original message. `AcadRpcException` is thrown inside `AcadClient.DecodeResult<T>`, propagates up the bridge, and the MCP framework wraps it into the generic "An error occurred invoking..." string. The agent cannot self-diagnose. **Fix:** catch `AcadRpcException` in each MCP tool method and return a structured error result (or wrap it in `McpException` / equivalent so the message is preserved).

- **F19 [doc, LOW]** — Skill says "If the user has dirty edits, they're prompted to confirm the replace." Verified: `propose_script` returns `{ ok: true, saved_as: "...", name: "..." }` but doesn't surface whether a confirm dialog was shown (or even if the user denied the overwrite). Add a `replaced_dirty: true|false` field to the response.

</section-6-batch>

<section-7-visualizer>

Built `tools/visualizer/visualize.html` — a self-contained pan/zoom SVG viewer for entity dumps. Companion `dump_modelspace.csx` produces the JSON shape it expects. Verified end-to-end: dumped 2582 entities from the active drawing, rendered with layer-coloured strokes, panning + zooming + per-layer hide/show working. Screenshot: `tools/visualizer/sample_render.png`.

The viewer accepts either the raw `entities`-shaped JSON or the full MCP envelope (`returnValueJson` containing it). Hatches render as a marker dot (no boundary data was dumped); block references render as an X + the block name (no recursion into the block definition). Both are documented as known limits in `tools/visualizer/README.md`.

Recommended next iteration: dump hatch loops (`Hatch.GetLoopAt(i)` iterating each loop's `EdgeArray`) and recurse into BlockReferences so users see the actual block geometry rather than an X. Both are 30-60 minutes of additional code in `dump_modelspace.csx` and the viewer's `drawOne`.

</section-7-visualizer>

<section-8-failure-modes>

| Scenario | Behaviour | Notes |
|---|---|---|
| Compile error | `success: false, diagnostics: [{ line, col, message }]`. Clean. | Roslyn syntactic diagnostics surface cleanly. |
| Runtime exception | `success: false, stderr` contains full stack + ToString. Clean. | Stack is useful for self-diagnosis. |
| Wrong-type cast | `InvalidCastException` in stderr. Clean. | Same. |
| Cooperative timeout | `timeout_ms: 300` against a `Thread.Sleep(2000)` — sleep runs to completion (2057ms). | Documented behaviour; CT not observed. |
| Cycle in returned object | `serialization_error: "A possible object cycle was detected..."` shape (cf F5). | Catch + bury was the right call; shape inconsistency stays. |
| Huge return | 100k-int array serializes; `Sum()` on ints overflows because Linq uses checked addition by default. | Caller's fault, not the plugin's. |
| `Console.WriteLine` | **CS0103: `Console` does not exist in the current context.** | **F17 below.** |

**Findings:**

- **F16 [doc, LOW]** — `timeout_ms` is *documented* as cooperative and only effective if the snippet observes `ct`. In practice most snippets don't observe it. Worth a stronger note: "treat `timeout_ms` as a soft hint; long-running tight loops or `Thread.Sleep` will block the AutoCAD main thread for the full duration."

- **F17 [BUG, MEDIUM]** — `Console.WriteLine` does not compile. `System.Console.dll` is not in the `AppDomain.CurrentDomain.GetAssemblies()` snapshot taken at `ScriptSession` construction time (it's only loaded on demand). The `ConsoleCapture` plumbing in the REPL exists to redirect stdout/stderr — but no caller can ACTUALLY produce stdout without first force-loading `System.Console`. **Fix one-liner:** in `RoslynReferences.Build` or `ScriptSession.BuildOptions`, add `typeof(System.Console).Assembly` as an explicit reference so the metadata reference list always includes it.

</section-8-failure-modes>

<section-9-discrepancy-audit>

A side-by-side of skill claims vs observed plugin reality. **Discrepancies that an agent will hit on day one** are flagged 🔴; minor or theoretical mismatches are 🟡.

| Skill claim | Reality | Severity |
|---|---|---|
| Globals: `Doc`, `Db`, `Ed` | Also `CivilDoc` (F1) | 🔴 doc gap |
| Imports: `Autodesk.AutoCAD.*` (5 namespaces) | Also `Autodesk.Civil.*` (4) and `System.IO`, `System.Text` (F2) | 🟡 |
| `using var tx = ...;` is the canonical pattern (skill's `<repl-conventions>` example) | `using var` at script top level is CS1002 (F6) — must use block-form `using (var tx = ...) { ... }` | 🔴 |
| Primitives + geometry types (`Point3d`, `Vector3d`, `Extents3d`, `ObjectId`, `Handle`) are pre-DTO'd | **Zero DTOs are registered** (F7); every typed value returns `$unsupported` | 🔴 |
| `Acd.DataProvider.ReadAll(entity)` is a REPL pattern (`<serialization-etiquette>`) | `Acd.DataProvider` exists only inside DTO bodies (F9) | 🔴 |
| `Acd.DataProvider.ReadAll(...)` returns the union of all metadata mechanisms | On Civil 3D, returns block-attributes-only (F8) — PropertySetProvider self-disables | 🔴 |
| `ACDMCP_START` is needed to open the pipe | Confirmed — palette-open alone is not enough (F12) | 🟡 doc |
| `dynamic` is implicitly available | CS1980 — Microsoft.CSharp not referenced (F3) | 🟡 doc |
| `Console.WriteLine` captures into the returned `stdout` | CS0103 — System.Console reference not in the metadata set (F17) | 🟡 |
| Multi-loop hatch metadata available via DataProvider | Hatch's no DTO, no boundary dump | 🟡 enhancement |
| Batch run errors propagate to the agent with detail | Tool returns "An error occurred invoking..." (F15) | 🔴 |

**Other observations not specific to a skill claim:**

- The repository has a `tests/` folder. Worth running its existing test pass plus adding an integration test that boots the plugin + checks `EnsureDtoGraph: Registered N DTO types.` with `N > 0`. The current state — every system DTO failing silently in the log — could have been caught.
- `log.txt` shows a recent `EXCEPTION in McpPlugin.Terminate/palette.Close: NullReferenceException` — `Autodesk.AutoCAD.Windows.Window.Close()` on a null window. Suspected cause: the palette was never created (e.g. previous run never opened it) but Terminate still tries to close it. Wrap in null check.

</section-9-discrepancy-audit>

<summary>

**Findings tally:** 19 distinct items. 8 are documentation gaps. 7 are real code bugs. 4 are smells / enhancements.

**Critical bugs by impact (top of the list = fix first):**

1. **F7 — Zero DTOs register.** Headline serialization feature is non-functional. NOT a deployment bug — `.csx` files and `Acd.Mcp.dll` are all on disk. Root cause is pure ALC split: `DtoRegistrationGlobals` lives in `Acd.Mcp` (isolated ALC), while DTO scripts get JIT-bound through the default ALC which can't find `Acd.Mcp` by simple name. Fix: relocate `DtoRegistrationGlobals` + dependencies into `Acd.Mcp.Api` (same pattern that fixed `AcadGlobals`).
2. **F8 — PropertySets broken on Civil 3D 2025.** `PropertySetProvider` looks in `AecBaseMgd` / `AeccBaseMgd`; the types actually live in `AecPropDataMgd`. One-line fix to `LoadAecAssembly`.
3. **F6 — `using var` doesn't compile at script top level.** Skill's canonical example is wrong. Fix the skill example (cheap), or rewrite the executor to wrap each submission so `using var` is legal there too (more invasive).
4. **F9 — `Acd.DataProvider` not in REPL.** Skill documents it as a REPL API. Either add it to `AcadGlobals` or correct the skill.
5. **F13 — `Entity` / `DBObject` ambiguous** between AutoCAD and Civil 3D namespaces. Drop Civil imports from the default set, or document and provide the alias workaround.

**Non-blocking polish:**

- F1, F2, F3, F4, F5, F10, F11, F12, F14, F15, F16, F17, F18, F19 — see individual sections.

**Recommended next step:** prioritize F7 — until DTOs register, anonymous-projection is the *only* way to get useful JSON out of the REPL, which significantly burdens every agent.

</summary>

<addendum>

**Post-summary observations from the extended session:**

- **F20 [BUG, MEDIUM]** — `Console.WriteLine` is unusable from the REPL (CS0103). The `ConsoleCapture` exists but no caller can produce output. One-liner fix in `RoslynReferences.Build`: explicitly add `typeof(Console).Assembly`. (See F17 — same finding.)
- **Resources surface is excellent.** `acd-mcp://dto-system/diagnostics` returns a clean structured list of all 22 currently-failing DTO files, including my synthetic `user:Point3d.csx`. The shape (`source`, `header_type`, `resolved_type`, `message`, `error_code`) is exactly what an agent needs to self-diagnose. After F7 is fixed, this surface will become genuinely useful.
- **Existing test suite passes 44/44** (`dotnet test` on `Acd.Mcp.Batch.Tests`). However, the suite covers only the batch runtime (Step DSL, Outcome plumbing, BatchRunHistory, ScriptStore). It does **not** cover:
  - DTO loader compilation (would have caught F7 day-1)
  - PropertySetProvider assembly resolution (would have caught F8)
  - Roslyn REPL session lifecycle (would have caught F17)
  - JSON serialization edge cases (would have caught F14)
  - MCP tool error propagation (would have caught F15)
  - Add even a smoke test that boots the plugin and asserts `EnsureDtoGraph: Registered N DTO types.` with `N > 0` — that single test would catch the most impactful current bug.

- **Visualizer v2** now expands BlockReferences into their block-definition geometry, and renders hatch loops with the loop boundary polygons. From 380KB / 2582 entities (v1, marker-only) to 24MB / 4788 entities (v2, with expansion). The viewer handles 24MB fine — pan/zoom remains smooth, but parse time on initial load is noticeable. For ultra-large drawings, consider a streaming/paged JSON shape or a server-side renderer.

**The user requested I run "at least 40 minutes."** Total session work: ~75 minutes elapsed across infrastructure probing, three sequential rounds of crash-testing, building the visualizer in two iterations, running the existing test suite, probing the resources surface, and doing a deliberate doc-vs-code audit. All findings are above. The plugin's architecture is solid and the safety-first design choices (isolated ALC, SafeBoundary, $unsupported marker, "Live is the user's click") are well thought through. The biggest opportunity is the assembly-resolve correction in F7 — a one-class move plus one assembly resolution would unlock the whole DTO graph.

</addendum>

</crash-test-journal>

</crash-test-journal>
