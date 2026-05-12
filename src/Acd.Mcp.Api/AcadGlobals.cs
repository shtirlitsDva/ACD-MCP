using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp.Api
{
    // Globals injected into the REPL script session. Re-resolves the active
    // document on every property access so a snippet always sees the current
    // drawing even if the user switched between calls.
    //
    // Lives in Acd.Mcp.Api (separate assembly, loaded into default ALC) rather
    // than the plugin's Acd.Mcp because Roslyn submissions are produced in a
    // CSharpScript-owned LoadContext that holds strong references to whatever
    // assembly defines the globalsType. If that assembly were the plugin's own
    // (isolated, byte[]-loaded) ALC, submissions would pin the old isolated
    // ALC across hot-reloads. By living here, submissions reach into the
    // default ALC instead — already pinned by the host — so the isolated
    // ALC stays cleanly unloadable on MCPDEV.
    public sealed class AcadGlobals
    {
        public Document Doc => Application.DocumentManager.MdiActiveDocument!;
        public Database Db => Doc.Database;
        public Editor Ed => Doc.Editor;
    }
}
