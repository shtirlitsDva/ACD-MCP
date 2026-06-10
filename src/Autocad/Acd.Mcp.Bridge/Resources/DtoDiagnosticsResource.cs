using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Resources
{
    // MCP resource: acd-mcp://dto-system/diagnostics
    //
    // Returns the current list of DTO files that failed to compile, one
    // entry per file. Each entry carries source tag, header type, resolved
    // type (or null if the header couldn't resolve), first-diagnostic
    // message, line/column, and error code (e.g. CS1061).
    //
    // The /acd-mcp:add-dto skill points the agent here when the serializer
    // emits a `$unsupported` with no inline `reason` (which happens only
    // when the missing DTO has never been attempted on disk).
    [McpServerResourceType]
    public sealed class DtoDiagnosticsResource
    {
        private readonly AcadClient _client;

        public DtoDiagnosticsResource(AcadClient client)
        {
            _client = client;
        }

        [McpServerResource(
            UriTemplate = "acd-mcp://dto-system/diagnostics",
            Name = "dto-diagnostics",
            MimeType = "application/json"),
         Description(
            "Live list of every DTO file in dto-system/ and dto-user/ that failed to compile. " +
            "Each entry has: source (e.g. 'user:Circle.csx'), header_type, resolved_type, message, " +
            "line, column, error_code. Use this to triage why the serializer is still emitting " +
            "{\"$unsupported\":\"...\"} for a type you just authored a DTO for. The same diagnostic " +
            "is also surfaced inline in the $unsupported marker as `reason` when the failing DTO's " +
            "@dto header resolved to the requested type.")]
        public async Task<string> GetAsync(CancellationToken ct = default)
        {
            var json = await _client.CallRawAsync("dto.diagnostics", new { }, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
