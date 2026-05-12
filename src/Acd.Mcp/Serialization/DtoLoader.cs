using System.Diagnostics;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Acd.Mcp.Scripting;

namespace Acd.Mcp.Serialization
{
    // Scans the system + user DTO folders, compiles each .csx file as a
    // CSharpScript submission against DtoRegistrationGlobals, and lets the
    // body register projections into the shared DtoRegistry.
    //
    // Resolution rule: user overrides system. The loader achieves this by
    // compiling the system folder FIRST and the user folder SECOND, with the
    // registry's Register call overwriting on conflict.
    //
    // Threading: the public methods take a coarse lock so concurrent reload
    // triggers don't fight. The compile itself is bound by Roslyn's own
    // concurrency, which is fine for the file counts we expect (dozens).
    //
    // Errors: a malformed .csx is logged via Trace and skipped. One bad file
    // must never poison the rest of the set — that would make the whole
    // serializer behave like the user has no DTOs at all.
    public sealed class DtoLoader
    {
        private readonly DtoRegistry _registry;
        private readonly DtoDataProviderApi _dataProvider;
        private readonly Dictionary<string, DateTime> _mtimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private ScriptOptions? _options;

        public DtoLoader(DtoRegistry registry, DtoDataProviderApi dataProvider)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        }

        // Wipe and re-populate from scratch. Use on plugin startup and any
        // time the loader's mtime cache might be stale (e.g. user moved a
        // file into the folder externally).
        public void ReloadAll()
        {
            lock (_gate)
            {
                _registry.Clear();
                _mtimes.Clear();
                CompileFolder(DtoPaths.SystemFolder, "system");
                CompileFolder(DtoPaths.UserFolder, "user");
            }
        }

        // Incremental: recompile only the .csx files whose mtime changed
        // since the last scan. Used by the on-demand reload-on-miss path so
        // an unrelated existing entity doesn't pay for a full rescan.
        public void Refresh()
        {
            lock (_gate)
            {
                CompileFolder(DtoPaths.SystemFolder, "system", incrementalOnly: true);
                CompileFolder(DtoPaths.UserFolder, "user", incrementalOnly: true);
            }
        }

        private void CompileFolder(string folder, string tag, bool incrementalOnly = false)
        {
            if (!Directory.Exists(folder)) return;

            foreach (var path in Directory.EnumerateFiles(folder, "*.csx"))
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (incrementalOnly && _mtimes.TryGetValue(path, out var prev) && prev == mtime)
                    continue;

                CompileOne(path, tag);
                _mtimes[path] = mtime;
            }
        }

        private void CompileOne(string path, string tag)
        {
            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DtoLoader] Read failed: {path}: {ex.Message}");
                return;
            }

            var sourceTag = $"{tag}:{Path.GetFileName(path)}";
            var api = new DtoRegistrationApi(_registry, _dataProvider, sourceTag);
            var globals = new DtoRegistrationGlobals(api);

            try
            {
                CSharpScript.RunAsync(source, GetOptions(), globals, typeof(DtoRegistrationGlobals))
                    .GetAwaiter().GetResult();
            }
            catch (CompilationErrorException cex)
            {
                Trace.WriteLine($"[DtoLoader] Compile error in {sourceTag}: {cex.Message}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DtoLoader] Runtime error in {sourceTag}: {ex.Message}");
            }
        }

        private ScriptOptions GetOptions()
        {
            if (_options is not null) return _options;

            var refs = RoslynReferences.Build(typeof(DtoRegistrationGlobals));

            _options = ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "Autodesk.AutoCAD.ApplicationServices",
                    "Autodesk.AutoCAD.DatabaseServices",
                    "Autodesk.AutoCAD.Geometry",
                    "Autodesk.AutoCAD.EditorInput",
                    "Autodesk.AutoCAD.Runtime",
                    "Acd.Mcp.Serialization")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);

            return _options;
        }
    }
}
