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
        private readonly DtoDiagnostics _diagnostics;
        private readonly Dictionary<string, DateTime> _mtimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        private ScriptOptions? _options;

        public DtoLoader(DtoRegistry registry, DtoDataProviderApi dataProvider, DtoDiagnostics diagnostics)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        // Wipe and re-populate from scratch. Use on plugin startup and any
        // time the loader's mtime cache might be stale (e.g. user moved a
        // file into the folder externally).
        public void ReloadAll()
        {
            lock (_gate)
            {
                _registry.Clear();
                _diagnostics.Clear();
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
                SafeBoundary.Info("DtoLoader", $"Read failed: {path}: {ex.Message}");
                return;
            }

            var sourceTag = $"{tag}:{Path.GetFileName(path)}";

            // Parse the @dto header so a compile failure can be keyed by
            // the type the file was *meant* to register (the converter
            // looks up failures by Type to enrich the $unsupported marker).
            // The header is documentation per DtoHeader's contract; the
            // body's RegisterDto<T> is the source of truth on success.
            var headerType = DtoHeader.TryParse(source) ?? "";
            Type? resolvedType = string.IsNullOrEmpty(headerType)
                ? null
                : ResolveType(headerType);

            var api = new DtoRegistrationApi(_registry, _dataProvider, sourceTag);
            var globals = new DtoRegistrationGlobals(api);

            try
            {
                CSharpScript.RunAsync(source, GetOptions(), globals, typeof(DtoRegistrationGlobals))
                    .GetAwaiter().GetResult();
                // Clear any prior diagnostic for this source on successful compile.
                _diagnostics.ClearForSource(sourceTag);
            }
            catch (CompilationErrorException cex)
            {
                var (line, col, code, msg) = DtoDiagnostics.ParseFirstDiagnostic(cex.Message);
                _diagnostics.RecordFailure(new DtoCompileFailure(
                    Source: sourceTag,
                    HeaderType: headerType,
                    ResolvedType: resolvedType,
                    Message: msg,
                    Line: line,
                    Column: col,
                    ErrorCode: code));
                SafeBoundary.Info("DtoLoader", $"Compile error in {sourceTag}: {cex.Message}");
            }
            catch (Exception ex)
            {
                _diagnostics.RecordFailure(new DtoCompileFailure(
                    Source: sourceTag,
                    HeaderType: headerType,
                    ResolvedType: resolvedType,
                    Message: ex.Message,
                    Line: null,
                    Column: null,
                    ErrorCode: ex.GetType().Name));
                SafeBoundary.Info("DtoLoader", $"Runtime error in {sourceTag}: {ex.Message}");
            }
        }

        // Resolve a type name to a Type via every loaded AppDomain assembly.
        // Returns null on miss — `$unsupported.reason` then carries the
        // failure keyed by source tag only (the diagnostics resource still
        // surfaces it, just without a Type-based lookup path).
        private static Type? ResolveType(string fullName)
        {
            var t = Type.GetType(fullName, throwOnError: false);
            if (t is not null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName, throwOnError: false);
                if (t is not null) return t;
            }
            return null;
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
                    "Autodesk.Civil",
                    "Autodesk.Civil.ApplicationServices",
                    "Autodesk.Civil.DatabaseServices",
                    "Autodesk.Civil.DatabaseServices.Styles",
                    "Acd.Mcp.Serialization")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);

            return _options;
        }
    }
}
