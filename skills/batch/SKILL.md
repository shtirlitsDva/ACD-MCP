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

Outcomes per step:

* All `Require` predicates passed AND `Apply` returned normally → `Pass`
  with the summary string. The file is eligible for commit (in Live mode).
* Any `Require` predicate returned false → `Skipped`. `Apply` did NOT run.
  The step is NOT a failure (the script intentionally bailed). Pass.
* Any predicate or `Apply` threw → `Failure`. No commit. The runtime
  marks the file failed and continues to the next file.

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

2. **Explore the active drawing via REPL.** Use `autocad_execute_csharp`
   to inspect what layers/blocks/objects exist. This is the only way to
   verify your assumptions about the data shape before authoring a batch
   script.

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

5. **Confirm folder selection.** Ask the user (or read
   `batch.getSelection` via a tool path if exposed) whether the BATCH
   palette has the right folder + mask + recurse flag. You cannot set
   this — only the user can.

6. **Run Test.**
   ```
   autocad_batch_run_test(name = "set-layer-transparency")
   ```
   Returns immediately with a placeholder result; the actual run is async.

7. **Poll for results.**
   ```
   acd-mcp://batch-runs/last
   ```
   Read until the run is completed. Inspect per-file outcomes. Each file
   is either Pass or Failure; Failure carries the exception message and
   the step outcomes leading up to it.

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

7. **Failures abort that file, not the batch.** The loop continues to
   the next file. The exception is "file is locked by another writer" —
   that aborts the whole batch immediately. No graceful skip.

8. **Test mode never commits.** Even if you call `xTx.Commit()` yourself
   (don't), the runtime's outer transaction is configured for rollback.
   Test mode is safe to retry.
</rules-the-agent-must-follow>

<file-locations>
The agent should know:

* Editor mirror (read before proposing):
  `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`

* Saved batch scripts (also readable as plain files):
  `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx`

* Saved REPL scripts (out of scope here, but the folder exists):
  `%APPDATA%\Acd.Mcp\scripts\repl\<name>.csx`

* Batch-run history (persistent across plugin reloads):
  `%LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json`
</file-locations>

<example-full-script>
Reproducing the spec's canonical example — set transparency to 0 for all
entities on layer X-FOOBAR:

```csharp
// @flavor: batch
// @name: set-layer-transparency-zero
// @summary: set transparency to 0 for all entities on layer X-FOOBAR

// ─── inputs ─────────────────────────────────────────────
var TARGET_LAYER = "X-FOOBAR";
var TRANSPARENCY = (byte)0;

// ─── helpers (optional — inline) ────────────────────────
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

// ─── step ──────────────────────────────────────────────
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

A `Skipped` step is NOT a failure — it means a Require predicate
returned false. The file's status is Pass when all steps are Pass or
Skipped.
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
