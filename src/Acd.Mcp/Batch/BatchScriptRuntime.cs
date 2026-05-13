using System;
using System.Linq;
using Acd.Mcp.Batch;
using Acd.Mcp.Scripting;
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
            // Use RoslynReferences.Build — handles both file-based and
            // byte-loaded assemblies (DevReload's IsolatedPluginContext loads
            // plugin assemblies with empty Location). Anchoring on
            // typeof(AcadBatchGlobals) ensures the globals assembly is
            // referenced even when AppDomain.GetAssemblies() hasn't enumerated it.
            //
            // KNOWN LIMITATION (G6 in v2 crash-test journal): even with refs
            // resolved correctly, batch scripts STILL fail at run-time because
            // the Roslyn-compiled assembly lives in a NON-COLLECTIBLE scripting
            // ALC, but AcadBatchGlobals and IBatchContext live in COLLECTIBLE
            // DevReload-managed ALCs (Acd.Mcp.dll, Acd.Mcp.Batch.dll). The CLR
            // forbids non-collectible → collectible references, so script IL
            // fails to load with "non-collectible assembly may not reference
            // collectible assembly". Full fix requires moving AcadBatchGlobals
            // + IBatchContext + supporting types to the default (non-collectible)
            // ALC. See journal for the structural-refactor plan.
            return ScriptOptions.Default
                .WithReferences(RoslynReferences.Build(typeof(AcadBatchGlobals)))
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.IO",
                    "System.Text",
                    "Acd.Mcp.Batch",
                    "Autodesk.AutoCAD.DatabaseServices",
                    "Autodesk.AutoCAD.Geometry",
                    "Autodesk.AutoCAD.Runtime",
                    "Autodesk.Civil",
                    "Autodesk.Civil.DatabaseServices",
                    "Autodesk.Civil.DatabaseServices.Styles")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);
        }
    }
}
