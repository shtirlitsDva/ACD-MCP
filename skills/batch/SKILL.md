---
name: batch
description: |
  Full reference for the multi-drawing batch surface of the ACD-MCP
  plugin. Covers Step DSL, the three batch globals (xDb / xTx / ctx),
  cross-file state, the Test → hand-off → Live workflow, discriminated
  response shapes, staging model, mirror-before-propose rule, and the
  replaced_dirty UX contract. MUST be loaded before any multi-drawing
  MCP call.
when_to_use: User wants to apply the same change across multiple .dwg files in a folder (e.g. "set transparency to 0 on layer X-FOO in every drawing in this folder", "renumber viewframes across all sheets", "for every file matching *_SHT.dwg, ..."). Do NOT use for single-drawing operations (those load `/acd-mcp:script`).
---

<what-this-skill-is-for>
A two-stage workflow for autonomous batch edits across many AutoCAD drawings:

1. **Iterate** — the agent uses `autocad_script_execute` (the single-drawing surface) to explore one or two representative drawings, drafts a batch script body, pushes it into the BATCH palette editor via `autocad_batch_propose_script`, kicks off a Test run via `autocad_batch_run_test`, reads the per-file results from `acd-mcp://batch-runs/last`, fixes any failures, repeats.

2. **Hand off** — once Test is clean across every file, tell the user the script is safe to execute. The user flips the slide-switch from Test to Live in the BATCH palette and clicks Run. The runtime auto-runs the Test pass again first; only if every file passes does the Live pass actually commit + save.

**Live execution is ALWAYS the user's click.** There is no `autocad_batch_run_live` tool. Don't ask, don't try.
</what-this-skill-is-for>

<response-shape>
Every `autocad_batch_*` tool returns a **discriminated success-shape**. Always check `ok` first before reading payload fields.

```
# autocad_batch_propose_script
{ ok: true,  saved_as, name, replaced_dirty,
  error_code: null, error_message: null }
{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  saved_as: null, name: null, replaced_dirty: null }

# autocad_batch_run_test
{ ok: true,  run_id, pending, results_resource, note,
  error_code: null, error_message: null }
{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  run_id: null, pending: null, results_resource: null, note: null }

# autocad_batch_get_selection
{ ok: true,  folder, mask, recurse, files, count,
  error_code: null, error_message: null }
{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  folder: null, mask: null, recurse: null, files: null, count: null }
```

The bridge never throws on plugin-rejected failures — those would be stripped to a generic "An error occurred invoking ..." by the MCP SDK (see V2-G4 in `CRASH_TEST_V2_JOURNAL.md`). Instead the plugin's message travels on the success path in `error_message`. Typical `ok: false` cases for batch tools:

* "BATCH palette is not open. Run ACDMCP_PALETTE inside AutoCAD first." — user hasn't opened the palette yet. Tell them.
* "No files are currently selected in the BATCH palette. Set a folder + mask first." — palette is open but no folder/mask. Tell them what to set.
* "BATCH editor buffer is empty." — `autocad_batch_run_test()` with no body proposed and no saved name. Propose first.

`error_code` is the JSON-RPC numeric code from the plugin envelope, serialised as a string. Today it's the generic `-32603` for most plugin exceptions; a future change may emit semantic slugs (`no_selection`, `palette_closed`, …). Read `error_message` for the human content regardless.

The shape is shared with `autocad_script_propose`, so the "check `ok` first" pattern transfers between flavours.
</response-shape>

<the-three-globals>
A batch script body sees exactly three globals:

* `xDb` — a fresh `Autodesk.AutoCAD.DatabaseServices.Database`, loaded from one .dwg file by the runtime.
* `xTx` — an open `Autodesk.AutoCAD.DatabaseServices.Transaction` on `xDb`.
* `ctx` — an `IBatchContext` exposing the Step DSL, cross-file state, and the cancellation token.

The script body does NOT write:

* `new Database(...)`, `db.ReadDwgFile`, `db.SaveAs`
* `tx.Commit`, `tx.Abort`
* The outer `using` blocks
* `try { ... } catch { ... }` around the whole body
* Any file iteration — the runtime iterates

Touching `Application`, `Document`, or `Editor` in a batch script will fail to compile (those globals are intentionally not exposed for batch scripts — they're for the single-drawing SCRIPT surface).
</the-three-globals>

<why-this-framework>
**Why not just write plain C#?** Nothing in the runtime *physically* stops you from writing a flat `if (cond) { mutate(); }` body. But the Step DSL is mandatory for one reason: **observability of the test pass**. Every Step + Require is recorded per-file in the run history with named pass/fail markers. When Test reports "file X failed", you see *exactly which precondition didn't hold* and *what the Apply summary was if it ran* — without re-reading the script and guessing.

A script written without Steps is opaque: the run history shows only "Pass" or "Exception: <message>". The user can't audit which preconditions you assumed, and you can't iterate fast.

Treat the Step DSL the way you treat unit tests: not optional. If a piece of logic isn't worth a Step, it isn't worth running across N drawings.
</why-this-framework>

<step-dsl>
Every batch script uses the Step DSL:

```csharp
ctx.Step("step-name")
   .Require("requirement-name", () => /* bool predicate */ )
   .Require("another-requirement", () => /* another bool */ )
   .Apply(() =>
   {
       // mutations on xDb / xTx
       return "summary of what changed (1 short line)";
   });
```

**`Require` is a HARD precondition.** Outcomes per step:

* All `Require` predicates passed AND `Apply` returned normally → `Pass` with the summary string. The file is eligible for commit (in Live mode).
* Any `Require` predicate returned false → `Failure`. The whole file is marked failed. The Test phase exists specifically so the user catches this before Live.
* Any predicate or `Apply` threw → `Failure`. Same outcome.

**Do not use `Require` for branching logic.** If a step has legitimate "do this thing only when X" semantics, write it as a plain `if` inside `Apply`. `Require` is for invariants that MUST hold for the script to be valid against this file — layer exists, target geometry is present, expected block is present, etc. If a Require fails, the user's assumption about which files this script applies to is wrong, and the file selection or the script needs fixing.

You can chain multiple `Step` calls per file. Each is recorded independently.
</step-dsl>

<cross-file-state>
For batches that need to share state between files (e.g. "number every viewframe sequentially across all drawings"):

```csharp
record ViewframeCounter { public int Next = 0; }

var counter = ctx.BatchState<ViewframeCounter>();   // same instance every file
foreach (var vf in xDb.GetViewframes(xTx))
{
    vf.UpgradeOpen();
    vf.Number = ++counter.Next;
}
```

`ctx.BatchState<T>()` returns the same instance of T for every file in the run. First call default-constructs T. State is per-run only; a fresh Run click gives a fresh state pool.
</cross-file-state>

<read-mirror-before-proposing>
Before EVERY call to `autocad_batch_propose_script`, read the live mirror file:

```
%LOCALAPPDATA%\Acd.Mcp\buffer-batch.csx
```

This is what the user is currently looking at in the BATCH editor. Skipping this step means you may overwrite hand-edits the user has made since your last proposal.

**Mirror write semantics** (see `<the-staging-model>` for the propose-time side):

* When the **user is typing** in the WPF text-box, writes are debounced ~250 ms so a flurry of keystrokes doesn't translate into a flurry of disk writes.
* When an **agent propose** lands and is accepted (clean-editor inline promote, or user clicks Yes on the dirty-prompt), the mirror is **sync-flushed** — the file is on disk before `autocad_batch_propose_script` returns or before the prompt-accept callback completes.

So: if your read directly follows the user's typing, there may be ≤250 ms of lag. If your read follows an agent propose, the mirror is already durable.

The flow is:

1. **Read the mirror** with ordinary file tools.
2. **Compare** what's there to what you last proposed. Differences are user edits.
3. **Plan your update** against the user's current content, not against your own last proposal.
4. **Call `autocad_batch_propose_script`**.

If you can't read the mirror (file doesn't exist yet, permission error), the BATCH editor is empty / fresh — proceed with the proposal.
</read-mirror-before-proposing>

<replaced-dirty-contract>
`replaced_dirty` (on the `autocad_batch_propose_script` success path) is the agent's signal about what the user is about to experience:

* **`false` or `null`** — the editor was clean. The proposal was inline-promoted (see `<the-staging-model>`) — the editor's visible text and the mirror file already reflect the new body when this RPC returns. Nothing further to say to the user.

* **`true`** — the editor had unsaved typed edits AND the new body differs from what the user is currently editing. The user is being prompted (async dialog: "Replace your unsaved changes? Yes/No"). The user's Yes/No is NOT reported here — the RPC returns before the dialog resolves.

When you see `replaced_dirty: true`, **warn the user proactively in your reply**: "I'm proposing an updated version of the script — you'll see a prompt to replace your in-progress edits. Click Yes to take my version, or No to keep yours and I'll re-read it before my next proposal."
</replaced-dirty-contract>

<the-staging-model>
The disposition of a proposal depends on whether the editor has unsaved user edits at propose time (see V3-H3 in `CRASH_TEST_V3_JOURNAL.md`). The model is identical to the SCRIPT side:

* **Editor is CLEAN (`IsDirty=false`)** — the common agent-iteration case. `ScriptEditor.ProposeFromAgent` **inline-promotes**: `CurrentText` becomes the saved body, the mirror file is sync-flushed (no debounce), no pending slot is set. The mirror is durable on disk **before** the RPC returns. `replaced_dirty: false`.

* **Editor is DIRTY (`IsDirty=true`)** — the user has typed edits we'd clobber. ProposeFromAgent **stages** the body in `PendingProposal`, CurrentText and the mirror stay at what the user is editing, and the UI prompts the user. On Yes → AcceptPending promotes pending → current (and sync-flushes the mirror). On No → DiscardPending leaves the user's text untouched. `replaced_dirty: true`.

**The mirror file always reflects what the user sees on screen.** Reading the mirror is honest — you never read your own last-proposed body back during the brief prompt window in the dirty case, and you read your own proposal immediately in the clean case (because that IS what the user sees now).
</the-staging-model>

<saved-script-header>
Every saved batch script should start with three header lines:

```csharp
// @flavor: batch
// @name: telegram-style-name
// @summary: one-line description for the Manage Scripts list
```

These are parsed when the file is read back. Missing fields default sensibly (folder flavor / file name / empty summary). The `propose_script` tool writes the header automatically — you only supply the body.
</saved-script-header>

<workflow>
The exact ordered workflow for a typical task:

1. **READ EDITOR FIRST.** See `<read-mirror-before-proposing>` — this is a hard rule, not a soft suggestion.

2. **Sample one or two target drawings via SCRIPT.** The active drawing is often NOT the right sample — the user typically points the BATCH palette at a folder + filename mask (e.g. `*_SHT.dwg` in some directory) and the target set is what matches the mask, not whatever happens to be open. Get the folder + mask via the `autocad_batch_get_selection` tool (see step 5 below), pick one or two representative `.dwg` paths, and **sideload** them via `autocad_script_execute` to inspect:

   ```csharp
   // `using` directives go FIRST (top-level rule for SCRIPT submissions).
   // `using var` does not parse at submission top level — use block-form
   // `using (var ... ) { ... }` for disposables.
   using (var sample = new Database(false, true))
   {
       sample.ReadDwgFile(@"C:\drawings\house-12_SHT.dwg",
                          System.IO.FileShare.Read, true, "");
       using (var tx = sample.TransactionManager.StartTransaction())
       {
           var lt = (LayerTable)tx.GetObject(sample.LayerTableId, OpenMode.ForRead);
           var layers = new List<string>();
           foreach (ObjectId id in lt)
               layers.Add(((LayerTableRecord)tx.GetObject(id, OpenMode.ForRead)).Name);
           return layers;
       }
   }
   ```

   The assumption is that one or two drawings are representative of the whole batch. If they aren't, the Test phase catches it — that's what Test is for.

3. **Author or update the script.** Plan your edits relative to the editor's current content. Include the Step DSL chain(s). Keep the body focused on "what changes"; the runtime handles "for each file".

4. **Propose the script.**
   ```
   autocad_batch_propose_script(
       name = "set-layer-transparency",
       script_body = "...",
       input_summary = "set transparency=0 on layer X-FOO")
   ```
   If the user has dirty edits, they're prompted to confirm the replace. The tool response carries `replaced_dirty: true` whenever the editor was dirty AND your proposed body differs from the current editor text — that means a confirm dialog is being shown to the user asynchronously. Tell the user in your reply so they know to look at the palette and click Yes/No. See `<replaced-dirty-contract>`.

5. **Get the folder selection.** Call
   ```
   autocad_batch_get_selection()
   ```
   Returns `{ folder, mask, recurse, files: [...] }` reflecting what the user has set in the BATCH palette right now. **You cannot change these** — only the user can, via the palette UI. If the user has instead pasted explicit paths into the conversation (e.g. "run on these three files: ..."), prefer those and tell the user to either match the selection in the palette or accept that Live will use whatever the palette shows.

6. **Run Test.**
   ```
   autocad_batch_run_test()                      # runs current editor buffer
   autocad_batch_run_test(name = "<saved-name>") # runs a saved script
   ```
   No-arg form runs whatever is in the live editor buffer right now (the common case after `propose_script`). The `name` form is only for re-running a previously saved script from the batch scripts folder. Returns immediately; the actual run is async.

7. **Wait for results.** Two viable approaches:

   a. **Poll** the `acd-mcp://batch-runs/last` resource until the run is complete.

   b. **Use the `Monitor` tool** (preferred when available) to watch the SafeBoundary log for the completion marker:
      ```
      Monitor command (PowerShell):
        Get-Content -Wait -Tail 0 "$env:LOCALAPPDATA\Acd.Mcp\log.txt" |
          Select-String "BATCH RUN COMPLETED <run_id>"
      ```
      The runtime writes an explicit `BATCH RUN COMPLETED <run_id>` line when the run finishes. Monitor fires when that line appears, so you wake up exactly once instead of polling.

   Then inspect per-file outcomes. Each file is either Pass or Failure; Failure carries the exception message and the step outcomes leading up to it.

8. **Iterate.** If any file failed, diagnose, fix the script body, go to step 1. (Yes, step 1 — the user may have made edits in the palette while you were polling.)

9. **Tell the user it's safe.** Once Test is green across every file, tell the user: "Script is clean. Flip the switch to Live and click Run when you're ready." Do not press for it; their click is the safety boundary.
</workflow>

<rules-the-agent-must-follow>
1. **Live execution is the user's click.** There is no `autocad_batch_run_live` tool. Don't ask for one; don't invent one.

2. **Always read the editor buffer before proposing.** See `<read-mirror-before-proposing>`. Skipping this is how you trample the user's in-flight changes.

3. **Use the Step DSL.** Don't write raw `try { ... } catch (Exception e) { ... }` around your mutations — the runtime already does that, and you'll just produce more noise.

4. **Don't write boilerplate.** No `new Database`, no `SaveAs`, no transaction lifecycle. The runtime owns those.

5. **Step names are telegram-style.** Short, descriptive, hyphenated: `set-layer-transparency`, not `SetLayerTransparencyForAllEntities`.

6. **`Require` predicates are arbitrary lambdas.** You write them inline. No baked-in helper library exists (intentionally — the user wants the runtime narrow).

7. **The user picks how the batch reacts to a file failure.** The BATCH palette has an "On failure" dropdown with two options:
   * **Abort** (default) — the run stops at the first failed file.
   * **Skip** — failed files are recorded as Failure but the loop continues to the next file.

   Don't ask the user to change this mid-run; describe both modes in your hand-off message so they pick deliberately. "File is locked by another writer" always aborts the whole batch regardless of the dropdown — that's a structural failure, not a per-file one.

8. **Test mode never commits.** Even if you call `xTx.Commit()` yourself (don't), the runtime's outer transaction is configured for rollback. Test mode is safe to retry.

9. **When `replaced_dirty: true`, warn the user.** They're about to see a dialog and need context.

10. **No guessing.** See `/acd-mcp:start` `<hard-rule-guessing-forbidden>`. Probe types before referencing properties.
</rules-the-agent-must-follow>

<file-locations>
Batch-specific paths (the general folder layout is in `/acd-mcp:start` `<file-locations>`):

* Editor mirror (read before proposing): `%LOCALAPPDATA%\Acd.Mcp\buffer-batch.csx`
* Saved batch scripts: `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx`
* Batch-run history (persistent across plugin reloads): `%LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json`
</file-locations>

<sibling-skills>
* **`/acd-mcp:start`** — plugin-level briefing, MUST-LOAD rule, initial-checks (pipe + palette + AECC), global file-locations table, the hard "no guessing" rule.
* **`/acd-mcp:script`** — full reference for the single-drawing surface. Load instead of this skill when the user wants to inspect or modify the currently-open drawing. Also useful from inside a batch session to sideload sample drawings.
* **`/acd-mcp:add-dto`** — write or override a DTO when serialization emits `{"$unsupported":"..."}`.
</sibling-skills>

<example-full-script>
Canonical script layout — four sections in this order:

```
// @declarations    (header: @flavor, @name, @summary)
// Inputs           (constants and configuration)
// Code             (the Step DSL chain — the actual logic)
// Helpers          (helper methods, at the END of the file)
```

**Keep the script flat.** Helpers go at the bottom. Only extract a nested helper when it actually de-duplicates two or more call sites — a single-call-site helper just adds noise.

Example: set transparency to 0 for all entities on layer X-FOOBAR.

```csharp
// @declarations ────────────────────────────────────────
// @flavor: batch
// @name: set-layer-transparency-zero
// @summary: set transparency to 0 for all entities on layer X-FOOBAR

// ─── Inputs ────────────────────────────────────────────
var TARGET_LAYER = "X-FOOBAR";
var TRANSPARENCY = (byte)0;

// ─── Code ──────────────────────────────────────────────
ctx.Step("set-transparency")
   .Require("layer-exists",  () => LayerExists(TARGET_LAYER))
   .Require("non-empty",     () => EntitiesOnLayer(TARGET_LAYER).Any())
   .Apply(() =>
   {
       int n = 0;
       foreach (var e in EntitiesOnLayer(TARGET_LAYER).ToArray())
       {
           e.UpgradeOpen();
           e.Transparency = new Transparency(TRANSPARENCY);
           n++;
       }
       return $"{n} entities updated";
   });

// ─── Helpers ───────────────────────────────────────────
IEnumerable<Entity> EntitiesOnLayer(string layer)
{
    var bt = (BlockTable)xTx.GetObject(xDb.BlockTableId, OpenMode.ForRead);
    var ms = (BlockTableRecord)xTx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    foreach (ObjectId id in ms)
    {
        var ent = xTx.GetObject(id, OpenMode.ForRead) as Entity;
        if (ent is not null && ent.Layer == layer) yield return ent;
    }
}

bool LayerExists(string name)
{
    var lt = (LayerTable)xTx.GetObject(xDb.LayerTableId, OpenMode.ForRead);
    return lt.Has(name);
}
```

This is what `autocad_batch_propose_script` expects as `script_body`.
</example-full-script>

<diagnosing-failures>
A failing per-file result looks like this (excerpt from `acd-mcp://batch-runs/last`):

```json
{
  "Path": "C:\\drawings\\apartment-03.dwg",
  "Phase": "Test",
  "Status": "Failure",
  "Steps": [
    {
      "Kind": "Failure",
      "Name": "set-transparency",
      "Requirements": [
        { "Name": "layer-exists", "Passed": true },
        { "Name": "non-empty", "Passed": true }
      ],
      "ErrorMessage": "eLockViolation"
    }
  ],
  "Error": "eLockViolation"
}
```

The Requirements list shows which preconditions passed; the ErrorMessage shows what blew up inside Apply. Common AutoCAD failures to recognise:

* `eLockViolation` — the script tried to write without `UpgradeOpen()`.
* `eWasOpenForRead` — same root cause; open for write first.
* `eInvalidLayer` — typo in the layer name.

In the example above the file is Failure because `Apply` threw. A file is also Failure if any `Require` predicate returns false — Require is a hard precondition (see `<step-dsl>`); a failed Require means the script's assumptions don't match this file, and the user needs to either narrow the file selection or fix the script.
</diagnosing-failures>

<what-NOT-to-do>
* Don't call `xTx.Commit()` from the body — let the runtime decide.
* Don't call `xDb.SaveAs(...)` — the runtime saves with `OriginalFileVersion`.
* Don't loop over files yourself — the runtime iterates.
* Don't wrap your body in `try { ... } catch { ... }` — the runtime catches.
* Don't read `Application.DocumentManager` — there's no active doc in batch mode; you'd get a compile error if you tried.
* Don't bake a "helper extensions" library into the body — declare what you need inline. Roslyn scripting supports it.
* Don't write a 5-line script just because it "feels small enough" — there is no line-count guidance. Write what the task needs.
* Don't ever say "I'll just trigger Live for you." You can't.
</what-NOT-to-do>
