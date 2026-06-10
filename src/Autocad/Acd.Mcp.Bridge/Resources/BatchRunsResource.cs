using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Resources
{
    // MCP resources for the agent's feedback loop.
    //
    // Three resource templates:
    //   acd-mcp://batch-runs/recent{?limit,offset}
    //   acd-mcp://batch-runs/{run_id}
    //   acd-mcp://batch-runs/last
    //
    // Pagination is mandatory per <feedback-loop>: /recent defaults to
    // limit=20, max=100. The history list grows unbounded across plugin
    // restarts; without pagination the agent's context would flood.
    //
    // The body is JSON text (TextResourceContents wrapping a UTF-8 JSON
    // string). The MCP SDK can deserialise a String return into a single
    // TextResourceContents; we serialise the JSON ourselves so the agent
    // sees pretty-printed structure.
    [McpServerResourceType]
    public sealed class BatchRunsResource
    {
        private readonly AcadClient _client;

        public BatchRunsResource(AcadClient client)
        {
            _client = client;
        }

        [McpServerResource(
            UriTemplate = "acd-mcp://batch-runs/recent{?limit,offset}",
            Name = "batch-runs-recent",
            MimeType = "application/json"),
         Description(
            "Paginated newest-first list of completed batch runs. Each entry has run id, " +
            "timestamps, mode, file count, pass/fail counts, cancellation flag, abort reason. " +
            "Default limit 20; max 100. Use after autocad_batch_run_test to poll for results.")]
        public async Task<string> RecentAsync(
            [Description("Page size. Default 20, max 100.")] int? limit = null,
            [Description("Skip-N. Default 0.")] int? offset = null,
            CancellationToken ct = default)
        {
            var json = await _client.CallRawAsync("batch.listRuns",
                new { limit, offset }, ct).ConfigureAwait(false);
            return PrettyPrint(json);
        }

        [McpServerResource(
            UriTemplate = "acd-mcp://batch-runs/{run_id}",
            Name = "batch-run-by-id",
            MimeType = "application/json"),
         Description(
            "Full per-file result of a specific batch run. Includes step-level outcomes " +
            "(which Requires passed, which Apply summaries ran, which exceptions were " +
            "caught), elapsed timings, and the cancellation status.")]
        public async Task<string> ByIdAsync(
            [Description("The run id returned by autocad_batch_run_test (or from the recent list).")]
            string run_id,
            CancellationToken ct = default)
        {
            // Reserved word: 'last' collides with the alias resource below.
            // The SDK's UriTemplate-based dispatch should pick the alias
            // first when the literal segment matches; we still guard.
            if (string.Equals(run_id, "last", System.StringComparison.OrdinalIgnoreCase))
                return await LastAsync(ct).ConfigureAwait(false);

            var json = await _client.CallRawAsync("batch.getRun", new { run_id }, ct).ConfigureAwait(false);
            return PrettyPrint(json);
        }

        [McpServerResource(
            UriTemplate = "acd-mcp://batch-runs/last",
            Name = "batch-run-last",
            MimeType = "application/json"),
         Description(
            "Convenience alias for the most-recent batch run. Saves an extra round-trip " +
            "to enumerate /recent just to read the freshest entry.")]
        public async Task<string> LastAsync(CancellationToken ct = default)
        {
            var json = await _client.CallRawAsync("batch.getLastRun", new { }, ct).ConfigureAwait(false);
            return PrettyPrint(json);
        }

        private static string PrettyPrint(JsonElement el) =>
            JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
    }
}
