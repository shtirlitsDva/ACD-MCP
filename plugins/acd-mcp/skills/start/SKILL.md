---
name: start
description: Brief Claude on the ACD-MCP plugin — what it does, how to call it, and which sibling skill is mandatory for the task. Auto-loads when the user mentions AutoCAD, Civil 3D, the C# script REPL inside AutoCAD, batch DWG edits, or asks how to use this MCP. Invoke explicitly with /acd-mcp:start at the top of an AutoCAD session.
when_to_use: User mentions AutoCAD, Civil 3D, the live script REPL inside AutoCAD, batch edits across many .dwg files, the BATCH palette or SCRIPT palette, "the MCP", `autocad_script_execute`, or asks what this plugin can do. Also use as a refresher when starting a new turn that involves the plugin and earlier briefing has scrolled out.
---

<what-this-plugin-is>
**ACD-MCP** is a Model Context Protocol server that runs a **live C# script session inside a running AutoCAD 2025+ process**, plus a **multi-file batch runner** for applying the same script across a folder of `.dwg` files.

Architecture: a stdio bridge (`Acd.Mcp.Bridge.exe`, run by the MCP client) talks over a named pipe to a plugin (`Acd.Mcp.dll`) loaded inside AutoCAD. Each script call compiles via Roslyn `CSharpScript`, runs on AutoCAD's main thread under `Doc.LockDocument()`, and returns. State persists between calls. Batch runs iterate `Database` objects loaded side-band — no active document.

The user clicks `ACDMCP_START` (or it's autoloaded) to open the pipe. The user opens `ACDMCP_PALETTE` to get the in-AutoCAD SCRIPT/BATCH tabs. **You cannot start either; you can only check via the MCP tools whether they're running.**
</what-this-plugin-is>

<two-modes>
1. **SCRIPT** — single-drawing operations against the active document. Two tools (`autocad_script_execute`, `autocad_script_propose`). See sibling **`/acd-mcp:script`** for the full surface, conventions, return-value serialization, propose-vs-execute decision rule, and the staging-model contract.

2. **BATCH** — multi-file edits across many `.dwg` files in a folder. Three tools (`autocad_batch_propose_script`, `autocad_batch_run_test`, `autocad_batch_get_selection`) + the `acd-mcp://batch-runs/last` resource. See sibling **`/acd-mcp:batch`** for the full surface, Step DSL, Test→Live workflow, and the staging-model contract.
</two-modes>

<must-load-a-flavor-before-any-mcp-call>
**This is a hard rule, not a recommendation.** Before ANY plugin tool call, you MUST load the matching flavor skill:

* **Single-drawing operation** (inspect / modify / report on the drawing currently open) → load **`/acd-mcp:script`**.
* **Multi-drawing operation** (same change across many `.dwg` files in a folder) → load **`/acd-mcp:batch`**.

The active drawing is the tell: one drawing = SCRIPT, many drawings = BATCH. If the user's intent is ambiguous, **ASK before loading** — do not default to one flavor.

Loading is not optional. Each flavor skill carries rules that prevent silent failures: auto-return semantics, mirror-before-propose, discriminated response shapes, the `replaced_dirty` UX contract, return-value serialization etiquette. Calling MCP tools without the flavor skill loaded is how agents trip the same gotchas every session.

If you are mid-conversation and realize you've been calling tools without the right sibling loaded, STOP and load it now. Re-reading a few rules costs less than the wrong action.
</must-load-a-flavor-before-any-mcp-call>

<hard-rule-guessing-forbidden>
**GUESSING IS FORBIDDEN.** Before referencing any AutoCAD / Civil 3D type's property in code or in a DTO, verify it exists. Three acceptable verification paths, in this order of preference:

* **Probe the live type via SCRIPT** — `autocad_script_execute("typeof(T).GetProperties().Select(p => p.Name).ToList()")`. Authoritative.
* **Read the official Autodesk .NET API docs** — fetch via Context7 (`mcp__plugin_context7_context7__query-docs` with the AutoCAD Managed API library) or web search the exact class name. Use when the type isn't reachable from the active drawing.
* **Inspect an instance** — `someObj.GetType().GetProperties()...` when you already have a representative value.

A guessed property name silently fails to compile or returns wrong data. If after all three you still aren't sure, ask the user — don't invent.
</hard-rule-guessing-forbidden>

<initial-checks>
On first use in a session, sanity-check the surface:

1. **Pipe up?** Call `autocad_script_execute("Doc.Name")`. Confirms the pipe is open and a drawing is loaded.
   * Failure looks like `success: false` with `stderr` containing `"No AutoCAD instance found. ..."` or `"AutoCAD pipe unavailable: ..."`. The bridge couldn't connect to a plugin pipe.
   * Resolution: the user has to run **`ACDMCP_START`** inside AutoCAD to open the pipe. (Release builds. DEBUG / DevReload builds auto-start the pipe on first idle after `Initialize` — see the v20 idle hook in `McpPlugin.cs`. No `ACDMCP_START` typing needed there.)

2. **Palette up?** (Required for BATCH and for `autocad_script_propose`.) Call `autocad_batch_get_selection()` (cheapest probe — no side effects). Inspect the **discriminated response shape**:
   * `{ ok: true, folder, mask, files, ... }` — palette is open and routed.
   * `{ ok: false, error_code: "-32603", error_message: "BATCH palette is not open. Run ACDMCP_PALETTE inside AutoCAD first." }` — user needs to run **`ACDMCP_PALETTE`** inside AutoCAD.
   * The bridge never throws for this case (V2-G4): always read `result.ok` first, then `error_message` on failure. Same pattern for `autocad_batch_propose_script`, `autocad_batch_run_test`, `autocad_script_propose`. See the sibling skills' `<response-shape>` sections.

3. **AECC stack loaded?** If you'll touch entity metadata, probe:
   ```csharp
   AppDomain.CurrentDomain.GetAssemblies()
       .Select(a => a.GetName().Name)
       .Where(n => n != null && n.StartsWith("Aec"))
       .ToList()
   ```
   On Civil 3D 2025, expect to see `AecPropDataMgd` (where `PropertyDataServices` lives) and friends. On vanilla AutoCAD, no `Aec*` assemblies are loaded and `Acd.DataProvider` will return block-attribute data only.

Never guess at what's available — verify.
</initial-checks>

<file-locations>
| Purpose | Path |
|---|---|
| DTO system folder (plugin-owned, do not edit) | `%LOCALAPPDATA%\Acd.Mcp\dto-system\` |
| DTO user folder (yours and the user's) | `%APPDATA%\Acd.Mcp\dto-user\` |
| Saved SCRIPT scripts | `%APPDATA%\Acd.Mcp\scripts\script\<name>.csx` |
| Saved batch scripts | `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx` |
| SCRIPT editor mirror (read before `autocad_script_propose`) | `%LOCALAPPDATA%\Acd.Mcp\buffer-script.csx` |
| BATCH editor mirror (read before `autocad_batch_propose_script`) | `%LOCALAPPDATA%\Acd.Mcp\buffer-batch.csx` |
| Batch-run history | `%LOCALAPPDATA%\Acd.Mcp\batch-runs\<timestamp>_<run_id>.json` |
| Plugin diagnostic log | `%LOCALAPPDATA%\Acd.Mcp\log.txt` |
</file-locations>

<sibling-skills>
* **`/acd-mcp:script`** — full reference for the single-drawing surface. Conventions (Doc/Db/Ed globals, namespace imports, using-first, block-form `using`, trailing-expression-return + auto-return gotchas), return-value serialization etiquette, propose-vs-execute decision rule, mirror-before-propose rule, response shapes, staging model, replaced_dirty contract.
* **`/acd-mcp:batch`** — full reference for the multi-drawing surface. Step DSL, three globals (`xDb`/`xTx`/`ctx`), cross-file state, Test→Live workflow, response shapes, staging model, replaced_dirty contract.
* **`/acd-mcp:add-dto`** — write or override a DTO when serialization emits `{"$unsupported":"..."}` or when the default projection of a type is too thin. One-type-per-DTO-file rule lives there.
</sibling-skills>
