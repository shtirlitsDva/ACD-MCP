using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: kick off a Test-mode batch run of a previously proposed
    // script against the BATCH palette's currently-selected folder + mask.
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = true    (test mode never mutates files; rollback is
    //                          guaranteed by the runtime; intent matches "I
    //                          want to see what would happen")
    //   Destructive = false
    //   Idempotent  = false   (state may differ between runs depending on
    //                          drawing contents at test time)
    //   OpenWorld   = true    (touches the file system to read drawings)
    //
    // **There is no `autocad_batch_run_live`.** Live execution requires the
    // user to flip the slide-switch to Live and click Run, in person. The
    // agent literally cannot trigger Live mode through the bridge.
    [McpServerToolType]
    public sealed class BatchRunTestTool
    {
        private readonly AcadClient _client;

        public BatchRunTestTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_batch_run_test",
            ReadOnly = true,
            Destructive = false,
            Idempotent = false,
            OpenWorld = true),
         Description(
            "Run a batch script in TEST mode against the BATCH palette's currently-selected folder + mask. " +
            "With NO argument, runs whatever is currently in the live BATCH editor buffer (the common case " +
            "right after autocad_batch_propose_script). With a `name` argument, loads that saved script into " +
            "the editor first and then runs it. Test mode opens each drawing read-shared, runs the script body " +
            "inside a transaction, then rolls back — no file is modified. The run id and a results resource URI " +
            "are returned; poll acd-mcp://batch-runs/last for the completed report, OR use the Monitor tool to " +
            "watch %LOCALAPPDATA%\\Acd.Mcp\\log.txt for the line 'BATCH RUN COMPLETED <run_id>'. " +
            "Live execution is intentionally NOT exposed as a tool — the user must flip the slide-switch " +
            "to Live and click Run in person; the runtime auto-runs a Test pass first and refuses Live " +
            "unless every Test file passed.")]
        public async Task<BatchRunStartedResult> RunTestAsync(
            [Description("Optional. Saved-script name to load into the editor and run. If omitted, runs whatever the BATCH editor buffer currently holds (the path autocad_batch_propose_script just populated).")]
            string? name = null,
            CancellationToken ct = default)
        {
            try
            {
                var p = await _client.CallAsync<BatchRunStartedPayload>("batch.runTest",
                    new { name }, ct).ConfigureAwait(false);
                return new BatchRunStartedResult(
                    ok: true, error_code: null, error_message: null,
                    p.run_id, p.pending, p.results_resource, p.note);
            }
            catch (AcadRpcException ex)
            {
                // G4: the MCP SDK was wrapping thrown messages into a generic
                // "An error occurred invoking ..." string at the client. Carry
                // the plugin-side message on the success path instead, so the
                // agent reliably sees e.g. "No files are currently selected
                // in the BATCH palette." and can recover.
                return new BatchRunStartedResult(
                    ok: false,
                    error_code: ex.Code.ToString(CultureInfo.InvariantCulture),
                    error_message: ex.Message,
                    run_id: null, pending: null, results_resource: null, note: null);
            }
            catch (AcadTransportException ex)
            {
                return new BatchRunStartedResult(
                    ok: false,
                    error_code: ex.ErrorCode,
                    error_message: ex.Message,
                    run_id: null, pending: null, results_resource: null, note: null);
            }
        }
    }

    // Plugin wire shape for batch.runTest. Bridge wraps this in
    // BatchRunStartedResult on the success path.
    internal sealed record BatchRunStartedPayload(
        string run_id, bool pending, string results_resource, string note);

    public sealed record BatchRunStartedResult(
        bool ok,
        string? error_code,
        string? error_message,
        string? run_id,
        bool? pending,
        string? results_resource,
        string? note);
}
