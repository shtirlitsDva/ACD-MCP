using System.Text.Encodings.Web;
using System.Text.Json;

using ModelContextProtocol;

namespace Acd.Mcp.Bridge
{
    // Single source of truth for the agent-facing MCP JSON policy, shared by the
    // AutoCAD and Revit bridges (the Revit bridge links this file). The stdio
    // channel is a JSON-RPC byte stream, not an HTML document, so the default
    // HTML-safe encoder needlessly escapes '<' '>' '&' '+' backtick and all
    // non-ASCII as \uXXXX (e.g. a generic type name `Dictionary`2` came back
    // mangled). Relax it once, here — both bridges pick it up, so the escaping
    // fix never has to be remembered in two Program.cs files again.
    public static class McpServerJson
    {
        public static JsonSerializerOptions Relaxed { get; } =
            new(McpJsonUtilities.DefaultOptions)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
    }
}
