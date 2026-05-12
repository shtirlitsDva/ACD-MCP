using System.ComponentModel;
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
            "Run a previously-proposed batch script in TEST mode against the BATCH palette's currently-" +
            "selected folder + mask. Test mode opens each drawing read-shared, runs the script body inside " +
            "a transaction, then rolls back — no file is modified. The run id and a results resource URI " +
            "are returned; poll acd-mcp://batch-runs/last for the completed report. " +
            "Live execution is intentionally NOT exposed as a tool — the user must flip the slide-switch " +
            "to Live and click Run in person; the runtime auto-runs a Test pass first and refuses Live " +
            "unless every Test file passed.")]
        public async Task<BatchRunStartedResult> RunTestAsync(
            [Description("The saved-script name to run (must have been proposed via autocad_batch_propose_script first).")]
            string name,
            CancellationToken ct = default)
        {
            return await _client.CallAsync<BatchRunStartedResult>("batch.runTest",
                new { name }, ct).ConfigureAwait(false);
        }
    }

    public sealed record BatchRunStartedResult(
        string run_id, bool pending, string results_resource, string note);
}
