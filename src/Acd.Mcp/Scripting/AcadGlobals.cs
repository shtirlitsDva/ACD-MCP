using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp.Scripting
{
    // Globals injected into the script session. Re-resolves the active document on
    // every property access so a snippet always sees the current drawing even if
    // the user switched between calls. Application is a static class in modern
    // AutoCAD, so we don't expose it as a value — the script gets it via the
    // imported Autodesk.AutoCAD.ApplicationServices namespace.
    public sealed class AcadGlobals
    {
        public Document Doc => Application.DocumentManager.MdiActiveDocument!;
        public Database Db => Doc.Database;
        public Editor Ed => Doc.Editor;
    }
}
