using Acd.Mcp.Batch.Runtime;
using Acd.Mcp.Batch.Ui;
using Acd.Mcp.Pipe;
using Acd.Mcp.Scripting;
using Autodesk.AutoCAD.Windows;

namespace Acd.Mcp.Ui
{
    // PaletteSet wrapper. Two tabs:
    //   - REPL  — the existing interactive C# session against the active doc.
    //   - BATCH — the side-loaded, multi-file batch authoring + runner.
    //
    // Disposes its hosted views synchronously from PaletteSet.Dispose because
    // WPF Unloaded fires asynchronously through the dispatcher — by the time
    // it would arrive, the plugin's executor and log have already been nulled
    // out in McpPlugin.Terminate. Same boundary-isolation pattern as
    // DimensioneringV2.CustomPaletteSet.
    internal sealed class ReplPaletteSet : PaletteSet
    {
        private static readonly Guid PaletteGuid =
            new("8a4f8d2b-7c5e-4a1f-9c93-3c4b6d2f9a87");

        private readonly ReplControl _replControl;
        private readonly BatchControl _batchControl;

        public ReplPaletteSet(AcadExecutor executor, ScriptSession session, ExecutionLog log, BatchExecutor batchExecutor)
            : base("ACD-MCP", "ACDMCP_PALETTE", PaletteGuid)
        {
            Style = PaletteSetStyles.ShowAutoHideButton
                  | PaletteSetStyles.ShowCloseButton
                  | PaletteSetStyles.ShowPropertiesMenu
                  | PaletteSetStyles.Snappable;
            MinimumSize = new System.Drawing.Size(420, 320);

            _replControl = new ReplControl(executor, session, log);
            _batchControl = new BatchControl(batchExecutor);
            AddVisual("REPL", _replControl);
            AddVisual("BATCH", _batchControl);
        }

        // Public so the plugin can wire the BATCH VM into the pipe handler
        // as the BatchUiState provider.
        public BatchViewModel BatchViewModel => _batchControl.ViewModel;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SafeBoundary.Run("ReplPaletteSet.Dispose(ReplControl)",  () => _replControl.Dispose());
                SafeBoundary.Run("ReplPaletteSet.Dispose(BatchControl)", () => _batchControl.Dispose());
            }
            base.Dispose(disposing);
        }
    }
}
