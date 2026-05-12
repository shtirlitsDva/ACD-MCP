<!--
Feature spec — second draft.
Captured from user voice + revdiff annotations on 2026-05-12.
Not implemented. Designed for an autonomous subagent (Opus 4.7 high) working
in a worktree, TDD red-green, with a test harness that exercises every facet
WITHOUT invoking AutoCAD.
-->

<status>idea / spec — not implemented, not scheduled</status>

<the-key-pivot>
The agent does NOT drive batch execution. The agent writes only the *body* of
a batch script — the code that goes inside the try block of a fully-standardised
per-file loop. That script body arrives in the BATCH palette's editor with a
short telegram-style name. The user reviews, optionally edits inputs, flips a
slide-switch between Test and Live, and clicks Run. The user owns execution.
The runtime owns DB loading, transaction lifetime, try/catch, save-or-skip.
The script body owns: what changes.
</the-key-pivot>

<precondition>
The MCP must actually be reachable from Claude / Codex / etc. first. The bridge
exists; it just needs registration in the user's MCP client config and one
successful round-trip from a real LLM session. Don't begin this work before
that verification.
</precondition>

<reference-projects>
Before designing anything, the implementing agent MUST read:

1. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\01 BatchProcessing.cs`
   — the user's existing batch loop. The nested `using Database / using Transaction / try-catch`
   shape, the Result + ResultStatus convention, AbortGracefully — those are
   load-bearing patterns that work in production today.

2. `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\IntersectUtilities\BatchProcessing\BPUIv2\UI\`
   — the file-selector + mask filter UI. Steal `DrawingList` and `FilterEditor`
   patterns; ignore `SequenceComposer` (the LLM replaces it — there are no
   pre-defined blocks anymore).
</reference-projects>

<runtime-shape>
The plugin compiles every batch script into a method body inside this exact
template:

  using var xDb = new Database(false, true);
  xDb.ReadDwgFile(path, FileShare.ReadWrite, allowCPConversion: false, password: "");
  using var xTx = xDb.TransactionManager.StartTransaction();
  try
  {
      // ─── INJECTED SCRIPT BODY STARTS ─────────────────────────
      <user-script-body>
      // ─── INJECTED SCRIPT BODY ENDS ───────────────────────────

      if (mode == Live && !ctx.HasFailures)
          xTx.Commit();
      // else: dispose without commit → in-memory edits are discarded
  }
  catch (Exception ex)
  {
      // tx auto-disposes on the way out → roll back
      ctx.Fail(path, ex);
  }

  if (mode == Live && !ctx.HasFailures)
      xDb.SaveAs(path, DwgVersion.Current);

The script body sees: `xDb`, `xTx`, `ctx` (an `IBatchContext` for reporting),
plus a small set of helper extension methods. It does NOT call `tx.Commit()`,
does NOT call `db.SaveAs()`, does NOT see `Application.DocumentManager`.

Mode toggle: `Test` skips the Commit + SaveAs. The body still mutates the
in-memory DB, can still run assertions and gather information; nothing
hits disk. `Live` commits AND saves IFF the body produced no failures.

Boilerplate stays out of the script. The agent writes ~10–50 lines of body.

(Note: `CloseInput` from earlier drafts is dropped — not used in the
reference project; only adds confusion.)
</runtime-shape>

<script-body-contract>
What the agent writes:

  // inputs (top of the file, conventionally; users edit these directly)
  var TARGET_LAYER = "X-FOOBAR";
  var NEW_TRANSPARENCY = 0;

  // verify  — non-fatal assertions about preconditions
  ctx.Check("layer-exists", xDb.HasLayer(TARGET_LAYER),
            $"layer {TARGET_LAYER} present");
  ctx.Check("non-empty",     xDb.EntitiesOnLayer(xTx, TARGET_LAYER).Any(),
            $"layer {TARGET_LAYER} has at least one entity");

  if (!ctx.AllChecksPassed) return;   // skip apply for this file

  // apply — mutations. ctx.Apply records what was done.
  int updated = 0;
  foreach (var ent in xDb.EntitiesOnLayer(xTx, TARGET_LAYER))
  {
      ent.UpgradeOpen();
      ent.Transparency = new Transparency((byte)NEW_TRANSPARENCY);
      updated++;
  }
  ctx.Apply("set-transparency", $"{updated} entities updated");

The runtime expects no failures in Live mode. If `ctx.Check` returns false and
the script doesn't return, the body itself decides whether to proceed. The
runtime treats any thrown exception as a hard fail (rollback, report, move on).
If `ctx.AnyFail()` was called during the body, the runtime treats the file as
failed and rolls back regardless of mode.
</script-body-contract>

<flavors>
Three flavors, **strictly separated**:

  @flavor: batch         — side-loaded Database; runs in the BATCH tab only.
  @flavor: current-doc   — uses Application.DocumentManager.MdiActiveDocument;
                           runs in the REPL or a future "Current Doc" tab.
  @flavor: repl          — palette-only free-form.

Saved scripts live in **separate folders per flavor**:

  %APPDATA%\Acd.Mcp\scripts\batch\
  %APPDATA%\Acd.Mcp\scripts\current-doc\
  %APPDATA%\Acd.Mcp\scripts\repl\

The "BATCH scripts" window only sees batch-flavored files. The future
"Saved scripts" manager in other tabs only sees their own flavor. No
cross-contamination, no flavor filter dropdown, no mistakes.

Validation: at compile time, the runtime injects a `Globals` type matching the
flavor. Batch-flavor globals expose `Db`, `Tx`, `Ctx`, helper extensions —
they do NOT expose `Application`, `Document`, `Editor`. A batch script that
tries to call `Application.DocumentManager.MdiActiveDocument` fails to compile.
</flavors>

<batch-palette-ui>
Second tab on the ACD-MCP PaletteSet (alongside REPL). Single concern: batch.

Sections, top to bottom:

  ┌── Files ──────────────────────────────────────────────┐
  │ Folder: [ ........................... ] [Browse]      │
  │ Mask:   [ *.dwg              ]  Recurse [x]           │
  │   → 47 files matched.  [Refresh]                      │
  ├── Script editor ──────────────────────────────────────┤
  │ Scripts: [ ▼ set-layer-transparency       ]           │
  │          [Save] [Save as…] [Delete] [Rename]          │
  │ ┌───────────────────────────────────────────────────┐ │
  │ │ // inputs                                         │ │
  │ │ var TARGET_LAYER = "X-FOOBAR";                    │ │
  │ │ var NEW_TRANSPARENCY = 0;                         │ │
  │ │ ...                                               │ │
  │ │ <script body goes here — runtime adds the loop>   │ │
  │ └───────────────────────────────────────────────────┘ │
  ├── Execution ──────────────────────────────────────────┤
  │   [ Test   ●─────○   Live ]      ← slide-switch       │
  │   [ Run ]   [ Cancel ]   Progress: 12 / 47            │
  ├── Per-file results ───────────────────────────────────┤
  │ ✓ apartment-01.dwg   OK     5 entities changed         │
  │ ⚠ apartment-02.dwg   SKIP   layer not present          │
  │ ✗ apartment-03.dwg   FAIL   eLockViolation             │
  │   …                                                   │
  └───────────────────────────────────────────────────────┘

Notes from the annotations:

* **Slide-switch**, not radio buttons. WPF doesn't ship one; either reuse a
  `ModernWpf` / `MahApps.Metro` toggle or hand-roll a styled `ToggleButton`.
  Visual must immediately read as Test-vs-Live (different colour for Live).
* **Cancel button** only enabled while a batch is running. Must actually
  interrupt — the runner observes a `CancellationToken` between files (and
  optionally inside long-running scripts, if the script body checks
  `ctx.Token`).
* **UI must not freeze.** The runner runs on a threadpool task; progress and
  results are dispatched to the WPF thread via the standard
  `ExecutionLog`-style observable pattern.
* **Script names are telegram-style** — short, descriptive, no filler. The
  agent emits the name when it sends a script (e.g. "set-layer-transparency",
  not "set the transparency value of all entities on a given layer to zero").
* When the agent sends a new script, it lands **at the top** of the dropdown
  and is auto-selected so the user reviews it immediately.
* Future feature (not v1): **"Edit in VSCode"** button on the editor. VSCode
  needs `.csx` highlighting + a stub `Acd.Mcp.BatchGlobals` reference so
  IntelliSense knows about `xDb`, `xTx`, `ctx`. Defer; document the goal.
</batch-palette-ui>

<agent-narrow-tool-surface>
The agent gets one minimal MCP tool to do its part:

  autocad_batch_propose_script(
      name: string,           // telegram-style, e.g. "set-layer-transparency"
      script_body: string,    // batch-flavour body only (no template, no loop)
      input_summary: string?  // optional human-readable summary for log line
  ) -> { ok: true, saved_as: string }

Behaviour: writes the script to `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx`,
inserts at top of the BATCH palette's dropdown, auto-selects it. **Returns
immediately.** Does NOT execute. Does NOT load drawings. Does NOT enumerate
files. The agent never sees the file list, never sees test results unless the
user actively sends them back (see feedback-loop).

That's the *whole* surface for batch authoring. No `_test`, no `_apply`, no
`_list_files`. Adding more tools would mean the agent thinking it can drive
execution — which it can't, by design.

For everything ELSE (querying the active drawing, exploring entities), the
agent uses the existing `autocad_execute_csharp` REPL path — same tool as
today. No new "list_entities" tool is added; the agent writes a one-liner that
returns the entities it wants, and the result streams back through the same
channel. The whole point is keeping the tool surface abstract and generic.
</agent-narrow-tool-surface>

<feedback-loop>
Open design problem. The user explicitly flagged this needs more thought:

> User cannot send a signal from MCP to agent? The idea is that user controls
> execution. So if we want results back to agent, we need actively to signal
> agent to read feedback.

MCP servers can expose **resources** (read-on-demand) and **notifications**
(push). The shape that fits this design:

  Option A — Polling. The bridge exposes an MCP resource
  `acd-mcp://last-batch-result` that returns the most-recent BATCH result
  JSON. The agent reads it when the user says "check the results".
  Simple, no push, agent-on-demand. Fits the "user controls execution" rule.

  Option B — Explicit user-pushed feedback. A "Send to agent" button in the
  BATCH palette that writes the last result to a known file the bridge then
  surfaces as an MCP resource. Identical mechanics to A but UX-explicit:
  the user opts in per-result.

  Option C — Server-initiated notifications. The MCP SDK supports
  `notifications/resources/updated`. The bridge fires one when the BATCH UI
  finishes a run; supporting clients (Claude Desktop) refresh the resource.
  Richest UX; needs investigation of which clients actually wire up the
  refresh signal.

**Recommendation for v1: Option B**. Explicit user-driven feedback keeps the
"user owns execution" invariant clean. The button is the user's consent to
share. Implement A as a fallback so the agent can always poll on user
request. Skip C until we know which clients honour the update notification.

Agent's role in feedback: advisory only. Reads the BATCH result, summarises
("3 files succeeded, 2 failed with eLockViolation — looks safe to flip to Live
once those two are fixed"). The agent **cannot** execute Live mode. There is
no `autocad_batch_run_live` tool, full stop.
</feedback-loop>

<dto-serialization-for-streaming>
When the agent asks the live REPL "list all circles", `autocad_execute_csharp`
needs to return readable JSON, not the 200-property dump of every Entity.

Mechanism:

  - `%APPDATA%\Acd.Mcp\dto\*.csx` — a folder of DTO definition scripts. Each
    script registers one or more `JsonConverter<T>` for an AutoCAD entity
    type. Example file `circle.csx`:

        // @dto: Autodesk.AutoCAD.DatabaseServices.Circle
        Acad.RegisterDto<Circle>(c => new {
            center = c.Center,
            radius = c.Radius,
            layer  = c.Layer,
            color  = c.Color.ColorIndex,
        });

  - On plugin load: scan the folder, compile each script via Roslyn (same
    machinery the REPL uses), register the resulting converters into a
    shared `JsonSerializerOptions` used by the REPL when an `ExecuteResult`
    contains a domain object that needs structured serialisation.

  - Palette button (future tab "DTOs"): "Reload DTOs" — recompiles all
    .csx files. Same lifecycle as live REPL — no plugin rebuild required.

The user phrased this as "we are running scripts already, can we just have
DTOs as scripts that get hot-reloaded?". Yes; this is that.

Add a simple template / example DTO so users see the shape. Ship a starter
set (Circle, Line, Polyline, Text, BlockReference) in the project's
deployed `dto/` folder; copied to `%APPDATA%` on first run if missing.
</dto-serialization-for-streaming>

<module-layout>
Strictly layered. Each module deep, no concern leaks across boundaries.

Project: `Acd.Mcp.Batch` (new) — pure, AutoCAD-free runtime + abstractions.
                                  References `Acd.Mcp` only for `ExecuteResult`-style DTOs.

  IBatchSession        — wraps a single drawing.
    Real impl (`AcadBatchSession`, in Acd.Mcp): owns `Database` + `Transaction`.
    Test impl (`FakeBatchSession`, in tests):   in-memory entity dict + flags.

  IBatchContext        — what the script body sees as `ctx`.
    Check(name, ok, msg), Apply(name, msg), Fail(reason), Token, AllChecksPassed.

  BatchScriptHost      — given a body string + flavour-Globals, compiles it via
                         Roslyn into a delegate. Caches the compiled delegate
                         by body hash. AutoCAD-free; takes ISession as input.

  BatchRunner          — takes IEnumerable<string> file paths, an IDrawingHost
                         (factory for IBatchSession from path), a compiled script
                         delegate, a Mode (Test/Live), a CancellationToken, an
                         IProgress<BatchFileResult>. Loops, reports, never
                         touches AutoCAD directly.

  IDrawingHost         — Open(path) -> IBatchSession. The AutoCAD impl uses
                         `new Database(false, true) + ReadDwgFile`. The fake
                         impl returns a `FakeBatchSession` from a dict.

Project: `Acd.Mcp` — adds the AutoCAD-backed `AcadDrawingHost`, the BATCH
                     palette UI, and the script-folder watcher.

Project: `Acd.Mcp.Batch.Tests` (new) — pure unit tests, NO AutoCAD reference.
                                       Targets `net8.0` (not net8.0-windows).

The Tests project is the proof of testability: if a unit test of `BatchRunner`
requires AutoCAD, the abstraction is wrong. Reject the design and re-layer.

Concrete things the tests must cover (red first, green after):

  - Loop iterates all files in input order.
  - Mode=Test never calls IBatchSession.Commit; Mode=Live calls it on success.
  - Any script exception → tx.Abort (semantic, on the fake) + result.Failed = true.
  - ctx.Fail() → no commit even in Live mode.
  - Cancellation token tripped between files → loop exits, returns partial results.
  - Cancellation token tripped during a script body → that file's result is "cancelled", loop exits.
  - Per-file results are reported via IProgress in order, exactly once per file.
  - Compile errors in the script body surface before the loop starts (no file is touched).
  - Compile errors include line/column relative to the user-visible body, not the wrapped template.

The autocad-backed AcadDrawingHost has a separate integration test (manual
or skipped by default in CI), because it does need AutoCAD. Unit tests cover
the runner exhaustively without it.
</module-layout>

<naming-and-flavor-files>
Saved scripts on disk use a header comment for the flavor and inputs:

  // @flavor: batch
  // @name: set-layer-transparency
  // @summary: set transparency to N for all entities on layer L
  // ─── inputs ─────────────────────────────────────────────
  var TARGET_LAYER = "X-FOOBAR";
  var TRANSPARENCY = 0;
  // ─── body ───────────────────────────────────────────────
  ctx.Check("layer-exists", xDb.HasLayer(TARGET_LAYER), ...);
  ...

The runtime parses the header, validates `@flavor`, displays `@name`, and
remembers `@summary` for the log line. If a flavor header is missing, the file
defaults to its containing folder's flavor.
</naming-and-flavor-files>

<resolved-decisions>
From the revdiff round:

* No SequenceComposer. LLM replaces it.
* Slide-switch for Test/Live, not radios.
* Cancel button required, only active while running. UI must not freeze.
* Test mode = skip `tx.Commit()` only. Body still runs end-to-end.
* Runtime owns DB load + tx + try/catch + save. Body owns mutations only.
* Batch scripts live in their own folder, in their own palette tab, with
  their own manager window. No mixing with other flavors.
* Agent's tool surface is the absolute minimum: `autocad_batch_propose_script`.
  Everything else stays in the user's hands.
* Script delivered to top of the dropdown, auto-selected.
* DTOs are scripts in a folder. Hot-reloadable. No special MCP tool.
* No `autocad_list_entities`. Reuse `autocad_execute_csharp` + DTOs.
* Parallel batch processing: dropped. AutoCAD is single-threaded; the user
  is sceptical it can work. YAGNI for v1; revisit if a real bottleneck shows up.
* `CloseInput` not used in the reference loop; drop from runtime.
* Saved-script catalogue: local folder for v1. Shared folder support is a
  later feature.
</resolved-decisions>

<open-decisions>
Things still to resolve before implementation. The subagent should ask before
guessing:

1. Slide-switch implementation: ModernWpf, MahApps.Metro, or hand-rolled?
   ModernWpf is lighter; MahApps has a richer style set; hand-roll keeps the
   plugin's dependency surface minimal. Default recommendation: hand-roll
   one `ToggleButton` style — ~30 lines of XAML, no extra NuGet.

2. Compile-cache key: hash the script body? Hash + Mode? Hash + Mode + flavor?
   Probably (body hash) — Mode and flavor are runtime-passed, body is the
   compilation input. Confirm with a test.

3. Feedback-loop final shape (Option A/B/C above). Recommendation: B for v1,
   with A as a fallback. Decide before writing the bridge changes.

4. Where does the runtime put the "result of the last batch" so the bridge
   can expose it as an MCP resource? Probably `%LOCALAPPDATA%\Acd.Mcp\last-batch.json`.
   Atomic write; bridge reads on demand. Confirm.

5. SaveAs target version: should the runtime preserve the file's existing DWG
   version or always save current? Reference loop saves current; that may be
   undesirable for a mixed shop. Make it a per-batch setting in the palette
   with default = "preserve original version" if we can read it cheaply.
</open-decisions>

<implementation-playbook>
This section is the brief for the autonomous subagent (Opus 4.7 high) that
will implement the feature. **Use it as the agent's prompt; read the rest of
this doc as context.**

Acceptance criteria (the agent is done when all are true):

  1. Solution builds clean: `dotnet build Acd.Mcp.sln -c Debug -p:Platform=x64`.
  2. New project `Acd.Mcp.Batch` builds against `net8.0` (no AutoCAD ref).
  3. New project `Acd.Mcp.Batch.Tests` builds against `net8.0` and runs green
     with `dotnet test`. Tests cover every bullet in <module-layout>.
  4. AutoCAD-backed `AcadDrawingHost` lives in `Acd.Mcp` proper and is exercised
     manually (no CI requirement).
  5. New `autocad_batch_propose_script` MCP tool on `Acd.Mcp.Bridge` with
     annotations: readOnly=false (it writes a script file), destructive=false
     (it does not execute), idempotent=true (re-sending same name overwrites
     deterministically), openWorld=true.
  6. BATCH tab added to the existing PaletteSet. Layout per <batch-palette-ui>.
     Slide-switch present and functional. Cancel button gates correctly.
     UI does not freeze during a run.
  7. Script folder watcher reloads the dropdown when files change on disk.
  8. Telegram-style new-script-from-agent flow: lands at top, auto-selected.
  9. DTO loader scans `%APPDATA%\Acd.Mcp\dto\*.csx` on startup, registers
     converters into a shared `JsonSerializerOptions`. Hot-reload button works.
 10. Documentation: a short usage note added to `README.md`. Architecture
     decisions added to `docs/design/architecture.md` (or a new `batch-architecture.md`).

Working method:

  * **TDD red-green**, no exceptions. For every public method in
    `Acd.Mcp.Batch`, write the failing test before the implementation.
    Commit the red test, then commit the green implementation. Reviewer reads
    git log to verify the discipline.
  * Work in an **isolated worktree** off this repo's master.
  * **No AutoCAD reference in the test project.** If a test "needs AutoCAD,"
    the abstraction is wrong — re-layer until it doesn't.
  * Keep modules **deep**: small interfaces, hidden implementations. The
    `BatchRunner.RunAsync` signature must reveal *nothing* about Roslyn,
    nothing about file I/O, nothing about WPF.
  * **No spaghetti**: if a class touches more than one of {UI, threading,
    AutoCAD APIs, Roslyn, file system}, split it.
  * Ask before guessing on the items in <open-decisions>. The user said
    "very clever agent" — that means it knows what it doesn't know.
  * **Read the reference projects first** before writing any code. Both
    files listed in <reference-projects> are required prerequisites.
  * **Do not start until the user confirms** the open decisions and the
    feedback-loop choice. Print the open questions, wait, then proceed.
  * Open a PR back to master when the acceptance criteria are met.
    Squash-merge candidate; many small commits are fine on the branch.

What the agent must NOT do:

  * Do not regress the existing MCP tool (`autocad_execute_csharp`) or the
    existing REPL palette. They are independent and stay independent.
  * Do not add a `list_entities`-style tool. The whole point of this spec is
    that the existing REPL covers querying.
  * Do not let the agent's tool surface drift. Exactly one new tool:
    `autocad_batch_propose_script`. Anything else needs the user to approve.
  * Do not allow Live execution to be triggered from the bridge. The bridge
    has no Run-Live verb. Period.
  * Do not commit AutoCAD-dependent code into `Acd.Mcp.Batch`. That project's
    csproj must not reference any `acmgd`/`accoremgd`/`acdbmgd` assembly.

Output contract back to the user:

  * A passing CI run on the worktree branch.
  * A PR with a clear summary referencing this spec by path.
  * A short "what's testable, what isn't" note in the PR description so the
    user knows what to exercise manually in AutoCAD.
</implementation-playbook>
