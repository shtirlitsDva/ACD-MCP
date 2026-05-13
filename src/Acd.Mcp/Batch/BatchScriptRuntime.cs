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
            // The collectibility-violation that blocked batch scripts in v1/v2
            // is fixed structurally: AcadBatchGlobals lives in Acd.Mcp.Api and
            // IBatchContext / StepOutcome / BatchPhase live in
            // Acd.Mcp.Contracts. Both assemblies are loaded into the default
            // (non-collectible) ALC via DevReload's streamedAssemblies list,
            // so the script's emitted IL never references a collectible type.
            // No Autodesk.Civil.* imports: those namespaces define their
            // own `Entity` type which collides with
            // Autodesk.AutoCAD.DatabaseServices.Entity, producing CS0104
            // for any unqualified `Entity` usage. F13 (v1) dropped Civil
            // imports for the REPL; G9 (v2) is the same change for BATCH.
            // Scripts that genuinely need Civil 3D types qualify them with
            // the full Autodesk.Civil.* path.
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
                    "Autodesk.AutoCAD.Runtime")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);
        }
    }
}
