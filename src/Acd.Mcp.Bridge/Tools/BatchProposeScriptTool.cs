using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: save a batch-flavour script to the local store and push
    // it into the BATCH palette editor.
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = false   (writes %APPDATA%\Acd.Mcp\scripts\batch\<name>.csx)
    //   Destructive = false   (overwriting an existing script with the same
    //                          name is the documented behaviour; no entity
    //                          data is destroyed)
    //   Idempotent  = true    (same name + body → same on-disk file)
    //   OpenWorld   = true    (file system access)
    //
    // The agent MUST read editor-buffer.csx via ordinary file tools first
    // to capture any user edits, then plan its update against that content,
    // then call this tool. See SKILL.md for the full workflow.
    [McpServerToolType]
    public sealed class BatchProposeScriptTool
    {
        private readonly AcadClient _client;

        public BatchProposeScriptTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_batch_propose_script",
            ReadOnly = false,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true),
         Description(
            "Save a batch-flavour C# script to %APPDATA%\\Acd.Mcp\\scripts\\batch\\<name>.csx and push it " +
            "into the BATCH palette's live-shared editor. Before calling, READ %LOCALAPPDATA%\\Acd.Mcp\\" +
            "editor-buffer.csx via ordinary file tools to see the editor's current content and plan the " +
            "update against it (so you don't trample user edits). If the editor has dirty changes, the " +
            "user is prompted to confirm before your version replaces theirs. Same name overwrites the " +
            "existing saved script. See the acd-batch skill for the full workflow and script-body contract.")]
        public async Task<BatchProposeResult> ProposeAsync(
            [Description("Telegram-style name (lowercase, hyphenated, no filler). Used as both the saved filename and the run label.")]
            string name,
            [Description("The script BODY only — no `new Database(...)`, no transactions, no try/catch, no SaveAs. The runtime owns those. Use the Step DSL: ctx.Step(\"name\").Require(\"label\", () => predicate).Apply(() => { ...mutation...; return \"summary\"; });. Globals: xDb (Database), xTx (Transaction), ctx (IBatchContext).")]
            string script_body,
            [Description("Optional one-line summary, surfaced in the Manage Scripts window.")]
            string? input_summary = null,
            CancellationToken ct = default)
        {
            return await _client.CallAsync<BatchProposeResult>("batch.proposeScript",
                new { name, script_body, input_summary }, ct).ConfigureAwait(false);
        }
    }

    public sealed record BatchProposeResult(bool ok, string saved_as, string name);
}
