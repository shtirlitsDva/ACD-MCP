using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    [McpServerToolType]
    public sealed class ExecuteCsharpTool
    {
        private readonly AcadClient _client;

        public ExecuteCsharpTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(Name = "execute_csharp"),
         Description(
            "Execute arbitrary C# code inside the running AutoCAD process. The snippet runs on AutoCAD's " +
            "main thread under a document lock. Variables declared at top level persist across calls — " +
            "it's a session, not a one-shot. Globals available: Doc (active Document), Db (its Database), " +
            "Ed (its Editor). The full Autodesk.AutoCAD.* namespaces are imported. Returns an object " +
            "with success, returnValueRepr, diagnostics (compile errors with line/col), stderr (runtime " +
            "exceptions), and elapsedMs.")]
        public Task<ExecuteResult> ExecuteCsharpAsync(
            [Description("C# code to execute. Multi-line allowed; may declare vars/methods; may end with an expression whose value is returned.")]
            string code,
            [Description("Optional cooperative timeout in milliseconds. A snippet that spins without observing its CancellationToken cannot be interrupted.")]
            int? timeout_ms = null,
            CancellationToken ct = default)
        {
            return _client.ExecuteAsync(code, timeout_ms, ct);
        }
    }
}
