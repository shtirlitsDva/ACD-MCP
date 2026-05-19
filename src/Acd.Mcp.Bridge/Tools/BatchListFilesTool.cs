using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: read the BATCH palette's current folder + filename mask +
    // recurse flag and the resulting expanded file list.
    //
    // This is the agent's view of "what would run RIGHT NOW if I called
    // autocad_batch_run_test against the current selection". The user owns
    // these inputs — the agent cannot set them. Use the returned list to
    // sample one or two representative drawings via SCRIPT sideload before
    // proposing a script (see /acd-mcp:batch workflow step 2).
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = true   (queries UI state)
    //   Destructive = false
    //   Idempotent  = true   (snapshot of palette state; same inputs => same output)
    //   OpenWorld   = true   (UI state is part of the open world)
    [McpServerToolType]
    public sealed class BatchListFilesTool
    {
        private readonly AcadClient _client;

        public BatchListFilesTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_batch_list_files",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true),
         Description(
            "Return the BATCH palette's current folder + mask + expanded .dwg file list: " +
            "{ folder, mask, recurse, files: [...], count }. " +
            "The agent uses this to know exactly which files autocad_batch_run_test would operate on right " +
            "now, to pick representative samples for sideload inspection, and to confirm the user has set " +
            "the right folder + mask before kicking off a Test run. The agent cannot change the file list — " +
            "only the user can, via the palette UI. If the user instead pasted explicit paths into the " +
            "conversation, prefer those and tell the user to match the folder + mask in the palette.")]
        public async Task<BatchFilesResult> ListFilesAsync(CancellationToken ct = default)
        {
            try
            {
                var p = await _client.CallAsync<BatchFilesPayload>("batch.listFiles",
                    @params: null, ct).ConfigureAwait(false);
                return new BatchFilesResult(
                    ok: true, error_code: null, error_message: null,
                    p.folder, p.mask, p.recurse, p.files, p.count);
            }
            catch (AcadRpcException ex)
            {
                // G4: typical failure here is "BATCH palette is not open" —
                // surface it on the success path so the SDK doesn't strip it
                // into a generic invocation-error string.
                return new BatchFilesResult(
                    ok: false,
                    error_code: ex.Code.ToString(CultureInfo.InvariantCulture),
                    error_message: ex.Message,
                    folder: null, mask: null, recurse: null, files: null, count: null);
            }
            catch (AcadTransportException ex)
            {
                return new BatchFilesResult(
                    ok: false,
                    error_code: ex.ErrorCode,
                    error_message: ex.Message,
                    folder: null, mask: null, recurse: null, files: null, count: null);
            }
        }
    }

    // Plugin wire shape for batch.listFiles. Bridge wraps this in
    // BatchFilesResult on the success path.
    internal sealed record BatchFilesPayload(
        string folder,
        string mask,
        bool recurse,
        string[] files,
        int count);

    public sealed record BatchFilesResult(
        bool ok,
        string? error_code,
        string? error_message,
        string? folder,
        string? mask,
        bool? recurse,
        string[]? files,
        int? count);
}
