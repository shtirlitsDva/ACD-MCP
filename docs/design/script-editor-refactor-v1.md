<title>
Script Editor refactor + REPL Manage Scripts + propose-to-REPL — Plan v1
</title>

<motivation>
Today the BATCH tab has a full "manage saved scripts" UX and an LLM-side
`autocad_batch_propose_script` tool that pushes a script into the BATCH
palette editor, with unsaved-edits race protection. The REPL tab has
neither. We want the same capability on the REPL side, without copy-
pasting the Batch implementation.

The driving user story (paraphrased):
  1. User asks the LLM to do something.
  2. LLM proposes a script — it lands in the REPL editor.
  3. To get specifics (layer names, object types, …), the LLM uses
     `autocad_execute_csharp` (direct run) — this DOES NOT touch the
     editor, so it's safe while the user is reviewing the proposed
     script.
  4. LLM refines the proposed script and re-proposes — but must not
     trample user edits that arrived since the last proposal.
  5. LLM is not obliged to always propose; it may just run scripts
     directly to do user work.

So the REPL needs propose + manage-scripts + dirty-edit protection,
parallel to BATCH but without batch-specific concepts (no Step DSL,
no Test/Live, no file iteration).
</motivation>

<deep-module-shape>
The shared concern between BATCH and REPL is a single deep module:
`ScriptEditor`. It owns five entangled things that today live scattered
across `BatchExecutor` / `EditorBuffer` / `SavedScriptStore`:

  * the saved-scripts store (filtered by flavor),
  * the live editor-buffer mirror file on disk,
  * the authoritative "current script text" slot,
  * the IsDirty flag (true after typing, false after Load/Propose),
  * the propose-vs-typing race resolution (event fired for the UI).

Public surface (intentionally narrow):

    class ScriptEditor (Acd.Mcp.Batch project — pure logic, testable)
        ScriptFlavor      Flavor
        SavedScriptStore  Store
        string            CurrentText
        bool              IsDirty
        event             ScriptProposed(saved, previousText)

        void              OnUserTyped(string text)
        void              LoadFromSaved(SavedScript s)
        SavedScript       ProposeFromAgent(name, body, summary?)

Implementation hides: mirror debounce timer, thread-safe slot access,
atomic save+mirror sequencing, store coupling, dirty bookkeeping.
Callers (MCP tool, manage-scripts UI, palette VM) never touch
`EditorBuffer` or `SavedScriptStore` directly.

One `ScriptEditor` instance per editor (BATCH and REPL each get their
own, with their own flavor and mirror path). `SavedScriptStore` is a
single shared instance — it's filesystem-backed and stateless.
</deep-module-shape>

<files-changed>

<core>
NEW    src/Acd.Mcp.Batch/ScriptEditor.cs
            The new deep module per <deep-module-shape>.

MOVE   src/Acd.Mcp/Batch/EditorBuffer.cs
       → src/Acd.Mcp.Batch/EditorBuffer.cs
            Pure I/O — belongs in the testable assembly next to
            SavedScriptStore. Namespace flips to Acd.Mcp.Batch.
            BatchExecutor is the only current caller.

EDIT   src/Acd.Mcp/Batch/BatchExecutor.cs
            Replace `Scripts` / `Editor` / `_currentScript` /
            `ProposeScript` / `OnEditorTextChanged` / `ScriptProposed`
            with delegation to an internally-owned ScriptEditor
            (Flavor=Batch). Existing public API preserved so
            BatchRpcHandler / BatchViewModel / ManageScriptsViewModel
            keep working unchanged. Run logic (StartRun, _activeCts,
            History) stays as-is.

NEW    src/Acd.Mcp.Batch.Tests/ScriptEditorTests.cs
            Cover: OnUserTyped sets dirty + updates current + writes
            mirror; LoadFromSaved clears dirty; ProposeFromAgent saves
            + fires event + clears dirty.
</core>

<plugin-wiring>
EDIT   src/Acd.Mcp/McpPlugin.cs
            TryEnsureCore creates one shared SavedScriptStore and two
            ScriptEditor instances (Batch + Repl). BatchExecutor
            receives its ScriptEditor via ctor. Repl ScriptEditor is
            handed to the palette set.
            ExtraRpcMethodHandler gets a `repl.*` branch.

EDIT   src/Acd.Mcp/Ui/ReplPaletteSet.cs
            New ctor param: `ScriptEditor replEditor`. Pass through
            to ReplControl.
</plugin-wiring>

<repl-ui>
EDIT   src/Acd.Mcp/Ui/ReplControl.xaml.cs
            Accept a ScriptEditor; pass through to ReplViewModel.

EDIT   src/Acd.Mcp/Ui/ReplControl.xaml
            Add a "Scripts…" button to the toolbar
            (next to Run / Reset / Clear).

EDIT   src/Acd.Mcp/Ui/ReplViewModel.cs
            - CurrentCode setter routes through _editor.OnUserTyped
              (so typing flips IsDirty + writes mirror).
            - Add IsDirty mirrored from _editor.
            - Subscribe to _editor.ScriptProposed; on event, prompt
              if dirty, otherwise silently accept (dispatcher-marshalled).
            - Add ScriptsCommand opening the shared Manage window.
            - Add public LoadSavedScript(SavedScript) used by the
              Manage window (prompt on dirty before replacing).
            - Implement the IManageScriptsTarget interface.
</repl-ui>

<shared-manage-window>
MOVE   src/Acd.Mcp/Batch/Ui/ManageScriptsWindow.xaml(.cs)
       → src/Acd.Mcp/Ui/ManageScripts/ManageScriptsWindow.xaml(.cs)
            Generalised:
              - takes ScriptEditor + IManageScriptsTarget;
              - title bound to "Manage Scripts — {Flavor}";
              - Save-As uses target.CurrentScriptText;
              - Load calls target.LoadSavedScript(saved).
            BatchViewModel and ReplViewModel both implement
            IManageScriptsTarget. Prompts.AskForString stays as-is
            (already flavor-neutral).
</shared-manage-window>

<mcp-tool-surface>
NEW    src/Acd.Mcp.Bridge/Tools/ReplProposeScriptTool.cs
            Sibling of BatchProposeScriptTool. Tool name:
            `autocad_repl_propose_script(name, script_body,
            input_summary?)`. Calls RPC `repl.proposeScript`.

NEW    src/Acd.Mcp/Repl/ReplRpcHandler.cs
            Mirrors BatchRpcHandler shape. Routes
            `repl.proposeScript` → replEditor.ProposeFromAgent(...).
            Returns the same shape as the Batch result
            (ok / saved_as / name / replaced_dirty).

(Result-DTO sharing between Batch and Repl propose tools — see
<open-question-4>; default is "don't share, duplicate".)
</mcp-tool-surface>

<docs-and-skills>
EDIT   skills/start/SKILL.md
            - Expand <repl-conventions> with a new
              <repl-script-proposal-workflow> block explaining the
              two-track model: direct execute for info gathering;
              propose for review/edit + iteration; read the REPL
              mirror first before re-proposing.
            - Add REPL mirror file row to <file-locations>.
            - Add /acd-mcp:repl to <sibling-skills>.

NEW    skills/repl/SKILL.md
            Sibling skill for the propose-to-REPL workflow.
            Modelled on skills/batch/SKILL.md but trimmed: no Step
            DSL, no Test/Live, no file iteration. Covers:
              - when to propose vs when to just run directly,
              - "read editor mirror before proposing" rule,
              - dirty-edit race contract,
              - meaning of `replaced_dirty` in the response.
</docs-and-skills>

</files-changed>

<explicitly-out-of-scope>
- Renaming the Acd.Mcp.Batch project despite hosting the shared
  ScriptEditor. Cosmetic; ripples through .csproj refs (rule 1, 7).
- Changing the existing batch mirror path `editor-buffer.csx`
  (preserves the published skill docs).
- Extracting a single `propose_script` MCP tool with a flavor arg.
  Two named tools have clearer Description fields for the agent.
- Any "save current REPL into history" feature.
</explicitly-out-of-scope>

<open-questions>

<open-question-1>
Mirror file naming.

  (a) Keep batch at  %LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx  (compat),
      add REPL at    %LOCALAPPDATA%\Acd.Mcp\repl-buffer.csx.
  (b) Rename both to  editor-buffer-batch.csx /
                      editor-buffer-repl.csx  for symmetry.

Recommend (a) — preserves existing published docs and any user/agent
muscle memory; small extra doc note covers the asymmetry.
</open-question-1>

<open-question-2>
Skill organisation.

  (a) New file  skills/repl/SKILL.md  mirroring  skills/batch/.
  (b) Extend the REPL section inside  skills/start/SKILL.md
      instead of a separate sibling.

Recommend (a) — symmetric with the existing batch skill and keeps
`start` short.
</open-question-2>

<open-question-3>
RPC handler placement.

  (a) Tiny `ReplRpcHandler` in its own file
      (mirrors BatchRpcHandler shape, slightly DRY).
  (b) Inline switch in McpPlugin.ExtraRpcMethodHandler
      (less code, couples plugin to RPC shape).

Recommend (a) — consistent with the batch handler; cheap and clear.
</open-question-3>

<open-question-4>
Tool-result DTO sharing.

  (a) Duplicate as `ReplProposeResult` next to `BatchProposeResult`.
  (b) Rename `BatchProposeResult` → `ProposeScriptResult` and share.

Recommend (a) — sharing buys little, touches a working surface
(rule 1, rule 7).
</open-question-4>

<open-question-5>
PR ordering.

  (a) Everything in one PR.
  (b) Two phases:
      1. Extract ScriptEditor + move EditorBuffer + refactor
         BatchExecutor + tests. Prove Batch still works. STOP.
      2. REPL window + propose tool + ReplRpcHandler + skills.
  (c) Just the REPL Manage Scripts window first (parallel copy of
      the batch window, no abstraction), then deeper refactor.

Recommend (b) — review each half independently; safe rollback after
phase 1.
</open-question-5>

</open-questions>
