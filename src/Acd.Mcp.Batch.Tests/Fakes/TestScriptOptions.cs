using System;
using System.Linq;
using Microsoft.CodeAnalysis.Scripting;

namespace Acd.Mcp.Batch.Tests.Fakes
{
    // Builds a ScriptOptions object that lets test scripts reference the
    // fake types in this assembly plus the Batch runtime's interfaces.
    public static class TestScriptOptions
    {
        public static ScriptOptions Build()
        {
            // Hand the script the same surface this test assembly has — this
            // assembly already references Acd.Mcp.Batch, so its types resolve.
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location))
                .ToArray();

            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "Acd.Mcp.Batch",
                    "Acd.Mcp.Batch.Tests.Fakes");
        }
    }
}
