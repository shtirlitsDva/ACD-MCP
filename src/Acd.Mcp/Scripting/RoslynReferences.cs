using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Acd.Mcp.Scripting
{
    // Builds the MetadataReference list that any Roslyn CSharpScript session
    // inside the plugin needs. Two paths per assembly:
    //
    //   * Location is non-empty  → MetadataReference.CreateFromFile(path).
    //   * Location is empty      → TryGetRawMetadata → AssemblyMetadata.Create
    //                              from the in-memory PE blob.
    //
    // The second path matters for assemblies loaded into a custom
    // AssemblyLoadContext via LoadFromStream (DevReload's IsolatedPluginContext
    // is the concrete case). Without it, Roslyn cannot resolve the globalsType
    // and binding silently breaks for any identifier reaching into the plugin
    // assembly's namespace.
    //
    // Shared by ScriptSession (REPL) and DtoLoader (DTO file compilation).
    public static class RoslynReferences
    {
        public static List<MetadataReference> Build(params Type[] alsoIncludeAssembliesOf)
        {
            var refs = new List<MetadataReference>();
            var seen = new HashSet<Assembly>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                Add(asm, refs, seen);

            // Belt-and-braces: ensure each requested anchor assembly is
            // referenced even if the AppDomain scan didn't enumerate it
            // (which can happen for assemblies the host hasn't touched yet).
            foreach (var t in alsoIncludeAssembliesOf ?? Array.Empty<Type>())
                Add(t.Assembly, refs, seen);

            return refs;
        }

        private static void Add(Assembly asm, List<MetadataReference> refs, HashSet<Assembly> seen)
        {
            if (asm.IsDynamic) return;
            if (!seen.Add(asm)) return;

            try
            {
                if (!string.IsNullOrEmpty(asm.Location))
                {
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                    return;
                }

                if (TryGetInMemoryReference(asm, out var inMemory))
                    refs.Add(inMemory);
            }
            catch
            {
                // A corrupt / unreadable assembly should not poison the whole
                // reference set. Skip silently; missing references will surface
                // as CS errors against any code that actually depends on this
                // assembly, which is the correct outcome.
            }
        }

        private static unsafe bool TryGetInMemoryReference(Assembly asm, out MetadataReference reference)
        {
            reference = null!;
            if (!asm.TryGetRawMetadata(out byte* blob, out int length)) return false;

            var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
            var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
            reference = assemblyMetadata.GetReference();
            return true;
        }
    }
}
