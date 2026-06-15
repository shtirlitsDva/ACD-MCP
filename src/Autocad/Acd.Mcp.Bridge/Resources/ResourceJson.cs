using System.Text.Encodings.Web;
using System.Text.Json;

namespace Acd.Mcp.Bridge.Resources
{
    // Shared serializer options for MCP resource bodies. Two reasons it is a
    // single cached instance rather than `new JsonSerializerOptions { ... }`
    // per call:
    //
    //   * Encoder — these bodies are read by an agent over a JSON-RPC byte
    //     stream, not embedded in HTML. The default HTML-safe encoder escapes
    //     '<' '>' '&' backtick and all non-ASCII as \uXXXX; the relaxed encoder
    //     emits them literally (only '"' '\' and control chars stay escaped).
    //   * Caching — System.Text.Json caches type metadata per options
    //     instance, so a fresh options on every resource read both produced the
    //     escaped output and threw that cache away each time.
    internal static class ResourceJson
    {
        public static readonly JsonSerializerOptions Indented = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
