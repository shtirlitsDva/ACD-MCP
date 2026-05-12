<!--
Feature spec — third draft.
Captured iteratively via revdiff passes. Each revision narrows the design.
Not implemented. Designed to be handed to an autonomous Opus 4.7 high subagent
working in an isolated worktree, TDD red-green, with a test harness that
exercises every facet WITHOUT a live AutoCAD process.
-->

<status>idea / spec — not implemented, not scheduled</status>

<the-key-pivot>
Two execution modes coexist:

* **Autonomous-agent mode.** The user says: "in this folder I have these
  drawings, do X across all of them." The agent uses the live REPL to
  explore the active drawing, designs a batch script, pushes it to the
  BATCH palette editor, drives test runs (allowed), reviews results, iterates
  until the script is clean, and flags "safe to execute". The user then
  flips the Live switch and clicks Run. **Live execution is always the
  user's click.** There is no agent verb that runs Live mode.

* **User-driven mode.** The user writes / edits the script in the editor
  directly (or loads a saved one) and runs it. The agent is not in the loop.

In both modes the runtime owns the boilerplate: DB loading, file
accessibility, transaction lifetime, try/catch, rollback-or-commit, save.
The script body owns "what changes," nothing else.

Crucially: the script editor is a **live-shared slot**. The agent pushes a
new version → the editor updates immediately. The user types in the editor
→ the runtime executes that text. The runtime never reads anything other
than what is currently in the slot. There is no separate "agent script"
and "user script" — there is one script, and both sides can edit it.
</the-key-pivot>

<precondition>
The MCP must actually be reachable from Claude / Codex / etc. first. The bridge
exists; it needs registration in a real MCP client and one successful round-trip.
Do not begin this work before that verification.
</precondition>

<distribution>
This whole thing ships as a **Claude plugin** registered via the `/plugin`
system, not as separate hand-assembled artefacts. The plugin bundle layout:

  acd-mcp/
    .claude-plugin/
      plugin.json                          ← registers MCP server, lists skills
      skills/
        acd-batch/SKILL.md                 ← drives the autonomous batch flow
        acd-mcp-add-dto/SKILL.md           ← teaches the agent how to add DTOs
      mcp/
        Acd.Mcp.Bridge/                    ← the stdio MCP binary + deps
    autocad-bundle/
      ACD-MCP.bundle/
        PackageContents.xml
        Contents/
          Acd.Mcp.dll
          ICSharpCode.AvalonEdit.dll
          CommunityToolkit.Mvvm.dll
          ...

On install, the plugin:
1. Registers the bridge as an MCP server in the user's Claude client config.
2. Copies `autocad-bundle/ACD-MCP.bundle/` to
   `%APPDATA%\Autodesk\ApplicationPlugins\ACD-MCP.bundle\`. AutoCAD picks
   up bundles from that folder automatically on next launch. AutoCAD must
   be closed when updating the bundle (the file is locked otherwise).
3. Seeds `%APPDATA%\Acd.Mcp\dto\` and `%APPDATA%\Acd.Mcp\scripts\<flavor>\`
   with starter content if those folders don't already exist.

Reference bundle layout to mirror:
`C:\Users\MichailGolubjev\AppData\Roaming\Autodesk\ApplicationPlugins\DevReload.bundle\`
(`PackageContents.xml` at root, `Contents/` folder with all DLLs).
</distribution>

<reference-projects>
Two existing user projects to read before designing anything:

1. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\01 BatchProcessing.cs`
   — the user's production batch loop. Read it for the **shape** (nested
   using/using/try-catch, side-loaded Database, per-file iteration,
   Result+Status reporting). **Do not copy its style.** The user explicitly
   labelled it "rookie code." The implementation must use a real-world
   pattern — Result/Maybe-style discriminated unions, not bare enums + ad-hoc
   `AbortGracefully` calls. Treat the original as proof the AutoCAD calls
   work, not as a template for the code.

2. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\BPUIv2\UI\`
   — file-selector + mask filter UI. `DrawingList` and `FilterEditor` are
   reusable. Skip `SequenceComposer` (the agent replaces it).
</reference-projects>

<runtime-shape>
The runtime compiles the script body into a delegate and invokes it inside
a fixed per-file loop. Conceptually (real implementation uses a
discriminated `Outcome<T>` or similar over bare exceptions):

  foreach (var path in batch.Files)
  {
      ct.ThrowIfCancellationRequested();

      var openResult = FileAccess.TryOpen(path, mode);
      if (openResult is Locked locked)
      {
          report.Skip(path, "file is locked: " + locked.By);
          continue;
      }

      using var xDb = new Database(false, true);
      // Live: ReadDwgFile + write-back share; Test: ReadAllShare
      xDb.ReadDwgFile(path, mode == Live ? FileShare.Read : FileShare.ReadWrite,
                      allowCPConversion: false, password: "");

      using var xTx = xDb.TransactionManager.StartTransaction();
      var ctx = new BatchContext(xDb, xTx, mode, batchState, ct);

      Outcome bodyOutcome;
      try
      {
          bodyOutcome = scriptDelegate.Invoke(xDb, xTx, ctx);
      }
      catch (Exception ex)
      {
          bodyOutcome = Outcome.Crashed(ex);
      }

      bool commit = mode == Live
                  && bodyOutcome is Outcome.Ok
                  && !ctx.HasFailures;

      if (commit)
      {
          xTx.Commit();
          xDb.SaveAs(path, xDb.OriginalFileVersion);   // preserve original
      }
      // else: dispose without commit → rollback

      report.RecordFile(path, bodyOutcome, ctx.Steps);
  }

Notes:

* **File accessibility check** — before the load we probe access. In Live we
  need write share; in Test `FileShare.ReadWrite` is enough so the user can
  keep other tools open. If a file is locked by another process the runtime
  skips it with a clear reason; no exception flooding from `ReadDwgFile`.

* **SaveAs version** — always preserve the file's original DWG version.
  No user toggle, no asking. `xDb.OriginalFileVersion` is the source of truth.

* **Cancellation** — checked between files. Inside a script, the body can
  observe `ctx.Token` if it wants finer-grained cancellation.

* **Failure rollback** — any exception, any `ctx.Fail()`, any step failure →
  no commit, even in Live mode. The runtime expects no failures in Live;
  one failure aborts that file but the loop continues to the next.

* `CloseInput` from earlier drafts is not used (the reference loop doesn't
  use it; only adds confusion).
</runtime-shape>

<step-dsl>
The script body uses a fluent step API that wraps validation, mutation, and
reporting into a single composable unit. This replaces ad-hoc `ctx.Check(...)`
+ `ctx.Apply(...)` calls.

  ctx.Step("set-layer-transparency")
     .Require("layer-exists",  () => xDb.HasLayer(TARGET_LAYER))
     .Require("non-empty",     () => xDb.EntitiesOnLayer(xTx, TARGET_LAYER).Any())
     .Apply(() =>
     {
         int n = 0;
         foreach (var e in xDb.EntitiesOnLayer(xTx, TARGET_LAYER))
         {
             e.UpgradeOpen();
             e.Transparency = new Transparency((byte)TRANSPARENCY);
             n++;
         }
         return $"{n} entities updated";
     });

Each `Step` yields one of three structured outcomes:

  Step ::= Passed { name, requirement_results[], applied_summary }
         | Skipped { name, failing_requirement }
         | Crashed { name, exception }

Multiple steps can be chained or independent. The runtime aggregates all
step outcomes per file into `ctx.Steps`, which the runner serialises into
the per-file `BatchFileResult`.

Requirements are arbitrary predicates supplied by the script. The runtime
does NOT bake in "has-layer," "non-empty," "color-equals," etc. — those are
just lambdas the script writes inline. That keeps the runtime tiny and lets
the agent compose whatever check is appropriate for the task.

If a `Require` predicate throws, the step is recorded as Crashed with the
exception; the file is failed; no commit. Same for `Apply`.

The script can also declare helper methods or types inline at the top of the
file — Roslyn scripting supports both (`void Helper(...) { ... }` and
`record MyDto(...);` as top-level submissions). The runtime does not have to
ship a bag of extension methods; the agent writes whatever it needs.
</step-dsl>

<script-body-contract>
What the agent writes (no line-count limit — script can be as long as the
task requires; only boilerplate is forbidden):

  // @flavor: batch
  // @name: set-layer-transparency-zero
  // @summary: set transparency to 0 for all entities on layer X-FOOBAR

  // ─── inputs ─────────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var TRANSPARENCY = 0;

  // ─── helpers (optional — Roslyn allows inline) ──────────
  static bool HasMatchingEntity(Database db, Transaction tx, string layer)
      => db.EntitiesOnLayer(tx, layer).Any();

  // ─── steps ─────────────────────────────────────────────
  ctx.Step("set-transparency")
     .Require("layer-exists", () => xDb.HasLayer(TARGET_LAYER))
     .Require("non-empty",    () => HasMatchingEntity(xDb, xTx, TARGET_LAYER))
     .Apply(() =>
     {
         int n = 0;
         foreach (var e in xDb.EntitiesOnLayer(xTx, TARGET_LAYER))
         {
             e.UpgradeOpen();
             e.Transparency = new Transparency((byte)TRANSPARENCY);
             n++;
         }
         return $"{n} entities updated";
     });

The agent does NOT write: `new Database(...)`, `db.SaveAs`, `tx.Commit`,
`tx.Abort`, the outer `using`s, `try/catch`, file iteration. All of that
lives in the runtime template.
</script-body-contract>

<cross-file-state>
Some batches need state passed between files (the user's example: count
viewframes across the whole drawing set and number them sequentially). The
runtime provides a strongly-typed shared bag:

  // declare a state type at the top of the script
  record ViewframeCounter { public int Next = 0; }

  // anywhere in the body
  var counter = ctx.BatchState<ViewframeCounter>();   // shared across files
  foreach (var vf in xDb.GetViewframes(xTx))
  {
      vf.UpgradeOpen();
      vf.Number = ++counter.Next;
  }

`BatchState<T>()` returns the same instance for every file in the batch run.
First call creates a default-constructed `T`; subsequent calls return that
same reference. Different `T`s coexist (one Counter, one ErrorList, etc.).

The state is **per batch run**, not persisted across runs. A new Run click
gives a fresh state pool. This is intentional — persistent state belongs
elsewhere (in a DTO, written by the script to disk if needed).
</cross-file-state>

<flavors>
Three flavors, **strictly separated** folders + windows:

  @flavor: batch         — side-loaded Database; BATCH tab only.
  @flavor: current-doc   — active document; REPL or future Current-Doc tab.
  @flavor: repl          — palette-only free-form.

  %APPDATA%\Acd.Mcp\scripts\batch\
  %APPDATA%\Acd.Mcp\scripts\current-doc\
  %APPDATA%\Acd.Mcp\scripts\repl\

The BATCH palette window manages batch-flavored scripts only. No flavor
dropdown, no filtering, no mixing.

Compile-time enforcement: per-flavor `Globals` types. Batch globals expose
`xDb`, `xTx`, `ctx`, helper accessors — they do NOT expose `Application`,
`Document`, `Editor`. A batch script that tries to touch `Application` fails
to compile, with the agent seeing a clear diagnostic.
</flavors>

<batch-palette-ui>
Second tab on the existing ACD-MCP PaletteSet (alongside REPL).

  ┌── Files ──────────────────────────────────────────────┐
  │ Folder: [ ........................... ] [Browse]      │
  │ Mask:   [ *.dwg              ]  Recurse [x]           │
  │   → 47 files matched.  [Refresh]                      │
  ├── Script editor  (live-shared) ───────────────────────┤
  │ Scripts: [ ▼ set-layer-transparency-zero        ]     │
  │          [Save] [Save as…] [Delete] [Rename]          │
  │ ┌───────────────────────────────────────────────────┐ │
  │ │ <AvalonEdit, C# highlighting, same theme as REPL> │ │
  │ │ // @flavor: batch                                 │ │
  │ │ // @name: set-layer-transparency-zero             │ │
  │ │ var TARGET_LAYER = "X-FOOBAR";                    │ │
  │ │ ctx.Step("set-transparency").Require(...).Apply(...│ │
  │ └───────────────────────────────────────────────────┘ │
  ├── Execution ──────────────────────────────────────────┤
  │   ┌───────────────┐                                   │
  │   │ Test  ◀────▶ Live │   ← hand-rolled toggle        │
  │   └───────────────┘                                   │
  │   [ Run ]   [ Cancel ]    Progress: 12 / 47           │
  ├── Per-file results ───────────────────────────────────┤
  │ ✓ apartment-01.dwg   OK     5 entities changed         │
  │ ⚠ apartment-02.dwg   SKIP   layer not present          │
  │ ✗ apartment-03.dwg   FAIL   eLockViolation             │
  │   …                                                   │
  └───────────────────────────────────────────────────────┘

Behaviour:

* **Live-shared editor.** When the agent calls `autocad_batch_propose_script`,
  the new script text immediately replaces the editor's content (or, if the
  user has unsaved local edits, prompts: "Replace your changes with the
  agent's version?" with a "diff" option). The agent can update again at
  any time; the runtime always executes what is currently in the editor.

* **Slide-switch hand-rolled.** A styled `ToggleButton` (~30 lines XAML),
  no MahApps/ModernWpf dependency. Distinct colour for Live (red accent)
  so the state is unambiguous.

* **Cancel** only enabled while a batch is running. Stops the runner via
  `CancellationToken` between files (and, if the script body checks
  `ctx.Token`, within a file too).

* **UI must not freeze.** The runner runs on a threadpool task; progress
  + results dispatch to the WPF thread via the existing observable pattern.

* **Script names are telegram-style** — short, descriptive, no filler.
  ("set-layer-transparency-zero", not "set the transparency value of all
  entities on a given layer to zero.")

* **Agent-pushed scripts land at the top of the dropdown and auto-select**
  so the user sees the latest version immediately.

* **Future feature (not v1):** "Edit in VSCode" button. Requires shipping
  a stub `Acd.Mcp.BatchGlobals` reference so VSCode IntelliSense resolves
  `xDb`, `xTx`, `ctx`. Document the goal; defer the work.
</batch-palette-ui>

<agent-tool-surface>
The agent gets exactly two MCP tools for batch authoring:

  autocad_batch_propose_script(
      name: string,                    // telegram-style, e.g. "set-layer-transparency-zero"
      script_body: string,             // batch-flavour body only — no template, no loop
      input_summary: string?           // optional one-line summary
  ) -> { ok: true, saved_as: string }
      Annotations: ReadOnly=false (writes a file), Destructive=false,
                   Idempotent=true, OpenWorld=true.

      Writes the script to %APPDATA%\Acd.Mcp\scripts\batch\<name>.csx,
      inserts at top of the BATCH palette's dropdown, auto-selects it.
      If a script with the same name exists, overwrites it (idempotent).

  autocad_batch_run_test(
      name: string                     // name of a script already proposed
  ) -> { run_id: string,
         results_resource: "acd-mcp://batch-runs/<run_id>" }
      Annotations: ReadOnly=true (test mode never mutates files; rollback
                                   is guaranteed by the runtime),
                   Destructive=false, Idempotent=false, OpenWorld=true.

      Triggers a test run of the named script against the currently-selected
      folder + mask in the BATCH palette. Returns immediately with a run-id
      and an MCP resource URI the agent reads when it wants the results.
      The agent can call this in a loop while iterating on a script.

      **There is no autocad_batch_run_live tool.** Live execution requires
      the user to flip the slide-switch to Live and click Run, in person.
      The agent literally cannot trigger Live mode through the bridge.

Querying drawings, listing entities, exploring layers, etc., all flow
through the existing `autocad_execute_csharp` REPL tool. No new query tools.
That tool is intentionally generic; specialised tools would only multiply
the surface for the agent to learn.

The bridge auto-discovers tools via `WithToolsFromAssembly()`. Adding
these two is a matter of two new `[McpServerToolType]` classes; the bridge
core does not change.
</agent-tool-surface>

<feedback-loop>
Per the user's annotations, the chosen design is **MCP resources, on-demand
read** — Option A from v2, reinforced twice.

Resources exposed by the bridge:

  acd-mcp://batch-runs/recent
      Latest N completed BATCH runs (id, when, summary). Newest first.

  acd-mcp://batch-runs/<run_id>
      Full per-file result of a specific run. Includes step-level outcomes
      (which Requires passed, which Apply summaries ran, which exceptions
      were caught), elapsed timings, and the cancellation status.

  acd-mcp://batch-runs/last
      Convenience alias for the most-recent run.

When the agent wants feedback after `autocad_batch_run_test` returned a
run-id, it reads `acd-mcp://batch-runs/<run_id>` (or `acd-mcp://batch-runs/last`).
Polling is fine; runs are bounded.

Storage:
  %LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json

The history file is durable — across plugin reloads, AutoCAD restarts, and
plugin updates. The bridge enumerates the directory to back the `recent`
resource. The agent can read older runs by id.

This also gives the user audit value: every batch run is a JSON file with
the exact script body, file list, mode, and per-file outcomes — a paper
trail without extra work.

Notifications (Option C from v2) are dropped for v1. MCP client support
is uneven; polling is reliable.
</feedback-loop>

<dto-serialization-for-streaming>
The REPL's `autocad_execute_csharp` returns garbage by default when an
AutoCAD entity is the last expression (hundreds of properties, recursive
graphs). DTOs solve this.

Mechanism:

  %APPDATA%\Acd.Mcp\dto\*.csx — hot-reloadable .csx scripts. Each registers
                                JsonConverter<T> for one entity type. Loaded
                                via Roslyn on plugin startup AND on
                                missing-type detection (see below).

  Example  circle.csx:
      // @dto: Autodesk.AutoCAD.DatabaseServices.Circle
      Acad.RegisterDto<Circle>(c => new {
          center = c.Center,
          radius = c.Radius,
          layer  = c.Layer,
          color  = c.Color.ColorIndex,
      });

**No reload button.** The serializer checks at invocation: if an entity
needs serialising and no converter is registered for its runtime type, it
triggers a folder rescan + recompile, then retries. If still missing, falls
back to a marker `{ "$unsupported": "TypeName" }` so the agent sees the gap
clearly and can write a DTO.

Starter set, populated by the installer at first run, plus any user-added
files later:

  Built-in primitives:
      DBPoint, Point2d, Point3d, Vector3d, Extents3d, ObjectId

  Text:
      DBText, MText

  Entities:
      Circle, Line, Arc, Polyline, Polyline3d, Hatch, BlockReference

The BlockReference DTO and any other entity DTOs go through a separate
**data-provider abstraction** (next section), not direct property reads.

A dedicated skill `acd-mcp-add-dto` ships with the plugin and teaches the
agent:
  - where the DTO folder lives,
  - the @dto header format,
  - the registration shape,
  - the requirement to verify type metadata via the AutoCAD API docs
    (Context7 or other authoritative sources — **never guess**, capitalised
    rule from the user: GUESSING IS FORBIDDEN, ALWAYS VERIFY),
  - the "write a probe with execute_csharp first to confirm the property
    exists on the live type" workflow.
</dto-serialization-for-streaming>

<entity-data-provider-abstraction>
AutoCAD entities carry user-defined data through three different mechanisms:

  - **Block Attributes**       — for `BlockReference`s with attached `AttributeReference`s.
  - **PropertySets** (AECC)    — for any entity. Used by AutoCAD Civil / Map / MEP.
  - **XData**                  — extended data, key-value-ish, on any entity.

A user may store the same logical metadata in any of these depending on the
domain. DTOs that just read `block.AttributeCollection` miss PropertySet
users entirely.

The plugin defines an abstract data-provider interface:

  public interface IEntityDataProvider
  {
      // Returns Maybe<string> for "is this key present, and if so what's the value?"
      Outcome<string> TryRead(Entity ent, Transaction tx, string key);

      // For enumeration / preview cases.
      IReadOnlyDictionary<string, string> ReadAll(Entity ent, Transaction tx);
  }

Concrete providers ship in v1:
  - BlockAttributeProvider     — reads `BlockReference.AttributeCollection`.
  - PropertySetProvider        — reads via the AEC PropertyData APIs.
  - XDataProvider              — interface present, implementation stubbed
                                  (TODO marked, throws NotSupported). v1 ships
                                  with the interface so consumers can rely on
                                  it; the implementation lands when needed.

A `CompositeDataProvider` aggregates all three so DTOs can ask "what does
the user know as 'PartNumber' on this entity?" without caring where it's
stored.

DTO scripts use the composite by default. Advanced users can register a
DTO that picks a specific provider when they know the data only lives in
one place.
</entity-data-provider-abstraction>

<naming-and-flavor-files>
Saved scripts on disk use a header for flavor + metadata:

  // @flavor: batch
  // @name: set-layer-transparency-zero
  // @summary: set transparency to N for all entities on layer L
  // ─── inputs ─────────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var TRANSPARENCY = 0;
  // ─── steps ──────────────────────────────────────────────
  ctx.Step(...)...

Missing `@flavor` defaults to the containing folder's flavor. Missing
`@name` defaults to the file name without extension. Missing `@summary`
is just blank.
</naming-and-flavor-files>

<module-layout>
Strictly layered. Each module deep, no concern leaks across boundaries.

Project: `Acd.Mcp.Batch` (new) — pure runtime + abstractions, AutoCAD-free.
                                  References `Acd.Mcp` (root namespace) only
                                  for shared records like `ExecuteResult` /
                                  `Outcome<T>`.

  Outcome<T>           — discriminated union: Ok(T) | Skip(reason) | Failed(error)
                         + a non-generic Outcome for steps that have no payload.

  IBatchSession        — wraps a single drawing.
    AcadBatchSession (in Acd.Mcp): owns Database + Transaction.
    FakeBatchSession (tests):       in-memory entity dict.

  IBatchContext        — what the script body sees as `ctx`.
                         Step(name) returns IStepBuilder. BatchState<T>().
                         Token (CancellationToken).

  IStepBuilder         — .Require(name, predicate).Apply(action). Records
                         StepOutcome onto context when committed.

  StepOutcome          — Passed { name, requirements[], summary }
                       | Skipped { name, failing_requirement }
                       | Crashed { name, exception }.

  BatchScriptHost      — compiles a body string into a delegate.
                         Caches by SHA256(body). AutoCAD-free; takes an
                         IBatchSession factory at invocation.

  BatchRunner          — takes IDrawingHost, compiled script, Mode,
                         CancellationToken, IProgress<BatchFileResult>.
                         Iterates files, calls into IBatchSession, records
                         outcomes, never touches AutoCAD directly.

  IDrawingHost         — Open(path) -> IBatchSession.
                         Real: AcadDrawingHost in Acd.Mcp.
                         Fake: in-memory dict in tests.

  IFileAccessProbe     — TryOpen(path, mode) -> Outcome<FileLease> to detect
                         locked files before attempting Read/Write.

  BatchRunHistory      — writes per-run JSON to
                         %LOCALAPPDATA%\Acd.Mcp\batch-runs\; enumerates for
                         the MCP resource. Pure file I/O; no AutoCAD.

Project: `Acd.Mcp` (existing) — adds AutoCAD-backed AcadDrawingHost,
                                AcadBatchSession, the BATCH palette UI, the
                                script-folder watcher, the data-provider
                                concretes.

Project: `Acd.Mcp.Batch.Tests` (new) — `net8.0` (NOT `net8.0-windows`).
                                       NO acmgd / accoremgd / acdbmgd reference.
                                       Tests use FakeBatchSession + mock
                                       Database-shaped types (the LLM
                                       implements whatever mocks are needed —
                                       no library dependency).

Bridge changes: **none structural.** The bridge auto-discovers tools via
`WithToolsFromAssembly()`. Adding `autocad_batch_propose_script` and
`autocad_batch_run_test` is two new `[McpServerToolType]` classes in
`Acd.Mcp.Bridge\Tools\`. Adding the MCP resources for batch runs is
attribute-based; the bridge core code stays as-is.

The Tests project is the proof of testability. If a unit test of
`BatchRunner` needs AutoCAD, the abstraction is wrong; re-layer.

Test coverage (red first, green after, per the TDD discipline):

  - Loop iterates all files in input order.
  - Mode=Test never calls IBatchSession.Commit; Mode=Live calls it on success.
  - File-locked path → Skip with the lock reason; never opens the DB.
  - Any script exception → no commit; result.Failed = true; loop continues.
  - ctx.Step.Require(false) → step recorded as Skipped; no Apply ran.
  - ctx.Step Apply throws → step recorded as Crashed; no commit even in Live.
  - Cross-file state survives across the loop and is the same instance.
  - Cancellation between files → loop exits early; results report partial.
  - Cancellation inside script (script checks ctx.Token) → that file's
    result is Cancelled; loop exits.
  - Per-file results report via IProgress in order, exactly once per file.
  - Compile errors in the body surface BEFORE the loop starts (no file touched).
  - Compile error line/column maps to the body, not the wrapped template.
  - BatchRunHistory writes one JSON per run; enumerates newest-first.
  - Outcome<T> discriminated union pattern-matches exhaustively.
  - IEntityDataProvider composite tries each provider in registration order
    and returns the first hit.
</module-layout>

<resolved-decisions>
* Two execution modes coexist: autonomous-agent and user-driven. Live is
  always a user click.
* Script editor is a live-shared slot. Agent updates → editor reflects.
* `TryApply` is renamed to a fluent `Step / Require / Apply` chain.
* Cross-file state via `ctx.BatchState<T>()`. Per-run lifetime.
* Real `Outcome<T>` / discriminated union pattern, not a `Result+enum` bag.
* No line-count guidance to the agent. Boilerplate forbidden, body free-form.
* File accessibility check before load. Live: write share. Test: ReadWrite share.
* SaveAs always preserves the file's original DWG version. No setting.
* `CloseInput` dropped (reference doesn't use it).
* Parallel batches dropped (AutoCAD single-threaded; user sceptical).
* Helpers / mini-types declared inline in the script via Roslyn submission
  support. No baked-in extension method library.
* Slide-switch: hand-rolled `ToggleButton` style, no extra NuGet.
* Distribution: Claude plugin with `/plugin install` workflow. Plugin bundles
  the MCP server, the skills, and the AutoCAD `.bundle`.
* DTOs hot-reload implicitly on missing-type detection. No reload button.
* DTO starter set: DBPoint, Point2d, Point3d, Vector3d, Extents3d, ObjectId,
  DBText, MText, Circle, Line, Arc, Polyline, Polyline3d, Hatch, BlockReference.
* Skill `acd-mcp-add-dto` ships with the plugin, embeds the verify-don't-guess
  rule.
* `IEntityDataProvider` abstraction covers Block Attributes + PropertySets
  in v1, with XData interface stubbed.
* Saved-script catalogue: local folders only for v1. Shared-folder support
  is a later feature.
* Feedback loop: MCP resource `acd-mcp://batch-runs/...` (polling). Storage
  in `%LOCALAPPDATA%\Acd.Mcp\batch-runs\`.
* Tools the agent gets: exactly two. `autocad_batch_propose_script` (writes
  + auto-selects) and `autocad_batch_run_test` (test runs only; live is the
  user's click).
* Bridge structure unchanged — tools auto-discovered.
* Mocking AutoCAD types in tests is fine and expected. LLMs are good at it.
</resolved-decisions>

<open-decisions>
Things the implementing agent should ask before guessing.

1. Exact Claude-plugin packaging format. The user described the goals
   (single `/plugin install`, bundle to `%APPDATA%\Autodesk\ApplicationPlugins\`,
   skills under `.claude-plugin/skills/`). The agent must read the current
   Claude plugin manifest spec (`plugin.json` schema, install hooks, MCP
   server registration) and confirm the layout matches before writing the
   manifest. If anything is fuzzy, ask the user, not the spec author.

2. Discriminated-union representation. `Outcome<T>` could be a sealed
   abstract record with derived records (idiomatic C#, zero deps), or via
   OneOf NuGet, or via LanguageExt. Recommend hand-rolled records unless
   there's a strong reason otherwise. Decide and document.

3. How does the BATCH palette's "the editor has unsaved local edits and the
   agent pushed a new version" race resolve in the UI? Options:
   (a) prompt with "replace / keep / diff"; (b) always replace + show an
   "undo" toast; (c) refuse the agent push and surface an MCP error.
   v1 recommendation: (a). Confirm.

4. PropertySets API requires the AEC managed assemblies. Does the plugin
   ship + depend on these unconditionally, or detect-and-disable when they
   are missing (pure AutoCAD without Civil 3D / MEP)? Recommend
   detect-and-disable so vanilla AutoCAD installs still work.
</open-decisions>

<implementation-playbook>
Brief for the autonomous subagent. The agent must be Opus 4.7 high. The
agent works in an isolated worktree off this repo's master branch. Use this
section as the agent's prompt; the rest of the doc is context.

Acceptance criteria:

  1. `dotnet build Acd.Mcp.sln -c Debug -p:Platform=x64` is clean.
  2. New `Acd.Mcp.Batch` project builds against `net8.0` with no AutoCAD ref.
  3. New `Acd.Mcp.Batch.Tests` project builds against `net8.0`, all tests
     green via `dotnet test`, covers every bullet in <module-layout>.
  4. AutoCAD-backed `AcadDrawingHost` + `AcadBatchSession` live in
     `Acd.Mcp` proper. Manual smoke test only; not in CI.
  5. Two new `[McpServerToolType]` classes added: `BatchProposeScriptTool`
     and `BatchRunTestTool`. Annotations as in <agent-tool-surface>.
  6. MCP resources for `acd-mcp://batch-runs/...` implemented per
     <feedback-loop>. Run history written to `%LOCALAPPDATA%\Acd.Mcp\batch-runs\`.
  7. BATCH tab added to the existing PaletteSet. Layout per <batch-palette-ui>.
     Hand-rolled slide-switch present. Cancel button gates correctly.
     UI does not freeze during a run.
  8. Live-shared script editor: agent pushes via `autocad_batch_propose_script`
     → editor updates (with the unsaved-edits resolution decided in
     <open-decisions> item 3).
  9. Script folder watcher reloads the dropdown when files change on disk.
 10. DTO loader scans `%APPDATA%\Acd.Mcp\dto\*.csx` on startup and on
     missing-type detection. Starter set per <dto-serialization-for-streaming>.
 11. `IEntityDataProvider` abstraction implemented with the three concretes
     per <entity-data-provider-abstraction> (XData stubbed).
 12. Claude plugin packaging: `plugin.json`, two skills (`acd-batch`,
     `acd-mcp-add-dto`), bundled MCP server, bundled AutoCAD `.bundle`.
     Install script copies the bundle to
     `%APPDATA%\Autodesk\ApplicationPlugins\ACD-MCP.bundle\`.
 13. Documentation: a short usage note in `README.md`. A new
     `docs/design/batch-architecture.md` capturing the implementation
     decisions (Outcome representation, mocking strategy, etc.) so future
     contributors don't have to spelunk the code.

Working method:

  * **TDD red-green.** For every public method in `Acd.Mcp.Batch`, write
    the failing test first. Commit the red test (its commit message says
    so). Then commit the green implementation. The reviewer reads `git log`
    to confirm the discipline.
  * **Isolated worktree.** Branch named `feat/acd-batch`. No work on master.
  * **No AutoCAD reference in the test project.** If a unit test
    needs AutoCAD, re-layer until it doesn't. Mock whatever Database /
    Transaction / Entity API the test exercises — the LLM is good at it.
  * **Deep modules.** `BatchRunner.RunAsync` reveals nothing about Roslyn,
    nothing about file I/O, nothing about WPF. Each public type does one
    job.
  * **Real patterns, not rookie code.** The reference batch loop is shape
    only. Use `Outcome<T>` discriminated unions, fluent step builders,
    `IProgress<T>` for streaming, proper async; not bare enums and ad-hoc
    `AbortGracefully` calls.
  * **Never guess.** Verify AutoCAD API shapes via authoritative sources
    (the SDK headers, Context7, AutoCAD documentation). If uncertain about
    a property's existence, write a probe via `execute_csharp` against a
    live drawing to confirm. The user is emphatic: **GUESSING IS FORBIDDEN.**
  * **Read the reference projects first.** Both files in <reference-projects>
    are prerequisites. Read them, understand them, then design.
  * **Ask before guessing on <open-decisions>.** Print the open questions,
    wait for the user, then proceed.
  * **One PR.** Open a PR back to master when all acceptance criteria pass.
    Many small commits on the branch are fine; squash-merge candidate.

What the agent must NOT do:

  * Do not regress `autocad_execute_csharp` or the existing REPL palette.
  * Do not add any `list_entities`-style query tool. The REPL covers it.
  * Do not let the agent's tool surface drift. Exactly two new tools, named
    as in <agent-tool-surface>. Adding another requires user approval.
  * Do not allow Live execution from the bridge in any form. There is no
    `autocad_batch_run_live`. The Live click is the user's, in person.
  * Do not commit AutoCAD-dependent code into `Acd.Mcp.Batch`. Its csproj
    must reference zero `acmgd` / `accoremgd` / `acdbmgd` assemblies.
  * Do not bake a huge "helper extension methods" library into the runtime.
    Roslyn scripting lets the agent declare inline helpers; let it.
  * Do not invent slide-switch UX; hand-roll a `ToggleButton` style with
    a clear Live = red accent.
  * Do not write line-count guidance into the agent's skill ("keep scripts
    under N lines"). The user explicitly forbade it.

Output contract:

  * A green CI run on `feat/acd-batch`.
  * A PR with a clear summary referencing this spec.
  * A "what's tested vs what to exercise manually" note in the PR
    description so the user knows what to smoke-test in AutoCAD.
</implementation-playbook>
