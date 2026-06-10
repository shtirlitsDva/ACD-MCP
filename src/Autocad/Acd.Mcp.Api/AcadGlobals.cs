using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
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
    //
    // The same constraint applies to the entire `Acd` REPL surface (see
    // AcdReplApi) and the DTO-loading types in Acd.Mcp.Api/Serialization.
    public sealed class AcadGlobals
    {
        public AcadGlobals(AcdReplApi acd)
        {
            Acd = acd ?? throw new ArgumentNullException(nameof(acd));
        }

        public Document Doc => Application.DocumentManager.MdiActiveDocument!;
        public Database Db => Doc.Database;
        public Editor Ed => Doc.Editor;

        // Null in non-Civil-3D drawings (e.g. plain .dwg opened in vanilla
        // AutoCAD). Callers either guard with a null check or wrap in try/catch
        // — same shape Civil scripts already expect from GetCivilDocument.
        public CivilDocument? CivilDoc => CivilApplication.ActiveDocument;

        // The canonical REPL pattern `Acd.DataProvider.ReadAll(entity)`
        // hangs off this. Same identifier (`Acd`) the DTO .csx files use,
        // but limited to read-only surface — no RegisterDto in REPL.
        public AcdReplApi Acd { get; }
    }
}
