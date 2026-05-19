using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    [McpServerToolType]
    public sealed class ScriptExecuteTool
    {
        private readonly AcadClient _client;

        public ScriptExecuteTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_script_execute",
            ReadOnly = false,        // snippet can modify the drawing
            Destructive = true,      // can erase / overwrite entities
            Idempotent = false,      // session state persists; same call twice ≠ same effect
            OpenWorld = true),       // can touch the file system, network, anything in-process
         Description(
            "Execute arbitrary C# code inside the running AutoCAD process against the active drawing. " +
            "The snippet runs on AutoCAD's main thread under a document lock. Variables declared at top " +
            "level persist across calls — it's a session, not a one-shot. Globals available: Doc (active " +
            "Document), Db (its Database), Ed (its Editor), CivilDoc (CivilDocument or null), Acd " +
            "(metadata façade). The full Autodesk.AutoCAD.* namespaces are imported (Civil 3D imports " +
            "must be added per-submission). Returns an object with success, return_value_repr, " +
            "return_value_json, diagnostics (compile errors with line/col), stdout/stderr (captured), " +
            "and elapsed_ms.")]
        public async Task<ExecuteResult> ExecuteAsync(
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
            catch (AcadTransportException ex)
            {
                // Bridge couldn't reach the plugin (no AutoCAD, pipe down,
                // retries exhausted). ExecuteResult has no dedicated
                // error_code slot, so prepend the stable code to the
                // runtime-error message — the agent's skill can parse it.
                return ExecuteResult.Runtime(
                    $"[{ex.ErrorCode}] {ex.Message}",
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
