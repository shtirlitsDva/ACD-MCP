using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace Acd.Mcp.Bridge.Tools
{
    // Agent verb: read the active drawing's pickfirst selection (the
    // entities the user has selected/highlighted in AutoCAD) and return
    // their identifying metadata + the drawing that owns them.
    //
    // This is the "push" channel the user reaches for when they want
    // the LLM to look at a specific entity without having to LIST it
    // and paste the handle into the chat. The user selects the entity
    // in AutoCAD, then asks the agent — the agent calls this tool and
    // gets every handle the user picked.
    //
    // Annotation matrix per the spec's <agent-tool-surface>:
    //   ReadOnly    = true   (queries live drawing state; no mutation)
    //   Destructive = false
    //   Idempotent  = true   (pure read; same pickset => same output)
    //   OpenWorld   = true   (drawing state is part of the open world)
    [McpServerToolType]
    public sealed class GetSelectionTool
    {
        private readonly AcadClient _client;

        public GetSelectionTool(AcadClient client)
        {
            _client = client;
        }

        [McpServerTool(
            Name = "autocad_get_selection",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true),
         Description(
            "Return the active drawing's pickfirst selection (entities the user has selected/highlighted " +
            "in AutoCAD) plus the drawing's filename and full path. Shape: " +
            "{ document_name, document_path, count, entities: [{ handle, object_class, layer, block_name? }] }. " +
            "object_class is the .NET type name (e.g. Polyline, BlockReference); block_name is populated " +
            "only for BlockReference entities (the user-visible name for dynamic blocks, the BTR name " +
            "otherwise); document_path is null for unsaved drawings. count=0 with empty entities[] when " +
            "nothing is selected — not an error. Errors: error_message starting with NO_ACTIVE_DOCUMENT " +
            "when no drawing is open. " +
            "Call this when the user says 'look at the selected entity' or similar — much faster than " +
            "asking them to LIST and paste the handle.")]
        public async Task<GetSelectionResult> GetSelectionAsync(CancellationToken ct = default)
        {
            try
            {
                var p = await _client.CallAsync<GetSelectionPayload>("script.getSelection",
                    @params: null, ct).ConfigureAwait(false);
                return new GetSelectionResult(
                    ok: true, error_code: null, error_message: null,
                    p.document_name, p.document_path, p.count, p.entities);
            }
            catch (AcadRpcException ex)
            {
                return new GetSelectionResult(
                    ok: false,
                    error_code: ex.Code.ToString(CultureInfo.InvariantCulture),
                    error_message: ex.Message,
                    document_name: null, document_path: null, count: null, entities: null);
            }
            catch (AcadTransportException ex)
            {
                return new GetSelectionResult(
                    ok: false,
                    error_code: ex.ErrorCode,
                    error_message: ex.Message,
                    document_name: null, document_path: null, count: null, entities: null);
            }
        }
    }

    // Plugin wire shape for script.getSelection. Bridge wraps this in
    // GetSelectionResult on the success path.
    internal sealed record GetSelectionPayload(
        string document_name,
        string? document_path,
        int count,
        SelectedEntity[] entities);

    public sealed record SelectedEntity(
        string handle,
        string object_class,
        string layer,
        string? block_name);

    public sealed record GetSelectionResult(
        bool ok,
        string? error_code,
        string? error_message,
        string? document_name,
        string? document_path,
        int? count,
        SelectedEntity[]? entities);
}
