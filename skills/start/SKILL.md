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

The full `Autodesk.AutoCAD.*` namespace tree is imported (`ApplicationServices`, `DatabaseServices`, `Geometry`, `EditorInput`, `Runtime`). Top-level declarations persist between calls — `var x = 5` typed now is still in scope next call.

**Lock + transaction pattern** — the executor already wraps your snippet in `Doc.LockDocument()`. You still open transactions yourself:

```csharp
using var tx = Db.TransactionManager.StartTransaction();
var bt = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
// ... read or write ...
tx.Commit();
```

**Return small, well-shaped values.** The MCP serializes your snippet's return value via the DTO graph. See `<serialization-etiquette>`.
</repl-conventions>

<serialization-etiquette>
REPL replies contain `returnValueJson` — JSON produced by a DTO-driven serializer. The serializer projects each value through a registered DTO; unknown types emit the marker `{"$unsupported":"FullTypeName"}`.

To get tidy, useful replies:

* **Anonymous-projection at the leaf.** `new { layer = e.Layer, color = e.Color.ColorIndex }` always works, no DTO needed.
* **Don't return whole `Entity` instances** unless a DTO exists. Project them.
* **Primitives + geometry types are pre-DTO'd.** `int`, `double`, `string`, `Point2d`, `Point3d`, `Vector3d`, `Extents3d`, `ObjectId`, `Handle` — return them directly.
* **For block attributes / PropertySets / XData, use `Acd.DataProvider.ReadAll(entity)`** — returns `IReadOnlyDictionary<string, string>` with the union of all metadata mechanisms. Reading any one of them by hand will miss the others for users who store metadata differently than you assumed.
* **When you see `{"$unsupported":"..."}`** — that's the signal to write a DTO. Hand the type to `/acd-mcp:add-dto`.

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

1. Call `autocad_execute_csharp("Doc.Name")` — confirms the pipe is open and a drawing is loaded. If this fails with "BATCH palette is not open" or similar, the user needs to run `ACDMCP_START` (and `ACDMCP_PALETTE` if they want the in-AutoCAD UI).
2. If you'll touch entity metadata, probe `typeof(PropertyDataServices)` to confirm whether the AECC stack is loaded (Civil 3D / Map 3D / MEP). On vanilla AutoCAD, PropertySets are unavailable and `Acd.DataProvider` will return only block-attribute + XData data.

Never guess at what's available — verify.
</initial-checks>
