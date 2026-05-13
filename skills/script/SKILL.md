---
name: script
description: |
  Full reference for the single-drawing C# script surface of the
  ACD-MCP plugin. Covers conventions (Doc/Db/Ed/CivilDoc globals,
  imports, using-first, block-form using, trailing-expression return
  with auto-return gotchas), return-value serialization etiquette,
  direct-execute vs propose-to-editor decision rule, mirror-before-
  propose rule, discriminated response shapes, staging model, and
  the replaced_dirty UX contract. MUST be loaded before any single-
  drawing MCP call.
when_to_use: User wants to inspect, modify, or report on the drawing currently open in AutoCAD — anything that maps to "one active drawing". Includes ad-hoc information gathering, one-shot edits, longer scripts the user wants to review before running, and iterative script development. Do NOT use for multi-drawing folder operations (those load `/acd-mcp:batch`).
---

<what-this-skill-is-for>
This skill is the full reference for the **single-drawing C# script surface** of the ACD-MCP plugin. Two MCP tools live here:

1. **`autocad_script_execute(code, timeout_ms?)`** — runs the snippet immediately against the active drawing. Returns the result. Does NOT touch the palette editor. The user doesn't see the script unless they look at the execution log. This is the default for everything: information gathering, one-shot edits, anything the user hasn't asked to review.

2. **`autocad_script_propose(name, script_body, input_summary?)`** — saves the script to `%APPDATA%\Acd.Mcp\scripts\script\<name>.csx` AND stages it in the SCRIPT palette editor for the user. The user reviews, edits if they want, then clicks Run. The script is also kept on disk so the user can re-load it later via the Manage Scripts window.

The two tracks are independent. You can run direct-execute queries to gather information (layer names, object types, …) **while** a proposed script sits in the editor waiting for the user — direct execute does not touch the editor, so the user's review is undisturbed.
</what-this-skill-is-for>

<script-conventions>
Globals in scope for every `autocad_script_execute` call:

* `Doc` — `Autodesk.AutoCAD.ApplicationServices.Document` (active doc)
* `Db`  — `Doc.Database`
* `Ed`  — `Doc.Editor`
* `CivilDoc` — `Autodesk.Civil.ApplicationServices.CivilDocument` (null when the active drawing isn't a Civil 3D / Map / MEP drawing — guard or wrap in try/catch)
* `Acd` — read-only metadata façade. Today exposes `Acd.DataProvider.ReadAll(entity)` / `Acd.DataProvider.TryRead(entity, key)`. See `<serialization-etiquette>`.

Imported namespaces (you can use unqualified type names from these):

* `System`, `System.Collections.Generic`, `System.Linq`, `System.IO`, `System.Text`
* `Autodesk.AutoCAD.ApplicationServices`, `Autodesk.AutoCAD.DatabaseServices`, `Autodesk.AutoCAD.Geometry`, `Autodesk.AutoCAD.EditorInput`, `Autodesk.AutoCAD.Runtime`

**Civil 3D namespaces are NOT in the default imports.** `Autodesk.Civil.DatabaseServices.Entity` collides with `Autodesk.AutoCAD.DatabaseServices.Entity`. If you need the Civil surface, add an explicit `using` at the top of your submission:

```csharp
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Autodesk.Civil.DatabaseServices;  // adds Alignment, Surface, Pipe, etc.
```

Top-level declarations persist between calls — `var x = 5` typed now is still in scope next call.

**`using` directives go FIRST.** All `using` statements (namespace imports AND `using` aliases) must appear before any other statement in the submission. They cannot be mixed in after `var foo = ...;` declarations.

**Lock + transaction pattern** — the executor already wraps your snippet in `Doc.LockDocument()`. You still open transactions yourself. The canonical form is the **block-form** `using` statement:

```csharp
using (var tx = Db.TransactionManager.StartTransaction())
{
    var bt = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
    // ... read or write ...
    tx.Commit();
}
```

Avoid `using var tx = ...;` at submission top level — Roslyn parses a top-level `using` as a directive (the namespace-import form), so `using var tx = ...;` is a CS1002 syntax error. Inside a method body (e.g. a helper you declared), `using var` works normally.

**`dynamic` is not available.** `Microsoft.CSharp` is not in the script's reference set, so `dynamic` produces a "missing compiler required member" error. For late-bound calls, use reflection (`Type.InvokeMember`, `GetProperty`, `GetMethod`) — the surface is verbose but always works.

**`Console.WriteLine` works.** The session captures stdout/stderr into the response. Use for ad-hoc tracing; small returns are still preferred over print-debugging for structured data.

**`timeout_ms` is cooperative.** A tight loop that doesn't observe its CancellationToken blocks AutoCAD's main thread for the full duration. Treat the parameter as a soft hint, not a hard kill switch.
</script-conventions>

<trailing-expression-return-and-auto-return-gotchas>
If your last statement is a bare expression ending with `;` (e.g. `x * 10;`), the executor auto-strips the trailing semicolon so the value becomes the submission's return value. Same convention as LINQPad / `dotnet-script`. The rule: **if the trailing expression is NOT legal as a C# statement, the semicolon is stripped and the value is returned. If it IS legal as a statement, the submission is treated as a void statement (no return value).**

The C#-spec set of "legal as a statement" expressions decides the cutoff. You need to know which side of that line your expression lands on, because the symmetry is not obvious:

| Snippet | Returned? | Why |
|---|---|---|
| `42;` | **yes** | literal — not a legal statement |
| `x * 10;` | **yes** | binary expr — not a legal statement |
| `Doc.Name;` | **yes** | member access — not a legal statement |
| `new { foo = 1 };` | **yes** | anonymous object — not a legal statement |
| `new int[] { 1,2,3 };` | **yes** | array creation — not a legal statement |
| `someMethod();` | **no** (intentionally) | invocation — legal statement (typical side-effect call) |
| `x = 5;` | **no** (intentionally) | assignment — legal statement |
| `await Task.Run(...);` | **no** (intentionally) | await — legal statement |

`new T(...);` (object creation) and `new T(...) { Prop = ... };` (object initializer) are also legal expression statements, and **DO get auto-returned** by this executor — i.e. `new List<int> { 1, 2, 3 };` returns the list. The auto-return logic was tightened (V5) to treat object-creation expressions as value-producing in the REPL context, since "create an object and discard it" is almost never the intent inside a script submission.

If you need to express a side-effect-only constructor and discard the value, assign explicitly: `_ = new MyService();`.

**Practical rule:** if you want a value back, either end with an expression (no semicolon), end with a trailing-semicolon expression that the table above says returns, or write an explicit `return <expr>;` inside a `using (...)` block.
</trailing-expression-return-and-auto-return-gotchas>

<serialization-etiquette>
SCRIPT replies contain `returnValueJson` — JSON produced by a DTO-driven serializer. The serializer projects each value through a registered DTO; unknown Autodesk-namespaced types emit the marker `{"$unsupported":"FullTypeName"}`.

To get tidy, useful replies:

* **Anonymous-projection at the leaf.** `new { layer = e.Layer, color = e.Color.ColorIndex }` always works, no DTO needed.
* **Don't return whole `Entity` instances** unless a DTO exists. Project them.
* **Primitives + geometry types are pre-DTO'd.** `int`, `double`, `string`, `Point2d`, `Point3d`, `Vector3d`, `Extents3d`, `ObjectId`, `Handle` — return them directly.
* **Collections of DTO'd types work natively.** `List<Circle>`, `IEnumerable<Alignment>`, `Dictionary<string, Layer>` — each element flows back through DTO projection. Lazy enumerables (`entities.Where(...)`) work too — STJ iterates them.
* **For block attributes / PropertySets, use `Acd.DataProvider.ReadAll(entity)`** — returns `IReadOnlyDictionary<string, string>` with the union of every registered metadata mechanism. Reading any one mechanism by hand misses the others for users who store metadata differently than you assumed. On vanilla AutoCAD the union is block-attributes-only; on Civil 3D / Map / MEP it also includes PropertySets. XData is intentionally not in the composite yet — track the issue rather than reading it by hand.
* **When you see `{"$unsupported":"Autodesk.XXX.YYY"}`** — that's the signal to write a DTO. Hand the type to `/acd-mcp:add-dto`. The factory claims every `Autodesk.*` type, so anything in that namespace without a DTO surfaces the marker.
* **When you see `{"$serialization_error":"..."}`** — the value couldn't be serialised (commonly: a return reference whose owning Transaction has been disposed). Re-run the snippet so the value is freshly built inside the active transaction, or project to primitives at the leaf.

The serializer applies `JsonNamingPolicy.SnakeCaseLower` to property names. Either Pascal or snake_case in the projection is fine — they normalize.

`Infinity` / `-Infinity` / `NaN` are emitted as named string literals (`"Infinity"` etc.) rather than throwing on the floating-point edge case. Pattern-match on those names if you need to detect degeneracy.
</serialization-etiquette>

<when-to-propose-vs-execute-directly>
Default to direct execute (`autocad_script_execute`). Propose only when one of these holds:

* The user asks you to "show me the script before running" / "let me see what you'd do" / "save this as a script".
* You're iterating on a non-trivial script (>~30 lines, or one the user has expressed interest in keeping) and the user should sanity-check before each run.
* The script does something irreversible enough that running it without review would be reckless even with the UI's Reset button (e.g. mass property edits across many entities, file-system writes).

Do NOT propose for: "what's the active layer?", "how many lines are on layer FOO?", "what's the DTO shape for Civil Surface?". Those are information gathering — direct execute.

Do NOT propose just because the script is "clever". The user is busy. Propose when there's user-facing value in review, not when there's agent-facing aesthetic value in saving.
</when-to-propose-vs-execute-directly>

<the-two-track-workflow>
A canonical session that uses both tracks:

1. **User asks for something substantial** ("set transparency to 50% on every entity on layer X-FOOBAR in this drawing, but only if the layer has more than 10 entities").

2. **Gather info via direct execute.** Confirm the layer exists, count entities on it, sanity-check the user's assumptions:
   ```
   autocad_script_execute("""
   using (var tx = Db.TransactionManager.StartTransaction())
   {
       var lt = (LayerTable)tx.GetObject(Db.LayerTableId, OpenMode.ForRead);
       if (!lt.Has("X-FOOBAR")) return new { exists = false };
       var bt = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
       var ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
       int n = 0;
       foreach (ObjectId id in ms)
       {
           var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
           if (ent is not null && ent.Layer == "X-FOOBAR") n++;
       }
       return new { exists = true, count = n };
   }
   """)
   ```
   This DOES NOT touch the editor.

3. **Propose the script.** Read the SCRIPT mirror first (see `<read-mirror-before-proposing>`), incorporate any user edits you find, then:
   ```
   autocad_script_propose(
       name = "transparency-on-x-foobar",
       script_body = "...",
       input_summary = "set transparency=50 on layer X-FOOBAR (12 entities)")
   ```
   The response carries `replaced_dirty: true|false` — see `<replaced-dirty-contract>`.

4. **Tell the user it's staged.** "I've put the script in the SCRIPT editor — review and click Run when you're ready."

5. **If the user asks for changes**, GO TO STEP 3 — read the mirror first (the user may have hand-edited), then propose again.

6. **Live execution is the user's click.** You don't need to ask. The propose tool's job is done once the script is staged.
</the-two-track-workflow>

<read-mirror-before-proposing>
Before EVERY call to `autocad_script_propose`, read the live mirror file:

```
%LOCALAPPDATA%\Acd.Mcp\buffer-script.csx
```

This is what the user is currently looking at in the SCRIPT editor. Skipping this step means you may overwrite hand-edits the user has made since your last proposal.

**Mirror write semantics** (see `<the-staging-model>` for the propose-time side):

* When the **user is typing** in the WPF text-box, writes are debounced ~250 ms so a flurry of keystrokes doesn't translate into a flurry of disk writes.
* When an **agent propose** lands and is accepted (clean-editor inline promote, or user clicks Yes on the dirty-prompt), the mirror is **sync-flushed** — the file is on disk before `autocad_script_propose` returns or before the prompt-accept callback completes.

So: if your read directly follows the user's typing, there may be ≤250 ms of lag. If your read follows an agent propose, the mirror is already durable.

The flow is:

1. **Read the mirror** with ordinary file tools.
2. **Compare** what's there to what you last proposed. Differences are user edits.
3. **Plan your update** against the user's current content, not against your own last proposal.
4. **Call `autocad_script_propose`**.

If you can't read the mirror (file doesn't exist yet, permission error), the SCRIPT editor is empty / fresh — proceed with the proposal.
</read-mirror-before-proposing>

<response-shape>
Both `autocad_script_execute` and `autocad_script_propose` return discriminated success-shapes. Always check the discriminator before reading payload fields.

```
# autocad_script_execute
{ success: true,  stdout, stderr, return_value_repr, return_value_json,
  diagnostics: [], elapsed_ms }                                    # success
{ success: false, stderr, diagnostics: [...],
  return_value_repr: null, return_value_json: null,
  elapsed_ms }                                                     # compile error or runtime

# autocad_script_propose
{ ok: true,  saved_as, name, replaced_dirty,
  error_code: null, error_message: null }                          # success
{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  saved_as: null, name: null, replaced_dirty: null }               # failure
```

The bridge never throws on plugin-rejected failures — those would be stripped to a generic "An error occurred invoking ..." by the MCP SDK (see V2-G4 in `CRASH_TEST_V2_JOURNAL.md`). Instead the plugin's message travels on the success path in `error_message`. Typical `ok: false` case for `autocad_script_propose`: SCRIPT palette not open (`error_code: "-32603"`, `error_message: "SCRIPT palette is not open. Run ACDMCP_PALETTE inside AutoCAD first."`).

The same shape is shared with `autocad_batch_propose_script`, so the "check `ok` first" pattern transfers between flavours.
</response-shape>

<replaced-dirty-contract>
`replaced_dirty` (on the `autocad_script_propose` success path) is the agent's signal about what the user is about to experience:

* **`false` or `null`** — the editor was clean. The proposal was inline-promoted (see `<the-staging-model>`) — the editor's visible text and the mirror file already reflect the new body when this RPC returns. Nothing further to say to the user.

* **`true`** — the editor had unsaved typed edits AND the new body differs from what the user is currently editing. The user is being prompted (async dialog: "Replace your unsaved changes? Yes/No"). The user's Yes/No is NOT reported here — the RPC returns before the dialog resolves.

When you see `replaced_dirty: true`, **warn the user proactively in your reply**: "I'm proposing an updated version of the script — you'll see a prompt to replace your in-progress edits. Click Yes to take my version, or No to keep yours and I'll re-read it before my next proposal."
</replaced-dirty-contract>

<the-staging-model>
The disposition of a proposal depends on whether the editor has unsaved user edits at propose time (see V3-H3 in `CRASH_TEST_V3_JOURNAL.md`):

* **Editor is CLEAN (`IsDirty=false`)** — the common agent-iteration case. `ScriptEditor.ProposeFromAgent` **inline-promotes**: `CurrentText` becomes the saved body, the mirror file is sync-flushed (no debounce), no pending slot is set. The mirror is durable on disk **before** the RPC returns. `replaced_dirty: false`.

* **Editor is DIRTY (`IsDirty=true`)** — the user has typed edits we'd clobber. ProposeFromAgent **stages** the body in `PendingProposal`, CurrentText and the mirror stay at what the user is editing, and the UI prompts the user. On Yes → AcceptPending promotes pending → current (and sync-flushes the mirror). On No → DiscardPending leaves the user's text untouched. `replaced_dirty: true`.

**The mirror file always reflects what the user sees on screen.** Reading the mirror is honest — you never read your own last-proposed body back during the brief prompt window in the dirty case, and you read your own proposal immediately in the clean case (because that IS what the user sees now).
</the-staging-model>

<saved-script-header>
A SCRIPT script saved via propose has an auto-prepended header:

```
// @flavor: script
// @name: telegram-style-name
// @summary: one-line description for the Manage Scripts window
```

You only supply the body to `autocad_script_propose`; the header is prepended by the saved-script store. The header survives in the editor's display.
</saved-script-header>

<rules-the-agent-must-follow>
1. **Direct execute is the default.** Reach for `autocad_script_execute` first. Only escalate to `autocad_script_propose` when the user has asked to review, or when iterating on a substantial script.

2. **Always read the SCRIPT mirror before proposing.** Skipping this is how you trample user edits. The file is at `%LOCALAPPDATA%\Acd.Mcp\buffer-script.csx`.

3. **Live execution is the user's click.** There is no `autocad_script_run` tool. Don't ask for one.

4. **When `replaced_dirty: true`, warn the user.** They're about to see a dialog and need context.

5. **The two tracks are independent.** You can call `autocad_script_execute` to gather info while a proposed script sits in the editor — direct execute doesn't disturb the staged proposal.

6. **Telegram-style names.** Short, descriptive, hyphenated: `transparency-on-x-foobar`, not `SetTransparencyOnLayerXFoobar`.

7. **No guessing.** See `/acd-mcp:start` `<hard-rule-guessing-forbidden>`. Probe types before referencing properties.
</rules-the-agent-must-follow>

<file-locations>
SCRIPT-specific paths (the general folder layout is in `/acd-mcp:start` `<file-locations>`):

* Editor mirror (read before proposing): `%LOCALAPPDATA%\Acd.Mcp\buffer-script.csx`
* Saved SCRIPT scripts: `%APPDATA%\Acd.Mcp\scripts\script\<name>.csx`
</file-locations>

<sibling-skills>
* **`/acd-mcp:start`** — plugin-level briefing, MUST-LOAD rule, initial-checks (pipe + palette + AECC), global file-locations table, the hard "no guessing" rule.
* **`/acd-mcp:batch`** — full reference for the multi-drawing surface. Load instead of this skill when the user wants to apply the same change across many `.dwg` files.
* **`/acd-mcp:add-dto`** — write or override a DTO when serialization emits `{"$unsupported":"..."}`.
</sibling-skills>

<what-NOT-to-do>
* Don't propose for trivial one-shot queries. `autocad_script_execute` is right there and doesn't touch the user's editor.
* Don't propose without reading the mirror first.
* Don't call `autocad_script_propose` and `autocad_script_execute` with the SAME body to "make sure it ran". Propose stages; execute runs. They're separate verbs.
* Don't try to "click Run for the user." That's their action.
</what-NOT-to-do>
