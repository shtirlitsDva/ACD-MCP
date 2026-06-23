using System.ComponentModel;
using Acd.Mcp;
using ModelContextProtocol.Server;

namespace Rvt.Mcp.Bridge.Tools
{
    [McpServerToolType]
    public sealed class RevitScriptExecuteTool
    {
        private readonly RevitClient _client;

        public RevitScriptExecuteTool(RevitClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "revit_script_execute",
            ReadOnly = false,
            Destructive = true,
            Idempotent = false,
            OpenWorld = true),
         Description(
            "Execute arbitrary C# code inside the running Revit process. The snippet runs in Revit " +
            "API context (via ExternalEvent, when Revit is idle). Variables declared at top level " +
            "persist across calls — it's a session, not a one-shot. Globals: UiApp (UIApplication), " +
            "App (Application), UiDoc (UIDocument or null), Doc (active Document or null). " +
            "Autodesk.Revit.DB and Autodesk.Revit.UI are imported. Model mutations need the snippet " +
            "to open/commit its own Transaction on Doc. Returns success, return_value_json " +
            "(Element → {id,name,category,type}; ElementId → number; XYZ → {x,y,z}; " +
            "other Revit types → {\"$unsupported\": ...}), and elapsed_ms — plus, only when " +
            "non-empty, stdout, stderr, and diagnostics.")]
        public async Task<ExecuteResult> ExecuteAsync(
            [Description("C# code to execute. Multi-line allowed; may declare vars/methods; may end with an expression whose value is returned.")]
            string code,
            [Description("Optional cooperative timeout in milliseconds. Also bounds waiting for Revit to become idle (modal dialogs block execution).")]
            int? timeout_ms = null,
            CancellationToken ct = default)
        {
            try
            {
                return await _client.ExecuteAsync(code, timeout_ms, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RvtTransportException ex)
            {
                return ExecuteResult.Runtime($"[{ex.ErrorCode}] {ex.Message}", 0);
            }
            catch (RvtRpcException ex)
            {
                return ExecuteResult.Runtime(
                    $"Revit reported an RPC error (code {ex.Code}): {ex.Message}", 0);
            }
        }
    }
}
