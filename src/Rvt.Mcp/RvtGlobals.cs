using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Rvt.Mcp
{
    // REPL globals — the identifiers every snippet sees. Properties
    // re-resolve per access so a document switch between calls is always
    // reflected; nothing here caches a Document.
    //
    // Snippets run inside Revit API context (ExternalEvent). Transactions
    // are the snippet's own responsibility:
    //   using (var t = new Transaction(Doc, "x")) { t.Start(); ...; t.Commit(); }
    public sealed class RvtGlobals
    {
        private readonly UIApplication _uiApp;

        public RvtGlobals(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        public UIApplication UiApp => _uiApp;
        public Application App => _uiApp.Application;
        public UIDocument? UiDoc => _uiApp.ActiveUIDocument;
        public Document? Doc => _uiApp.ActiveUIDocument?.Document;
    }
}
