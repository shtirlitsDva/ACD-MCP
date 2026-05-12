using System;
using System.Linq;
using Acd.Mcp.Batch;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;

namespace Acd.Mcp.Batch.Runtime
{
    // Convenience: builds a BatchScriptHost<AcadBatchGlobals> with the
    // Autodesk.AutoCAD.* imports the spec lists, plus all currently-loaded
    // assemblies as references. Same approach as the REPL's ScriptSession.
    //
    // One per plugin lifetime. The host's compile cache is shared across
    // runs (same body hash → same compiled delegate).
    internal static class BatchScriptRuntime
    {
        public static BatchScriptHost<AcadBatchGlobals> CreateHost() =>
            new(BuildOptions());

        public static ScriptOptions BuildOptions()
        {
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToArray();

            // The script body sees xDb / xTx / ctx via AcadBatchGlobals. We
            // import the AutoCAD namespaces so unqualified `Database`,
            // `Transaction`, `Entity`, etc. resolve. Application /
            // ApplicationServices imports are deliberately omitted at the
            // namespace level — the spec wants compile-time enforcement
            // that batch bodies never reach for the live document. (The
            // namespace IS technically importable, but the globals don't
            // expose Document / Editor / Application, so any attempt will
            // surface as an undefined-name error at compile time.)
            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.IO",
                    "System.Text",
                    "Acd.Mcp.Batch",
                    "Autodesk.AutoCAD.DatabaseServices",
                    "Autodesk.AutoCAD.Geometry",
                    "Autodesk.AutoCAD.Runtime")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);
        }
    }
}
