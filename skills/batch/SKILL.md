---
name: batch
description: |
  Author and iterate batch C# scripts that mutate many AutoCAD drawings via the
  ACD-MCP plugin. Use when the user wants to apply the same change across a
  folder of .dwg files (e.g. "set transparency to 0 on layer X-FOO in every
  drawing in this folder", "number every viewframe sequentially across all
  drawings"). The agent designs and tests the script; the user clicks Run on
  Live to actually execute.
---

<what-this-skill-is-for>
A two-stage workflow for autonomous batch edits across many AutoCAD drawings:

1. **Iterate** — the agent uses `autocad_execute_csharp` (the REPL) to
   explore the active drawing, drafts a batch script body, pushes it into
   the BATCH palette editor via `autocad_batch_propose_script`, kicks off a
   Test run via `autocad_batch_run_test`, reads the per-file results from
   `acd-mcp://batch-runs/last`, fixes any failures, repeats.

2. **Hand off** — once Test is clean across every file, tell the user the
   script is safe to execute. The user flips the slide-switch from Test to
   Live in the BATCH palette and clicks Run. The runtime auto-runs the
   Test pass again first; only if every file passes does the Live pass
   actually commit + save.

**Live execution is ALWAYS the user's click.** There is no
`autocad_batch_run_live` tool. Don't ask, don't try.
</what-this-skill-is-for>

<the-three-globals>
A batch script body sees exactly three globals:

* `xDb` — a fresh `Autodesk.AutoCAD.DatabaseServices.Database`, loaded
  from one .dwg file by the runtime.
* `xTx` — an open `Autodesk.AutoCAD.DatabaseServices.Transaction` on `xDb`.
* `ctx` — an `IBatchContext` exposing the Step DSL, cross-file state, and
  the cancellation token.

The script body does NOT write:

* `new Database(...)`, `db.ReadDwgFile`, `db.SaveAs`
* `tx.Commit`, `tx.Abort`
* The outer `using` blocks
* `try { ... } catch { ... }` around the whole body
* Any file iteration — the runtime iterates

Touching `Application`, `Document`, or `Editor` in a batch script will
fail to compile (those globals are intentionally not exposed for batch
scripts — they're for the REPL).
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

* All `Require` predicates passed AND `Apply` returned normally → `Pass`
  with the summary string. The file is eligible for commit (in Live mode).
* Any `Require` predicate returned false → `Failure`. The whole file is
  marked failed. The Test phase exists specifically so the user catches
  this before Live.
* Any predicate or `Apply` threw → `Failure`. Same outcome.

**Do not use `Require` for branching logic.** If a step has legitimate
"do this thing only when X" semantics, write it as a plain `if` inside
`Apply`. `Require` is for invariants that MUST hold for the script to
be valid against this file — layer exists, target geometry is present,
expected block is present, etc. If a Require fails, the user's
assumption about which files this script applies to is wrong, and the
file selection or the script needs fixing.

You can chain multiple `Step` calls per file. Each is recorded independently.
</step-dsl>

<cross-file-state>
For batches that need to share state between files (e.g. "number every
viewframe sequentially across all drawings"):

```csharp
record ViewframeCounter { public int Next = 0; }

var counter = ctx.BatchState<ViewframeCounter>();   // same instance every file
foreach (var vf in xDb.GetViewframes(xTx))
{
    vf.UpgradeOpen();
    vf.Number = ++counter.Next;
}
```

`ctx.BatchState<T>()` returns the same instance of T for every file in
the run. First call default-constructs T. State is per-run only; a fresh
Run click gives a fresh state pool.
</cross-file-state>

<script-header>
Every saved batch script should start with three header lines:

```csharp
// @flavor: batch
// @name: telegram-style-name
// @summary: one-line description for the Manage Scripts list
```

These are parsed when the file is read back. Missing fields default
sensibly (folder flavor / file name / empty summary). The
`propose_script` tool writes the header automatically — you only supply
the body.
</script-header>

<workflow>
The exact ordered workflow for a typical task:

1. **READ EDITOR FIRST.** Read `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`
   via ordinary file tools. This is the live mirror of whatever is in the
   palette editor right now — including any edits the user typed since
   your last proposal. If you don't read this first, your `propose_script`
   call risks overwriting the user's in-progress changes.

2. **Sample one or two target drawings via REPL.** The active drawing is
   often NOT the right sample — the user typically points the BATCH
   palette at a folder + filename mask (e.g. `*_SHT.dwg` in some
   directory) and the target set is what matches the mask, not whatever
   happens to be open. Get the folder + mask via the
   `autocad_batch_get_selection` tool (see step 5 below), pick one or
   two representative `.dwg` paths, and **sideload** them via REPL to
   inspect:

   ```csharp
   using var sample = new Database(false, true);
   sample.ReadDwgFile(@"C:\drawings\house-12_SHT.dwg",
                      System.IO.FileShare.Read, true, "");
   using var tx = sample.TransactionManager.StartTransaction();
   var lt = (LayerTable)tx.GetObject(sample.LayerTableId, OpenMode.ForRead);
   var layers = new List<string>();
   foreach (ObjectId id in lt) layers.Add(((LayerTableRecord)tx.GetObject(id, OpenMode.ForRead)).Name);
   layers
   ```

   The assumption is that one or two drawings are representative of the
   whole batch. If they aren't, the Test phase catches it — that's what
   Test is for.

3. **Author or update the script.** Plan your edits relative to the
   editor's current content. Include the Step DSL chain(s). Keep the body
   focused on "what changes"; the runtime handles "for each file".

4. **Propose the script.**
   ```
   autocad_batch_propose_script(
       name = "set-layer-transparency",
       script_body = "...",
       input_summary = "set transparency=0 on layer X-FOO")
   ```
   If the user has dirty edits, they're prompted to confirm the replace.

5. **Get the folder selection.** Call
   ```
   autocad_batch_get_selection()
   ```
   Returns `{ folder, mask, recurse, files: [...] }` reflecting what the
   user has set in the BATCH palette right now. **You cannot change
   these** — only the user can, via the palette UI. If the user has
   instead pasted explicit paths into the conversation (e.g. "run on
   these three files: ..."), prefer those and tell the user to either
   match the selection in the palette or accept that Live will use
   whatever the palette shows.

6. **Run Test.**
   ```
   autocad_batch_run_test()                      # runs current editor buffer
   autocad_batch_run_test(name = "<saved-name>") # runs a saved script
   ```
   No-arg form runs whatever is in the live editor buffer right now (the
   common case after `propose_script`). The `name` form is only for
   re-running a previously saved script from the batch scripts folder.
   Returns immediately; the actual run is async.

7. **Wait for results.** Two viable approaches:

   a. **Poll** the `acd-mcp://batch-runs/last` resource until the run is
      complete.

   b. **Use the `Monitor` tool** (preferred when available) to watch the
      SafeBoundary log for the completion marker:
      ```
      Monitor command (PowerShell):
        Get-Content -Wait -Tail 0 "$env:LOCALAPPDATA\Acd.Mcp\log.txt" |
          Select-String "BATCH RUN COMPLETED <run_id>"
      ```
      The runtime writes an explicit `BATCH RUN COMPLETED <run_id>`
      line when the run finishes. Monitor fires when that line appears,
      so you wake up exactly once instead of polling.

   Then inspect per-file outcomes. Each file is either Pass or Failure;
   Failure carries the exception message and the step outcomes leading
   up to it.

8. **Iterate.** If any file failed, diagnose, fix the script body, go to
   step 1. (Yes, step 1 — the user may have made edits in the palette
   while you were polling.)

9. **Tell the user it's safe.** Once Test is green across every file,
   tell the user: "Script is clean. Flip the switch to Live and click
   Run when you're ready." Do not press for it; their click is the
   safety boundary.
</workflow>

<rules-the-agent-must-follow>
1. **Live execution is the user's click.** There is no
   `autocad_batch_run_live` tool. Don't ask for one; don't invent one.

2. **Always read the editor buffer before proposing.** Skipping this
   step is how you trample the user's in-flight changes.

3. **Use the Step DSL.** Don't write raw `try { ... } catch (Exception e)
   { ... }` around your mutations — the runtime already does that, and
   you'll just produce more noise.

4. **Don't write boilerplate.** No `new Database`, no `SaveAs`, no
   transaction lifecycle. The runtime owns those.

5. **Step names are telegram-style.** Short, descriptive, hyphenated:
   `set-layer-transparency`, not `SetLayerTransparencyForAllEntities`.

6. **`Require` predicates are arbitrary lambdas.** You write them
   inline. No baked-in helper library exists (intentionally — the user
   wants the runtime narrow).

7. **The user picks how the batch reacts to a file failure.** The BATCH
   palette has an "On failure" dropdown with two options:
   * **Abort** (default) — the run stops at the first failed file.
   * **Skip** — failed files are recorded as Failure but the loop
     continues to the next file.

   Don't ask the user to change this mid-run; describe both modes in
   your hand-off message so they pick deliberately. "File is locked by
   another writer" always aborts the whole batch regardless of the
   dropdown — that's a structural failure, not a per-file one.

8. **Test mode never commits.** Even if you call `xTx.Commit()` yourself
   (don't), the runtime's outer transaction is configured for rollback.
   Test mode is safe to retry.
</rules-the-agent-must-follow>

<file-locations>
Batch-specific paths (general folder layout is in `/acd-mcp:start`):

* Editor mirror (read before proposing):
  `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`

* Saved batch scripts (also readable as plain files):
  `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx`

* Batch-run history (persistent across plugin reloads):
  `%LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json`
</file-locations>

<example-full-script>
Canonical script layout — four sections in this order:

```
// @declarations    (header: @flavor, @name, @summary)
// Inputs           (constants and configuration)
// Code             (the Step DSL chain — the actual logic)
// Helpers          (helper methods, at the END of the file)
```

**Keep the script flat.** Helpers go at the bottom. Only extract a
nested helper when it actually de-duplicates two or more call sites —
a single-call-site helper just adds noise.

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
A failing per-file result looks like this (excerpt from
`acd-mcp://batch-runs/last`):

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

The Requirements list shows which preconditions passed; the
ErrorMessage shows what blew up inside Apply. Common AutoCAD failures
to recognise:

* `eLockViolation` — the script tried to write without `UpgradeOpen()`.
* `eWasOpenForRead` — same root cause; open for write first.
* `eInvalidLayer` — typo in the layer name.

In the example above the file is Failure because `Apply` threw. A
file is also Failure if any `Require` predicate returns false — Require
is a hard precondition (see `<step-dsl>`); a failed Require means the
script's assumptions don't match this file, and the user needs to
either narrow the file selection or fix the script.
</diagnosing-failures>

<what-NOT-to-do>
* Don't call `xTx.Commit()` from the body — let the runtime decide.
* Don't call `xDb.SaveAs(...)` — the runtime saves with `OriginalFileVersion`.
* Don't loop over files yourself — the runtime iterates.
* Don't wrap your body in `try { ... } catch { ... }` — the runtime catches.
* Don't read `Application.DocumentManager` — there's no active doc in batch
  mode; you'd get a compile error if you tried.
* Don't bake a "helper extensions" library into the body — declare what
  you need inline. Roslyn scripting supports it.
* Don't write a 5-line script just because it "feels small enough" —
  there is no line-count guidance. Write what the task needs.
* Don't ever say "I'll just trigger Live for you." You can't.
</what-NOT-to-do>
