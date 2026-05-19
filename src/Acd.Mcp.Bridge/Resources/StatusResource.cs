using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Resources
{
    // MCP resource: acd-mcp://status
    //
    // Returns a snapshot of "what works right now" in the plugin —
    // pipe state, palette state, per-capability ready/degraded/
    // unavailable status. The agent's skill consults this when a
    // tool call returns a transport error, to decide whether to
    // retry, wait, or surface the failure to the user.
    //
    // Read-only and side-effect-free; safe to poll. Backed by the
    // plugin's acdmcp.status RPC method.
    [McpServerResourceType]
    public sealed class StatusResource
    {
        private readonly AcadClient _client;

        public StatusResource(AcadClient client)
        {
            _client = client;
        }

        [McpServerResource(
            UriTemplate = "acd-mcp://status",
            Name = "acdmcp-status",
            MimeType = "application/json"),
         Description(
            "Live snapshot of plugin capability state: pipe up?, palette open?, per-capability " +
            "{ status: ready|degraded|unavailable, reason: <error_code or null> }. Use when a tool " +
            "returns a transport error (PIPE_NOT_LISTENING, AMBIGUOUS_AUTOCADS, etc.) to decide " +
            "between retry, wait, and escalation. Safe to read repeatedly — no side effects.")]
        public async Task<string> GetAsync(CancellationToken ct = default)
        {
            try
            {
                var json = await _client.CallRawAsync("acdmcp.status", new { }, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (AcadTransportException ex)
            {
                // The status resource exists to diagnose transport
                // failures — so if it fails, return the diagnosis
                // inline rather than throwing. The shape mirrors the
                // success payload so consumers parse the same way.
                var fallback = new
                {
                    version = "<unknown>",
                    pid = 0,
                    pipe = "<unreachable>",
                    transport_error = new { code = ex.ErrorCode, message = ex.Message },
                };
                return JsonSerializer.Serialize(fallback, new JsonSerializerOptions { WriteIndented = true });
            }
        }
    }
}
