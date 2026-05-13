using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: save a script-flavour body to the local store and
    // stage it in the SCRIPT palette tab's editor for the user to
    // review and (optionally) edit before running.
    //
    // NOTE: this is the OPTIONAL path. To DO user work, prefer the
    // direct autocad_script_execute tool — it doesn't touch the editor
    // and is safe to call repeatedly while the user reviews a proposal.
    // Use propose only when the user has asked to review/edit the
    // script, or when you're iterating on a longer script you want
    // them to keep.
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = false   (writes %APPDATA%\Acd.Mcp\scripts\script\<name>.csx)
    //   Destructive = false   (overwriting an existing script with the same
    //                          name is the documented behaviour)
    //   Idempotent  = true    (same name + body → same on-disk file)
    //   OpenWorld   = true    (file system access)
    //
    // The agent MUST read buffer-script.csx via ordinary file tools
    // first to capture any user edits, then plan the update against
    // that content. See the acd-mcp:script skill for the full workflow.
    [McpServerToolType]
    public sealed class ScriptProposeTool
    {
        private readonly AcadClient _client;

        public ScriptProposeTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_script_propose",
            ReadOnly = false,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true),
         Description(
            "Save a single-drawing C# script to %APPDATA%\\Acd.Mcp\\scripts\\script\\<name>.csx and stage " +
            "it in the SCRIPT palette tab's editor for the user to review. Before calling, READ " +
            "%LOCALAPPDATA%\\Acd.Mcp\\buffer-script.csx via ordinary file tools to see the editor's current " +
            "content and plan the update against it (so you don't trample user edits). If the editor has " +
            "dirty changes, the user is prompted to confirm before your version replaces theirs. Use this " +
            "tool only when the user wants to review/edit a script before running — for ad-hoc execution " +
            "and information gathering, call autocad_script_execute directly (it doesn't touch the editor). " +
            "Same name overwrites the existing saved script. See the acd-mcp:script skill for the workflow.")]
        public async Task<ProposeScriptResult> ProposeAsync(
            [Description("Telegram-style name (lowercase, hyphenated, no filler). Used as the saved filename.")]
            string name,
            [Description("The full script body — top-level C# statements, `using` directives at the top, block-form `using (var tx = ...) { ... }` for disposables. Globals: Doc, Db, Ed, CivilDoc, Acd. See acd-mcp:script for conventions.")]
            string script_body,
            [Description("Optional one-line summary, surfaced in the Manage Scripts window.")]
            string? input_summary = null,
            CancellationToken ct = default)
        {
            try
            {
                return await _client.CallAsync<ProposeScriptResult>("script.proposeScript",
                    new { name, script_body, input_summary }, ct).ConfigureAwait(false);
            }
            catch (AcadRpcException ex)
            {
                // G4: never throw — carry the plugin-side message on the
                // success path so the agent reliably sees it (the MCP SDK
                // strips thrown messages into a generic invocation-error).
                // Same reasoning as BatchProposeScriptTool.
                return new ProposeScriptResult(
                    ok: false,
                    error_code: ex.Code.ToString(CultureInfo.InvariantCulture),
                    error_message: ex.Message,
                    saved_as: null, name: null, replaced_dirty: null);
            }
        }
    }
}
