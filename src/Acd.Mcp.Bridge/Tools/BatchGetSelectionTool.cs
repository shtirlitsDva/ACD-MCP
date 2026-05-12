using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: read the BATCH palette's current folder + filename mask +
    // recurse flag and the resulting expanded file list.
    //
    // This is the agent's view of "what would run RIGHT NOW if I called
    // autocad_batch_run_test against the current selection". The user owns
    // these inputs — the agent cannot set them. Use the returned list to
    // sample one or two representative drawings via REPL sideload before
    // proposing a script (see /acd-mcp:batch workflow step 2).
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = true   (queries UI state)
    //   Destructive = false
    //   Idempotent  = true   (snapshot of palette state; same inputs => same output)
    //   OpenWorld   = true   (UI state is part of the open world)
    [McpServerToolType]
    public sealed class BatchGetSelectionTool
    {
        private readonly AcadClient _client;

        public BatchGetSelectionTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_batch_get_selection",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true),
         Description(
            "Return the BATCH palette's current selection: { folder, mask, recurse, files: [...], count }. " +
            "The agent uses this to know exactly which files autocad_batch_run_test would operate on right " +
            "now, to pick representative samples for sideload inspection, and to confirm the user has set " +
            "the right folder + mask before kicking off a Test run. The agent cannot change the selection — " +
            "only the user can, via the palette UI. If the user instead pasted explicit paths into the " +
            "conversation, prefer those and tell the user to match the selection in the palette.")]
        public async Task<BatchSelectionResult> GetSelectionAsync(CancellationToken ct = default)
        {
            try
            {
                return await _client.CallAsync<BatchSelectionResult>("batch.getSelection",
                    @params: null, ct).ConfigureAwait(false);
            }
            catch (AcadRpcException ex)
            {
                // Typical failure here is "BATCH palette is not open" — the
                // agent needs to read that message and tell the user to
                // open the palette first.
                throw new McpException(ex.Message);
            }
        }
    }

    public sealed record BatchSelectionResult(
        string folder,
        string mask,
        bool recurse,
        string[] files,
        int count);
}
