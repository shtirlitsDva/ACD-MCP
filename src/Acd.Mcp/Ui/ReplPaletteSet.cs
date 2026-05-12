using Acd.Mcp.Pipe;
using Autodesk.AutoCAD.Windows;

namespace Acd.Mcp.Ui
{
    // PaletteSet wrapper. One tab today ("REPL"); adding a future "Locals" or
    // "Drawing inspector" tab is one more AddVisual call.
    //
    // Disposes its hosted view synchronously from PaletteSet.Dispose because
    // WPF Unloaded fires asynchronously through the dispatcher — by the time
    // it would arrive, the plugin's executor and log have already been nulled
    // out in McpPlugin.Terminate. Same boundary-isolation pattern as
    // DimensioneringV2.CustomPaletteSet.
    internal sealed class ReplPaletteSet : PaletteSet
    {
        private static readonly Guid PaletteGuid =
            new("8a4f8d2b-7c5e-4a1f-9c93-3c4b6d2f9a87");

        private readonly ReplControl _control;

        public ReplPaletteSet(AcadExecutor executor, ExecutionLog log)
            : base("ACD-MCP REPL", "ACDMCP_PALETTE", PaletteGuid)
        {
            Style = PaletteSetStyles.ShowAutoHideButton
                  | PaletteSetStyles.ShowCloseButton
                  | PaletteSetStyles.ShowPropertiesMenu
                  | PaletteSetStyles.Snappable;
            MinimumSize = new System.Drawing.Size(400, 250);

            _control = new ReplControl(executor, log);
            AddVisual("REPL", _control);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SafeBoundary.Run("ReplPaletteSet.Dispose(ReplControl)", () => _control.Dispose());
            }
            base.Dispose(disposing);
        }
    }
}
