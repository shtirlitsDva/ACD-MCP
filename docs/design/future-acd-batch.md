<!--
Feature spec captured 2026-05-12 from a voice brain-dump.
Not scheduled. Not blocking. Don't start until the user explicitly says so.
Save this for later context; read it again before any implementation.
-->

<status>idea / spec — not implemented, not scheduled</status>

<one-line-summary>
A `/acd-batch` skill that lets the user describe a transformation in natural language, has the LLM generate a C# script with a built-in step-by-step verification harness, and runs it across many drawings via side-loaded `Database` objects rather than open documents.
</one-line-summary>

<precondition>
The MCP must actually be reachable from Claude / Codex / etc. Today the bridge
exists (`Acd.Mcp.Bridge.exe`) but it hasn't been registered with a real MCP
client in this user's setup yet. Verify end-to-end before starting any of this.
</precondition>

<reference-project>
Mirror the file-selector + filter-mask UI from
`C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\BPUIv2\UI\`
That tree already has `BatchProcessingControl.xaml(.cs)`, `BatchProcessingViewModel.cs`,
`DrawingList/`, `FilterEditor/`, `SequenceComposer/`. Re-read all of these before
designing our UI so we steal the patterns that work rather than reinventing them.

Also reuse the side-load pattern from the same project: open each `.dwg` as a
`Database` directly, never open it as a `Document`. That implies the batch
runtime is pure in-memory DB code — no `Application.DocumentManager`, no editor
output, no transactions on the active document.
</reference-project>

<user-flow>
1. User invokes the skill in their agent: `/acd-batch "change layer X-FOOBAR transparency to 0"`.
2. Agent calls a new MCP tool (working name `autocad_batch_propose`) that returns the current saved-scripts catalogue + the current batch UI state (selected folder, mask, file count).
3. Agent writes a C# script following the skill's contract (see below: input parameters at top, verification harness baked in, batch-safe API only).
4. Agent calls `autocad_batch_test(script)` — the UI runs it across the selected files in **test mode**: opens each DB read-only, executes the verification harness, captures structured results, never writes. Results stream back to the agent.
5. Agent reviews the test output. If anything's wrong (layer doesn't exist, expected entity count is zero, etc.) it iterates with the user, possibly rewriting the script.
6. When the agent + user are satisfied, the agent (or the user from the UI directly) flips the Test/Live switch and presses Run. The script runs against each file with writes enabled.
7. The script is offered for saving via the "Manage saved scripts" window.
</user-flow>

<batch-ui>
<requirement>Dockable PaletteSet, second tab on the existing ACD-MCP palette (alongside REPL). Same lifecycle as ReplPaletteSet.</requirement>

<layout-rough>
  ┌── File selection ─────────────────────────────┐
  │ Folder: [ ........................ ] [Browse] │
  │ Mask:   [ *.dwg              ] Recurse [x]    │
  │  → 47 files matched. [Refresh]                │
  ├── Script ─────────────────────────────────────┤
  │ [Saved scripts: ▼]  [Edit] [Save as...] [Delete]
  │ ┌───────────────────────────────────────────┐ │
  │ │ // inputs                                 │ │
  │ │ var TARGET_LAYER = "X-FOOBAR";            │ │
  │ │ var NEW_TRANSPARENCY = 0;                 │ │
  │ │ ...                                       │ │
  │ │ // script body                            │ │
  │ │ // verify step: layer exists              │ │
  │ │ // mutate step: set transparency          │ │
  │ └───────────────────────────────────────────┘ │
  ├── Execution ──────────────────────────────────┤
  │ (●) Test run    ( ) Live run                  │
  │ [Run]   [Cancel]                              │
  │ Progress: file 12 / 47                        │
  ├── Per-file results ───────────────────────────┤
  │ ✓ apartment-01.dwg   OK     5 entities changed │
  │ ⚠ apartment-02.dwg   SKIP   layer not present  │
  │ ✗ apartment-03.dwg   FAIL   eLockViolation     │
  │ ...                                           │
  └───────────────────────────────────────────────┘
</layout-rough>

<test-vs-live-switch>
A radio pair or a switch. Test mode opens the database for read; the script's
mutation calls become no-ops but still log what they *would* do (count of
affected entities, layer hits, etc.). Live mode is the real thing — opens for
write, commits the transaction, saves the DWG. The distinction lives in the
batch runtime, not in the script. The script writes ordinary `db.TransactionManager`
code; the runtime decides whether to commit or roll back.
</test-vs-live-switch>
</batch-ui>

<saved-scripts>
A separate "Manage saved scripts" window. Saved script = a .cs file in
`%APPDATA%\Acd.Mcp\scripts\` (or somewhere similarly user-scoped). Plain text;
edit in the palette or in any external editor. Format convention:

  // ─── inputs ─────────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var NEW_TRANSPARENCY = 0;
  // ─── verify ─────────────────────────────────────────────
  // ... step-checks that produce VerificationReport entries
  // ─── apply ──────────────────────────────────────────────
  // ... mutations gated by `if (ctx.Mode == Live)`

The "inputs" block is conventionally the top of the file so a user can re-run a
saved script with different inputs by editing the top and pressing Run —
without round-tripping through the LLM.

Operations on the window: New / Load / Save / Save as / Delete / Rename /
Duplicate. Standard list view. Don't overthink.
</saved-scripts>

<script-flavors>
Every saved script declares a header attribute:

  // @flavor: batch          — side-loaded Database, no UI access
  // @flavor: current-doc    — uses Application.DocumentManager.MdiActiveDocument
  // @flavor: repl           — palette only, free-form

The batch UI's "Run" button is enabled only for batch-flavor scripts. Other
flavors get a "Run in REPL" button instead. The batch runtime refuses to run a
current-doc script (would fail anyway when trying to touch `Application`).

Validation lives in the runtime, not in human convention — at compile time we
inject a `Globals` type per flavor:
  - batch    → `Globals { Database Db; BatchContext Ctx; }`  (no Application, no Doc, no Ed)
  - current-doc → existing AcadGlobals
  - repl     → AcadGlobals
</script-flavors>

<verification-harness>
The skill MUST teach the LLM to structure scripts as a sequence of
verification steps followed by mutation steps. Every step calls a helper that
records a result into `ctx.Report`:

  ctx.Verify("layer-exists",
      () => db.GetLayerOrNull("X-FOOBAR") != null,
      "layer X-FOOBAR is present in the drawing");

  ctx.Verify("non-empty",
      () => db.EnumEntitiesOnLayer("X-FOOBAR").Any(),
      "at least one entity is on layer X-FOOBAR");

  if (ctx.AllVerified)
  {
      ctx.Apply("set-transparency", () =>
      {
          // ... mutate ...
          return $"{count} entities updated";
      });
  }

The runtime serialises `ctx.Report` per file and streams it back to the agent.
In test mode `ctx.Apply` records the intended action without running it.

This is what makes the loop work: the agent gets structured proof of each
precondition before it commits to live mode. The skill spec must hammer this:
verify everything, mutate nothing, until verification is clean.
</verification-harness>

<batch-runtime>
For each matched file:
  1. `using var db = new Database(false, true);`
  2. `db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: true, password: null);`
     (or `OpenForReadAndWriteNoShare` in live mode)
  3. `db.CloseInput(true);`
  4. Build a `BatchContext` for this file: mode (test/live), path, report.
  5. Compile script once (cached). Invoke with the per-file Globals.
  6. If live + script succeeded: `db.SaveAs(path, DwgVersion.Current);`
  7. Append per-file VerificationReport to streamed results.

No `[CommandMethod]` is involved — this code lives in a service the palette
calls. Side-loading does NOT need the AutoCAD main thread for DB-only work,
which is a big perf win for batches: parallelism is on the table later if we
want it (one DB per worker thread).
</batch-runtime>

<result-serialization>
The user voiced a separate concern that the REPL / MCP currently can't return
"all circles on this drawing" usefully — AutoCAD entities have hundreds of
properties, most useless. Need per-type DTOs:

  Circle    → { Center, Radius, Layer, Color }
  Polyline  → { Vertices[], Elevation, Layer, Closed }
  Text      → { Position, Contents, Layer, Height, Rotation }
  ... etc.

Implementation: register `JsonConverter<T>` per AutoCAD entity type with
System.Text.Json. The plugin maintains a `AcadJsonOptions` configured with all
known converters; both the REPL and the batch result stream use it. The
default for unknown types stays whatever System.Text.Json does today.

Open question — how to extend the set without rebuilding the plugin every
time we want a new DTO:

  Option A: Converters compiled into Acd.Mcp.dll. Rebuild + hot-reload via
    DevReload to add new ones. Fine for our own dev loop, painful for end users.

  Option B: Converters live in a separate "DTOs" assembly that the plugin
    loads from a known folder on startup (or on demand via a "reload DTOs"
    palette button). Reflection-discovered. Decoupled from the plugin's
    release cycle. Probably the right answer.

  Option C: Author DTOs as scripts and emit them at runtime. Most flexible,
    most fragile.

Lean B for v1 of this feature.
</result-serialization>

<mcp-tool-surface-additions>
Adds to the bridge:
  - `autocad_batch_propose() → { savedScripts[], selectedFolder, mask, fileCount }`
  - `autocad_batch_test(scriptId | scriptText) → { perFile: [ { path, report } ] }`
  - `autocad_batch_apply(scriptId | scriptText, confirm: true) → { perFile: [ ... ] }`
    (refuses unless confirm=true AND a prior test run on the same script
    succeeded — small but real safety rail against the LLM going live by
    mistake.)
  - `autocad_list_entities(filter) → [ DTO, DTO, ... ]`  (uses the per-type
    converters)

Annotations: `propose` and `test` are read-only. `apply` is destructive +
open-world + non-idempotent.
</mcp-tool-surface-additions>

<skill-shape>
A new skill (in the user's `~/.claude/skills/` or as a plugin) named `acd-batch`.
When invoked:
  1. Calls `autocad_batch_propose` to get current UI state.
  2. Tells the model: "you are writing a batch script. ALWAYS structure it as
     verify-then-apply. NEVER call mutating APIs outside `ctx.Apply` blocks.
     Use only batch-flavor globals. Run test mode first; review the report;
     do not call live until every verification passed."
  3. Provides examples of well-formed scripts.
  4. Documents the test→live progression and the `confirm: true` requirement.
</skill-shape>

<open-questions>
1. Test mode trick: do we open DBs read-only and let mutation calls fail
   silently (and the script wraps everything in `ctx.Apply` which swallows in
   test mode), or do we open them read-write but roll back the transaction?
   The latter is more honest — actually exercises the mutation code — but
   slower and dirties file timestamps on some I/O paths. Pick after measuring.

2. Parallel batches: tempting (one Database per worker), but AutoCAD's
   non-thread-safe runtime may bite. First version is sequential.

3. Cancellation mid-batch: easy to support (CT through each file), worth
   wiring from the start.

4. Where does the saved-scripts catalogue live for the BRIDGE to enumerate?
   The bridge is out-of-process from the plugin. Either (a) the plugin
   exposes a `list_scripts` RPC, (b) both processes read the same
   `%APPDATA%\Acd.Mcp\scripts\` directly. (b) is simpler and survives the
   plugin being unloaded.

5. The `ctx.Report` shape needs designing. Probably:
     { File, OverallStatus, Steps: [ { Name, Kind: Verify|Apply, Ok, Message, Detail? } ] }
   Stable schema so the agent can parse it reliably across script versions.
</open-questions>

<don't-do-yet>
This whole document is "later." The current task list (slices 1–5 + palette) is
shipped. Don't start any of this until the user explicitly says go, and don't
silently include any of it in unrelated changes.
</don't-do-yet>
