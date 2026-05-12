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

        [McpServerTool(
            Name = "autocad_execute_csharp",
            ReadOnly = false,        // snippet can modify the drawing
            Destructive = true,      // can erase / overwrite entities
            Idempotent = false,      // session state persists; same call twice ≠ same effect
            OpenWorld = true),       // can touch the file system, network, anything in-process
         Description(
            "Execute arbitrary C# code inside the running AutoCAD process. The snippet runs on AutoCAD's " +
            "main thread under a document lock. Variables declared at top level persist across calls — " +
            "it's a session, not a one-shot. Globals available: Doc (active Document), Db (its Database), " +
            "Ed (its Editor). The full Autodesk.AutoCAD.* namespaces are imported. Returns an object " +
            "with success, returnValueRepr, diagnostics (compile errors with line/col), stderr (runtime " +
            "exceptions), and elapsedMs.")]
        public async Task<ExecuteResult> ExecuteCsharpAsync(
            [Description("C# code to execute. Multi-line allowed; may declare vars/methods; may end with an expression whose value is returned.")]
            string code,
            [Description("Optional cooperative timeout in milliseconds. A snippet that spins without observing its CancellationToken cannot be interrupted.")]
            int? timeout_ms = null,
            CancellationToken ct = default)
        {
            try
            {
                return await _client.ExecuteAsync(code, timeout_ms, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller (e.g. MCP client shutting down) cancelled — let the SDK handle.
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // AutoCAD discovery: no instance, multiple instances, bad --pid.
                return ExecuteResult.Runtime(ex.Message, 0);
            }
            catch (IOException ex)
            {
                // Pipe transport: not connected, broken mid-call, etc.
                return ExecuteResult.Runtime(
                    $"AutoCAD pipe unavailable: {ex.Message}. Is the listener running? " +
                    "(ACDMCP_START inside AutoCAD.)",
                    0);
            }
            catch (AcadRpcException ex)
            {
                // Plugin returned a JSON-RPC error envelope.
                return ExecuteResult.Runtime(
                    $"AutoCAD reported an RPC error (code {ex.Code}): {ex.Message}",
                    0);
            }
        }
    }
}
