---
name: start
description: Brief Claude on the ACD-MCP plugin — what it does, how to call it, and which sibling skill to reach for. Auto-loads when the user mentions AutoCAD, Civil 3D, the C# REPL, batch DWG edits, or asks how to use this MCP. Invoke explicitly with /acd-mcp:start at the top of an AutoCAD session.
when_to_use: User mentions AutoCAD, Civil 3D, the live REPL inside AutoCAD, batch edits across many .dwg files, the BATCH palette, "the MCP", autocad_execute_csharp, or asks what this plugin can do. Also use as a refresher when starting a new turn that involves the plugin and earlier briefing has scrolled out.
---

<what-this-plugin-is>
**ACD-MCP** is a Model Context Protocol server that runs a **live C# REPL inside a running AutoCAD 2025+ process**, plus a **multi-file batch runner** for applying the same script across a folder of `.dwg` files.

Architecture: a stdio bridge (`Acd.Mcp.Bridge.exe`, run by the MCP client) talks over a named pipe to a plugin (`Acd.Mcp.dll`) loaded inside AutoCAD. Each REPL call compiles via Roslyn `CSharpScript`, runs on AutoCAD's main thread under `Doc.LockDocument()`, and returns. State persists between calls. Batch runs iterate `Database` objects loaded side-band — no active document.

The user clicks `ACDMCP_START` (or it's autoloaded) to open the pipe. The user opens `ACDMCP_PALETTE` to get the in-AutoCAD REPL/BATCH tabs. **You cannot start either; you can only check via the MCP tools whether they're running.**
</what-this-plugin-is>

<two-modes>
1. **REPL** — `autocad_execute_csharp(code, timeout_ms?)`. Run snippets against the active drawing. See `<repl-conventions>`.
2. **BATCH** — multi-file edits via the `autocad_batch_*` tools and `acd-mcp://batch-runs/last`. See sibling `/acd-mcp:batch` for the full workflow and rules.
</two-modes>

<repl-conventions>
Globals in scope for every `autocad_execute_csharp` call:

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

**Trailing-expression return.** If your last statement is a bare expression ending with `;` (e.g. `x * 10;`), the executor strips the trailing semicolon so the value becomes the submission's return value. Same convention as LINQPad or `dotnet-script`.

**Return small, well-shaped values.** The MCP serializes your snippet's return value via the DTO graph. See `<serialization-etiquette>`.

**`dynamic` is not available.** `Microsoft.CSharp` is not in the script's reference set, so `dynamic` produces a "missing compiler required member" error. For late-bound calls, use reflection (`Type.InvokeMember`, `GetProperty`, `GetMethod`) — the surface is verbose but always works.

**`Console.WriteLine` works.** The session captures stdout/stderr into the response. Use for ad-hoc tracing; small returns are still preferred over print-debugging for structured data.

**`timeout_ms` is cooperative.** A tight loop that doesn't observe its CancellationToken blocks AutoCAD's main thread for the full duration. Treat the parameter as a soft hint, not a hard kill switch.
</repl-conventions>

<serialization-etiquette>
REPL replies contain `returnValueJson` — JSON produced by a DTO-driven serializer. The serializer projects each value through a registered DTO; unknown types emit the marker `{"$unsupported":"FullTypeName"}`.

To get tidy, useful replies:

* **Anonymous-projection at the leaf.** `new { layer = e.Layer, color = e.Color.ColorIndex }` always works, no DTO needed.
* **Don't return whole `Entity` instances** unless a DTO exists. Project them.
* **Primitives + geometry types are pre-DTO'd.** `int`, `double`, `string`, `Point2d`, `Point3d`, `Vector3d`, `Extents3d`, `ObjectId`, `Handle` — return them directly.
* **For block attributes / PropertySets, use `Acd.DataProvider.ReadAll(entity)`** — returns `IReadOnlyDictionary<string, string>` with the union of every registered metadata mechanism. Reading any one mechanism by hand misses the others for users who store metadata differently than you assumed. On vanilla AutoCAD the union is block-attributes-only; on Civil 3D / Map / MEP it also includes PropertySets. XData is intentionally not in the composite yet — track the issue rather than reading it by hand.
* **When you see `{"$unsupported":"..."}`** — that's the signal to write a DTO. Hand the type to `/acd-mcp:add-dto`.
* **When you see `{"$serialization_error":"..."}`** — the value couldn't be serialised (commonly: a return reference whose owning Transaction has been disposed). Re-run the snippet so the value is freshly built inside the active transaction, or project to primitives at the leaf.

The serializer applies `JsonNamingPolicy.SnakeCaseLower` to property names. Either Pascal or snake_case in the projection is fine — they normalize.
</serialization-etiquette>

<hard-rules>
1. **GUESSING IS FORBIDDEN.** Before referencing any AutoCAD type's property in REPL code or in a DTO, verify it exists. Three acceptable verification paths, in this order of preference:
   * **Probe the live type via REPL** — `typeof(T).GetProperties().Select(p => p.Name).ToList()`. Authoritative.
   * **Read the official Autodesk .NET API docs** — fetch via Context7 (`mcp__plugin_context7_context7__query-docs` with the AutoCAD Managed API library) or web search the exact class name. Use when the type isn't reachable from the active drawing.
   * **Inspect an instance** — `someObj.GetType().GetProperties()...` when you already have a representative value.

   A guessed property name silently fails to compile or returns wrong data. If after all three you still aren't sure, ask the user — don't invent.

2. **One type per DTO file.** `circle.csx` registers `Circle` only. Multi-type files break per-type override. See `/acd-mcp:add-dto`.

Batch-specific rules live in `/acd-mcp:batch` — load that skill before doing anything in BATCH mode.
</hard-rules>

<file-locations>
| Purpose | Path |
|---|---|
| DTO system folder (plugin-owned, do not edit) | `%LOCALAPPDATA%\Acd.Mcp\dto-system\` |
| DTO user folder (yours and the user's) | `%APPDATA%\Acd.Mcp\dto-user\` |
| Saved REPL scripts | `%APPDATA%\Acd.Mcp\scripts\repl\<name>.csx` |
| Saved batch scripts | `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx` |
| Live editor mirror (read before proposing) | `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx` |
| Batch-run history | `%LOCALAPPDATA%\Acd.Mcp\batch-runs\<timestamp>_<run_id>.json` |
| Plugin diagnostic log | `%LOCALAPPDATA%\Acd.Mcp\log.txt` |
</file-locations>

<sibling-skills>
* **`/acd-mcp:batch`** — the full workflow for authoring, Test-iterating, and handing off batch scripts. Use whenever the user wants to apply a change across a folder of `.dwg` files.
* **`/acd-mcp:add-dto`** — write or override a DTO when REPL emits `{"$unsupported":"..."}` or when the default projection of a type is too thin.
</sibling-skills>

<initial-checks>
On first use in a session, sanity-check the surface:

1. Call `autocad_execute_csharp("Doc.Name")` — confirms the pipe is open and a drawing is loaded. If the call returns "AutoCAD pipe unavailable: ..." or "BATCH palette is not open", the user has to run **`ACDMCP_START`** inside AutoCAD to open the pipe. Opening the BATCH palette alone is NOT enough — `ACDMCP_START` is the listener-start command.
2. If you'll need the BATCH workflow, also have the user run `ACDMCP_PALETTE` to bring up the REPL/BATCH palette.
3. If you'll touch entity metadata, probe whether the AECC stack is loaded:
   ```csharp
   AppDomain.CurrentDomain.GetAssemblies()
       .Select(a => a.GetName().Name)
       .Where(n => n != null && n.StartsWith("Aec"))
       .ToList()
   ```
   On Civil 3D 2025, expect to see `AecPropDataMgd` (where `PropertyDataServices` lives) and friends. On vanilla AutoCAD, no `Aec*` assemblies are loaded and `Acd.DataProvider` will return block-attribute data only.

Never guess at what's available — verify.
</initial-checks>
