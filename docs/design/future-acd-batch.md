<!--
Feature spec — fourth draft. Final design pass before implementation.
Distribution + DTO concerns extracted to siblings.
This document is the implementation brief for an autonomous Opus 4.7 (xhigh)
subagent working in an isolated worktree, TDD red-green, with a test harness
that exercises every facet WITHOUT a live AutoCAD process.
-->

<status>idea / spec — implementation pending</status>

<the-key-pivot>
Two execution modes coexist:

* **Autonomous-agent mode.** User says "in this folder I have these drawings,
  do X across all of them." The agent uses the live REPL to explore the
  active drawing, designs a batch script, pushes it into the BATCH palette
  editor, drives test runs, reviews the streamed results, iterates until the
  script is clean, and flags "safe to execute." The user then flips the
  Live switch and clicks Run. **Live execution is always the user's click.**

* **User-driven mode.** User writes / edits the script in the editor directly
  (or loads a saved one) and runs it. The agent is not in the loop.

In both modes the runtime owns the boilerplate: file accessibility check,
DB loading, transaction lifetime, try/catch, rollback-or-commit, save. The
script body owns "what changes" — nothing else.

The script editor is a **live-shared slot**: when the agent pushes a new
version via `autocad_batch_propose_script`, the editor content updates
immediately; the runtime always executes the editor's current content.
The agent must read the editor before editing (so it doesn't trample
user-typed changes — see <agent-read-first>).
</the-key-pivot>

<reference-projects>
Two existing user projects to read before designing anything:

1. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\01 BatchProcessing.cs`
   — the user's production batch loop. Read it for the **shape** (nested
   using/using/try-catch, side-loaded `Database`, per-file iteration).
   **Do not copy its style.** The user explicitly labelled it "rookie code."
   Use it as proof the AutoCAD calls work; design clean abstractions from
   scratch.

2. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\BPUIv2\UI\`
   — file-selector + mask filter UI. `DrawingList` and `FilterEditor` are
   reusable. Skip `SequenceComposer` (the agent replaces it).
</reference-projects>

<runtime-shape>
The runtime compiles the script body into a delegate and invokes it inside
a fixed per-file loop. Conceptually (real implementation uses an `Outcome<T>`
discriminated union over bare exceptions):

  foreach (var path in batch.Files)
  {
      ct.ThrowIfCancellationRequested();

      // File-locked → THROW. We do not silently skip; the user must know.
      var lease = FileAccess.OpenExclusive(path, mode);   // FileShare.Read
                                                          // (both Test and Live)

      using var xDb = new Database(false, true);
      xDb.ReadDwgFile(path, FileShare.Read, allowCPConversion: false, password: "");

      using var xTx = xDb.TransactionManager.StartTransaction();
      var ctx = new BatchContext(xDb, xTx, mode, batchState, ct);

      Outcome bodyOutcome;
      try
      {
          bodyOutcome = scriptDelegate.Invoke(xDb, xTx, ctx);
      }
      catch (Exception ex)
      {
          bodyOutcome = Outcome.Failure(ex);
      }

      bool commit = mode == Live
                  && bodyOutcome is Outcome.Pass
                  && !ctx.HasFailures;

      if (commit)
      {
          xTx.Commit();
          xDb.SaveAs(path, xDb.OriginalFileVersion);   // preserve original DWG version
      }
      // else: dispose without commit → rollback

      report.RecordFile(path, bodyOutcome, ctx.Steps);
  }

Key invariants:

* **File-locked → throw.** No graceful skip. The user must intervene before
  the batch continues. Half-finished batches are unacceptable; they cause
  hours of state-restoration work.

* **Restrictive share in both modes.** `FileShare.Read` for the open call
  means no other process can have write access. If the file is open in
  another AutoCAD, the open fails and the batch aborts. The user explicitly
  forbade `FileShare.ReadWrite` even in Test mode.

* **SaveAs preserves the file's original DWG version** via
  `xDb.OriginalFileVersion`. No user toggle, no asking.

* **Cancellation** is checked between files. Inside a script, the body can
  observe `ctx.Token` if it wants finer-grained cancellation.

* **Failure rollback.** Any exception, any `ctx.Fail()`, any step Failure →
  no commit, even in Live mode. The runtime expects no failures in Live;
  one failure aborts that file but the loop continues to the next.

* `CloseInput` is not used (the reference loop doesn't use it; only adds
  confusion).
</runtime-shape>

<live-requires-prior-test-pass>
**Safety upgrade.** When the user clicks Run with the switch on Live:

1. The runner first performs a **complete Test pass** over the whole file
   list. In-memory only. No commits. No saves.
2. **Only if every file passes the Test pass** does the runner proceed to
   the Live pass.
3. If any file fails the Test pass, the Live pass is **not started**. The
   UI displays the failing file(s) and the user must fix the script (or
   the file) and retry.

UI shows two-phase progress: "Phase 1 / 2: Test" then "Phase 2 / 2: Live."
Cancellation stops the whole sequence.

This guards against the "stale script + locked file" trap that the user
flagged: a half-finished Live batch is far worse than a slower-but-safer
one. The double-I/O cost is acceptable; the alternative is unacceptable.

When the user clicks Run on Test directly, only the Test pass runs (no
Live). This is the normal iteration flow during script development.
</live-requires-prior-test-pass>

<step-dsl>
The script body uses a fluent step API that wraps validation, mutation, and
reporting into a single composable unit:

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

Each `Step` yields one of three structured outcomes (the discriminated
union is named StepOutcome):

  StepOutcome.Pass     { name, requirement_results[], applied_summary }
  StepOutcome.Skipped  { name, failing_requirement }
  StepOutcome.Failure  { name, exception }

(Earlier drafts called the failure case "Crashed" — renamed because in
AutoCAD lingo "crash" implies process termination, which it does not here.)

Multiple steps can be chained or independent. The runtime aggregates all
step outcomes per file into `ctx.Steps`, serialised into the
`BatchFileResult`.

`Require` predicates are arbitrary lambdas the script writes inline. The
runtime does NOT bake in `HasLayer`, `EntitiesOnLayer`, "non-empty", etc.
The agent composes whatever check is appropriate. Helper methods or small
record types can be declared inline at the top of the script — Roslyn
scripting supports both.

If a `Require` predicate throws → step is Failure; file is failed; no
commit.
If `Apply` throws → step is Failure; file is failed; no commit.

`StepOutcome` is a **hand-rolled** sealed abstract record + sealed derived
records. No `OneOf` / `LanguageExt` dependency. The user was explicit: no
hacks, clean discriminated union.
</step-dsl>

<script-body-contract>
What the agent writes (no line-count limit — script is as long as the task
needs; only boilerplate is forbidden):

  // @flavor: batch
  // @name: set-layer-transparency-zero
  // @summary: set transparency to 0 for all entities on layer X-FOOBAR

  // ─── inputs ─────────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var TRANSPARENCY = 0;

  // ─── helpers (optional — inline) ────────────────────────
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
Some batches need state passed between files (e.g. count viewframes across
the whole drawing set and number them sequentially). The runtime provides
a strongly-typed shared bag:

  // declare the state type at the top of the script
  record ViewframeCounter { public int Next = 0; }

  // anywhere in the body
  var counter = ctx.BatchState<ViewframeCounter>();   // same instance for every file
  foreach (var vf in xDb.GetViewframes(xTx))
  {
      vf.UpgradeOpen();
      vf.Number = ++counter.Next;
  }

`BatchState<T>()` returns the same instance for every file in the batch
run. First call creates a default-constructed `T`; subsequent calls return
that same reference. Different `T`s coexist (one Counter, one ErrorList,
etc.).

State is **per batch run**, not persisted across runs. A fresh Run click
gives a fresh state pool. Persistent state belongs elsewhere (write to
disk explicitly if you need it).
</cross-file-state>

<flavors>
Two flavors:

  @flavor: batch    — side-loaded Database; BATCH tab only.
  @flavor: repl     — palette-only free-form (covers what earlier drafts
                      called "current-doc" too).

Folders:

  %APPDATA%\Acd.Mcp\scripts\batch\
  %APPDATA%\Acd.Mcp\scripts\repl\

The BATCH palette window manages batch-flavored scripts only.

Compile-time enforcement: per-flavor `Globals` types. Batch globals expose
`xDb`, `xTx`, `ctx` — they do NOT expose `Application`, `Document`, or
`Editor`. A batch script that tries to touch `Application` fails to
compile with a clear diagnostic for the agent.
</flavors>

<batch-palette-ui>
Second tab on the existing ACD-MCP PaletteSet, alongside REPL.

  ┌── Files ──────────────────────────────────────────────┐
  │ Folder: [ ........................... ] [Browse]      │
  │ Mask:   [ *.dwg              ]  Recurse [x]           │
  │   → 47 files matched.  [Refresh]                      │
  ├── Editor  (live-shared slot — agent + user) ──────────┤
  │ [ Manage scripts… ]                                   │
  │ ┌───────────────────────────────────────────────────┐ │
  │ │ <AvalonEdit, C# highlighting, same theme as REPL> │ │
  │ │ // @flavor: batch                                 │ │
  │ │ // @name: set-layer-transparency-zero             │ │
  │ │ var TARGET_LAYER = "X-FOOBAR";                    │ │
  │ │ ctx.Step("set-transparency").Require(...).Apply(...│ │
  │ └───────────────────────────────────────────────────┘ │
  ├── Execution ──────────────────────────────────────────┤
  │   ┌───────────────┐                                   │
  │   │ Test ◀────▶ Live │   ← hand-rolled slide-switch   │
  │   └───────────────┘                                   │
  │   [ Run ]   [ Cancel ]    Phase 1/2 Test  12 / 47     │
  ├── Per-file results ───────────────────────────────────┤
  │ ✓ apartment-01.dwg   PASS    5 entities changed       │
  │ ⚠ apartment-02.dwg   SKIP    layer not present        │
  │ ✗ apartment-03.dwg   FAIL    eLockViolation           │
  │   …                                                   │
  └───────────────────────────────────────────────────────┘

Important behaviours:

* **Editor is one slot.** There is no script dropdown in the palette.
  The editor always shows "the current script." Agent pushes update it;
  user typing updates it; loading a saved script via the manage window
  replaces it (with unsaved-edits prompt).

* **Manage scripts window** (separate, opened via the button):
  list of all saved batch scripts, with **Load** / Save / Save-as /
  Delete / Rename. "Load" copies the file content into the editor
  (with the unsaved-edits prompt if dirty).

* **Unsaved-edits race resolution: option (a) — prompt.** When the
  agent pushes a new script and the editor has unsaved changes, show
  a modal: "Replace your changes with the agent's version? [Replace]
  [Keep mine] [Show diff]". The "Show diff" option opens a side-by-side
  in a small window.

* **Slide-switch hand-rolled.** A styled `ToggleButton` (~30 lines
  XAML), no MahApps/ModernWpf dependency. Distinct colour for Live
  (red accent). **No hacks** — clean style.

* **Cancel button** only enabled while a batch is running. Stops the
  runner via `CancellationToken` between files (and inside if the
  script body checks `ctx.Token`).

* **UI must not freeze.** The runner runs on a threadpool task;
  progress + results dispatch to the WPF thread via the existing
  observable pattern (see `ExecutionLog` for the established shape).

* **Two-phase progress.** When Live is selected, progress shows
  "Phase 1/2: Test" then "Phase 2/2: Live" with file counters in each.

* **Telegram-style names.** Short, descriptive, no filler.

* **Future feature (not v1):** "Edit in VSCode" button. Requires
  shipping a stub `Acd.Mcp.BatchGlobals` reference so VSCode
  IntelliSense resolves `xDb`, `xTx`, `ctx`. Document the goal; defer.
</batch-palette-ui>

<agent-tool-surface>
The agent gets two MCP tools for batch authoring:

  autocad_batch_propose_script(
      name: string,                    // telegram-style
      script_body: string,             // batch-flavour body only
      input_summary: string?           // optional one-line summary
  ) -> { ok: true, saved_as: string }

      Annotations: ReadOnly=false (writes a file), Destructive=false,
                   Idempotent=true (same name overwrites), OpenWorld=true.

      Writes %APPDATA%\Acd.Mcp\scripts\batch\<name>.csx, then loads its
      content into the editor (subject to the unsaved-edits prompt
      described in <batch-palette-ui>). If the name is already saved,
      overwrites.

  autocad_batch_run_test(
      name: string                     // name of a script previously proposed
                                       // OR currently in the editor (see below)
  ) -> { run_id: string,
         results_resource: "acd-mcp://batch-runs/<run_id>" }

      Annotations: ReadOnly=true (test mode never mutates files; rollback
                                   guaranteed by the runtime),
                   Destructive=false, Idempotent=false, OpenWorld=true.

      Triggers a test-mode batch run of the named script against the
      currently-selected folder + mask. Returns immediately with a run id
      and an MCP resource URI to read for results.

**There is no `autocad_batch_run_live`.** Live execution requires the
user to flip the slide-switch to Live and click Run, in person. The
agent literally cannot trigger Live mode through the bridge.

The bridge auto-discovers tools via `WithToolsFromAssembly()`; adding
these two means adding two new `[McpServerToolType]` classes. No
structural changes to the bridge core.

For everything else (querying drawings, listing entities, exploring
layers, etc.) the agent uses the existing `autocad_execute_csharp`
REPL tool. No specialised query tools.
</agent-tool-surface>

<agent-read-first>
The agent reads the editor's current content **before** editing it.
Mechanism: the runtime mirrors the editor's text to
`%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx` on every change (debounced,
~250 ms). The agent reads this file via ordinary file tools — no MCP
wrapper.

Workflow expected of the agent:
  1. Read `editor-buffer.csx` to see current state.
  2. Plan its edits relative to that content.
  3. Call `autocad_batch_propose_script` with the new body.

Saved scripts at `%APPDATA%\Acd.Mcp\scripts\batch\*.csx` are also
agent-readable via ordinary file tools. The agent can enumerate, read,
or even write them directly — but `propose_script` is the preferred
write path because it also surfaces the script in the editor.
</agent-read-first>

<feedback-loop>
Feedback flows back via **MCP resources, polled on demand** — no server
notifications, no in-band streaming.

Resources exposed by the bridge:

  acd-mcp://batch-runs/recent?limit=N&offset=M
      Paginated list of completed batch runs. Default limit 20.
      Each entry has id, timestamp, mode, files-attempted,
      summary-status. Newest first.

  acd-mcp://batch-runs/<run_id>
      Full per-file result of a specific run. Includes step-level
      outcomes (which Requires passed, which Apply summaries ran,
      which exceptions were caught), elapsed timings, and the
      cancellation status.

  acd-mcp://batch-runs/last
      Convenience alias for the most-recent run — saves the agent a
      round-trip to enumerate just to read the freshest entry.

Storage (durable across plugin reloads / AutoCAD restarts / upgrades):

  %LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json

The history is also a user-visible audit trail.

**Pagination is mandatory** for `/recent`. After dozens of runs the
list grows long; an unbounded response would flood the agent's
context. Default page size 20, max 100.
</feedback-loop>

<naming-and-flavor-files>
Saved scripts on disk use a header for flavor + metadata:

  // @flavor: batch
  // @name: set-layer-transparency-zero
  // @summary: set transparency to N for all entities on layer L
  // ─── inputs ────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var TRANSPARENCY = 0;
  // ─── steps ─────────────────────────────────────────
  ctx.Step(...)...

Missing `@flavor` defaults to the containing folder's flavor. Missing
`@name` defaults to the file name without extension. Missing
`@summary` is blank.
</naming-and-flavor-files>

<module-layout>
Strictly layered. Each module deep, no concern leaks across boundaries.

Project: `Acd.Mcp.Batch` (new) — pure runtime + abstractions. AutoCAD-free.
                                  References `Acd.Mcp` (root) only for shared
                                  records like `ExecuteResult` / `Outcome<T>`.

  Outcome<T>           — discriminated union: Pass(T) | Skip(reason) | Failure(error).
                         Hand-rolled sealed abstract record + sealed derived
                         records. No OneOf/LanguageExt dependency.

  StepOutcome          — Pass { name, requirement_results, summary }
                       | Skipped { name, failing_requirement }
                       | Failure { name, exception }.

  IBatchSession        — wraps a single drawing.
    AcadBatchSession (in Acd.Mcp): owns Database + Transaction.
    FakeBatchSession (tests):       in-memory entity dict.

  IBatchContext        — what the script body sees as `ctx`.
                         Step(name) returns IStepBuilder.
                         BatchState<T>() shared bag.
                         Token (CancellationToken).
                         Fail(reason) / HasFailures.

  IStepBuilder         — .Require(name, predicate).Apply(action). Records
                         StepOutcome on the context when the chain
                         terminates.

  BatchScriptHost      — compiles a body string into a delegate.
                         Caches by SHA256(body). AutoCAD-free; takes an
                         IBatchSession factory at invocation.

  BatchRunner          — takes IDrawingHost, compiled script, Mode,
                         CancellationToken, IProgress<BatchFileResult>.
                         Implements the Test-then-Live two-phase flow
                         when Mode == Live. Iterates files, calls into
                         IBatchSession, records outcomes, never touches
                         AutoCAD directly.

  IDrawingHost         — Open(path) -> IBatchSession.
                         Real: AcadDrawingHost in Acd.Mcp.
                         Fake: in-memory dict in tests.

  IFileAccessProbe     — OpenExclusive(path) -> FileLease.
                         Throws on lock (no graceful skip).

  BatchRunHistory      — writes per-run JSON to
                         %LOCALAPPDATA%\Acd.Mcp\batch-runs\;
                         enumerates with pagination for the MCP resource.
                         Pure file I/O.

Project: `Acd.Mcp` (existing) — adds:
  - AcadDrawingHost + AcadBatchSession (AutoCAD-backed concretes).
  - BATCH palette UI + Manage Scripts window + editor-buffer mirror.
  - Script-folder watcher.
  - Wires new tools into the bridge.

Project: `Acd.Mcp.Batch.Tests` (new) — net8.0 (NOT net8.0-windows).
                                       NO acmgd / accoremgd / acdbmgd reference.
                                       Tests use FakeBatchSession +
                                       hand-written AutoCAD-shaped mocks
                                       (the LLM writes mocks freely — no
                                       library dependency).

Bridge changes: **none structural.** Tools auto-discovered via
WithToolsFromAssembly. Adding `autocad_batch_propose_script` and
`autocad_batch_run_test` is two new `[McpServerToolType]` classes in
`Acd.Mcp.Bridge\Tools\`. MCP resources are attribute-registered the
same way.

**The Tests project is the proof of testability.** If a unit test of
`BatchRunner` needs AutoCAD, the abstraction is wrong — re-layer.

Test coverage required (red first, green after, per TDD discipline):

  - Loop iterates all files in input order.
  - Mode=Test never calls IBatchSession.Commit; Mode=Live calls it on success.
  - File-locked path → THROW; loop aborts; no files touched.
  - Mode=Live triggers an internal Test-pass first; Live pass starts ONLY
    if the Test pass yields zero failures.
  - Mode=Live with Test-pass failures → Live pass is NOT started; results
    reflect the failed Test pass.
  - Any script exception → no commit; StepOutcome.Failure; loop continues.
  - ctx.Step.Require(false) → step recorded as Skipped; Apply did not run.
  - ctx.Step Apply throws → step recorded as Failure; no commit.
  - Cross-file state survives across the loop and is the same instance.
  - Cancellation between files → loop exits early; partial results.
  - Cancellation inside script (script checks ctx.Token) → that file's
    result is Cancelled; loop exits.
  - Per-file results report via IProgress in order, exactly once per file.
  - Compile errors in the body surface BEFORE the loop starts (no file touched).
  - Compile errors include line/column relative to the user-visible body,
    not the wrapped template.
  - BatchRunHistory writes one JSON per run; pagination of /recent works
    (offset + limit applied correctly, newest-first ordering stable).
  - Outcome<T> + StepOutcome pattern-match exhaustively (test the switch
    expression with every variant + the default branch).
</module-layout>

<resolved-decisions>
* Two execution modes coexist: autonomous-agent and user-driven.
* Live is always a user click. No `autocad_batch_run_live` tool. Ever.
* Script editor is a live-shared slot. No dropdown in the palette.
* Manage Scripts is a separate window opened via a button.
* Editor content mirrored to `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`
  for agent reads via ordinary file tools.
* Agent reads editor content FIRST before proposing edits.
* Step DSL: `ctx.Step(name).Require(predicate).Apply(action)`.
* StepOutcome names: Pass | Skipped | Failure. ("Crashed" was an AutoCAD
  lingo conflict.)
* Cross-file state via `ctx.BatchState<T>()`. Per-run lifetime.
* Outcome<T> hand-rolled discriminated union. NO OneOf. NO LanguageExt.
  No hacks.
* No line-count guidance to the agent.
* File-locked → THROW (no graceful skip).
* Both Test and Live use `FileShare.Read` (no `ReadAllShare`).
* Live mode auto-runs a complete Test pass first; Live proceeds only on
  zero Test failures. Two-phase progress in UI.
* SaveAs preserves the file's original DWG version. No setting.
* Inline helpers / mini records in scripts via Roslyn submissions.
* Slide-switch: hand-rolled `ToggleButton` style. No extra NuGet.
* Unsaved-edits race: prompt (Replace / Keep / Show diff).
* MCP resources for feedback: paginated `/recent`, by-id, `/last`.
  Storage `%LOCALAPPDATA%\Acd.Mcp\batch-runs\`.
* Two flavors only: batch, repl. (No "current-doc" — same as repl.)
* Saved-script catalogue: local folders only for v1.
* Tools the agent gets: exactly two (`propose_script`, `run_test`).
* Bridge structure unchanged — tools auto-discovered.
* Mocking AutoCAD types in tests is expected and fine.
</resolved-decisions>

<open-decisions>
The implementing agent should ask before guessing on these. They are not
blockers; they shape one or two code paths each.

1. Compile-cache key: SHA256(body) only, or include something else?
   Recommendation: body hash alone — Mode and flavor are runtime-passed,
   body is the compilation input. Confirm with a test.

2. Editor-buffer debounce interval. 250 ms is the proposed default;
   confirm or adjust based on a quick benchmark of WPF text-change rates.

3. What happens if the user clicks Run while a previous run is still
   in progress? Recommendation: Run button is disabled while running;
   the existing run must complete or be cancelled first. Confirm.

4. Manage Scripts window: is it modal or non-modal? Modal is simpler
   (closes after Load) and matches the "select one to load" intent.
   Recommendation: modal.
</open-decisions>

<implementation-playbook>
Brief for the autonomous subagent. The agent must be **Opus 4.7 (xhigh)**.
The agent works in an isolated git worktree off this repo's master branch.
Use this section as the agent's prompt; the rest of the doc is context.

Acceptance criteria (all must be true):

  1. `dotnet build Acd.Mcp.sln -c Debug -p:Platform=x64` is clean.
  2. New project `Acd.Mcp.Batch` builds against `net8.0` with NO AutoCAD ref.
  3. New project `Acd.Mcp.Batch.Tests` builds against `net8.0`, all tests
     green via `dotnet test`, covers every bullet listed in <module-layout>.
  4. AutoCAD-backed `AcadDrawingHost` + `AcadBatchSession` live in
     `Acd.Mcp` proper. Manual smoke test only; not in CI.
  5. Two new `[McpServerToolType]` classes added to the bridge:
     `BatchProposeScriptTool` and `BatchRunTestTool`. Annotations as in
     <agent-tool-surface>.
  6. MCP resources for `acd-mcp://batch-runs/...` implemented per
     <feedback-loop>. Run history written to
     `%LOCALAPPDATA%\Acd.Mcp\batch-runs\`. Pagination works.
  7. BATCH tab added to the existing PaletteSet, layout per
     <batch-palette-ui>. Hand-rolled slide-switch. Cancel button gates
     correctly. UI does not freeze during a run. Two-phase progress
     visible when Live is chosen.
  8. Manage Scripts window opens via the editor's "Manage scripts…"
     button. Load / Save / Save-as / Delete / Rename all work.
  9. Editor content mirrored to `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`,
     debounced. Agent can read it directly.
 10. Script folder watcher reloads the Manage Scripts list when files
     change on disk.
 11. Live mode runs a complete Test pass first; refuses to start Live if
     any Test file failed. Confirmed by tests (use the fake host).
 12. Documentation: a short usage note added to `README.md`. A new
     `docs/design/batch-architecture.md` captures the implementation
     decisions (Outcome representation, mocking strategy, two-phase
     flow rationale) so future contributors don't have to spelunk.

Working method:

  * **TDD red-green.** For every public method in `Acd.Mcp.Batch`, write
    the failing test first. Commit the red test (commit message says
    "red:") then commit the green implementation (commit message says
    "green:"). The reviewer reads `git log` to confirm the discipline.
  * **Isolated worktree.** Branch named `feat/acd-batch`. No work on master.
  * **No AutoCAD reference in the test project.** If a test would need
    AutoCAD, re-layer until it doesn't. Mock whatever Database /
    Transaction / Entity shape the test exercises — LLM-written mocks are
    expected.
  * **Deep modules.** `BatchRunner.RunAsync` reveals nothing about Roslyn,
    nothing about file I/O, nothing about WPF. Each public type does one
    job.
  * **Real patterns, not rookie code.** Use `Outcome<T>` discriminated
    unions, fluent step builders, `IProgress<T>` for streaming, proper
    async; not bare enums, ad-hoc abort calls, or in-band magic strings.
  * **NO HACKS.** The user repeatedly emphasised clean design. If a
    proposed approach feels like a workaround, stop and find the right
    way. Hand-roll the discriminated union, hand-roll the slide-switch
    style, hand-roll the mocks.
  * **GUESSING IS FORBIDDEN.** Always verify AutoCAD API shapes via
    authoritative sources (the SDK headers, Context7, the live runtime
    via `autocad_execute_csharp`). The user is emphatic about this.
  * **Read the reference projects first.** Both files in
    <reference-projects> are prerequisites. Read them, understand them,
    then design.
  * **Ask before guessing on <open-decisions>.** Print the questions
    you have, wait for the user, then proceed.
  * **One PR.** Open a PR back to master when all acceptance criteria
    pass. Many small commits on the branch are fine; squash-merge
    candidate.

Hard rules — do not violate:

  * Do not regress `autocad_execute_csharp` or the existing REPL palette.
  * Do not add any `list_entities`-style query tool. The REPL covers it.
  * Do not add a third batch tool without user approval. Exactly two.
  * Do not allow Live execution to be triggered from the bridge in any
    form. No `autocad_batch_run_live`. The Live click is the user's, in
    person.
  * Do not commit AutoCAD-dependent code into `Acd.Mcp.Batch`. Its
    csproj must reference zero `acmgd` / `accoremgd` / `acdbmgd`
    assemblies.
  * Do not bake a huge "helper extension methods" library into the
    runtime. Roslyn scripting lets the agent declare inline helpers; let
    it.
  * Do not write line-count guidance into the agent's skill or tool
    descriptions ("keep scripts under N lines"). User explicitly forbade.
  * Do not work on DTO serialization. It is a separate concern in
    `docs/design/future-dto-and-data-providers.md` and has its own
    implementation cycle.
  * Do not work on plugin packaging / multi-client distribution. It is a
    separate concern in `docs/design/future-plugin-distribution.md`.

Output contract:

  * A green CI run on `feat/acd-batch`.
  * A PR with a clear summary referencing this spec.
  * A "what's tested vs what to exercise manually" note in the PR
    description.
  * If scope is too large to finish in one pass, prioritise the
    FOUNDATION (Acd.Mcp.Batch project, Outcome<T>, StepOutcome,
    IBatchSession, BatchRunner with full test coverage). Get those
    landed green, then push UI / palette / MCP-tool work as follow-ups.
    Leave the PR description explicit about what's done and what's not.
</implementation-playbook>
