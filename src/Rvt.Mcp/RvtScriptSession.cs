using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Acd.Mcp;
using Acd.Mcp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace Rvt.Mcp
{
    // Persistent C# script session over RvtGlobals — the Revit twin of
    // Acd.Mcp.Scripting.ScriptSession (which is welded to AcadGlobals and
    // its AutoCAD import list). The shared mechanics (trailing-expression
    // rewrite, reference building, console capture, ExecuteResult shapes)
    // come from the linked Acd.Mcp sources; only globals + imports differ.
    public sealed class RvtScriptSession : IDisposable
    {
        private readonly RvtGlobals _globals;
        private readonly JsonSerializerOptions? _jsonOptions;
        private ScriptState? _state;
        private ScriptOptions _options = BuildOptions();
        private InteractiveAssemblyLoader _loader = BuildLoader();

        public RvtScriptSession(RvtGlobals globals, JsonSerializerOptions? jsonOptions = null)
        {
            _globals = globals ?? throw new ArgumentNullException(nameof(globals));
            _jsonOptions = jsonOptions;
        }

        public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var capture = new ConsoleCapture();

            var submission = TrailingExpressionRewriter.AutoReturnTrailingExpression(code);

            try
            {
                // Create with an explicit InteractiveAssemblyLoader: this add-in
                // lives in a custom ALC, and without pre-registered dependencies
                // Roslyn's scripting host loads Rvt.Mcp.dll AGAIN from disk into
                // its own context — the submission's RvtGlobals then isn't OUR
                // RvtGlobals (InvalidCastException on the very first call).
                _state = _state is null
                    ? await CSharpScript
                        .Create<object?>(submission, _options, typeof(RvtGlobals), _loader)
                        .RunAsync(_globals, ct)
                        .ConfigureAwait(false)
                    : await _state
                        .ContinueWithAsync(submission, _options, ct)
                        .ConfigureAwait(false);

                var value = _state.ReturnValue;
                var repr = value?.ToString();
                var json = SerializeReturnValue(value);
                return ExecuteResult.Ok(repr, json, sw.ElapsedMilliseconds)
                    with { Stdout = capture.Stdout, Stderr = capture.Stderr };
            }
            catch (CompilationErrorException cex)
            {
                var diags = cex.Diagnostics.Select(MapDiagnostic).ToArray();
                return ExecuteResult.CompileError(diags, sw.ElapsedMilliseconds)
                    with { Stdout = capture.Stdout, Stderr = capture.Stderr };
            }
            catch (OperationCanceledException)
            {
                return ExecuteResult.Runtime("Cancelled", sw.ElapsedMilliseconds)
                    with { Stdout = capture.Stdout, Stderr = capture.Stderr };
            }
            catch (Exception ex)
            {
                var stderr = capture.Stderr;
                stderr = string.IsNullOrEmpty(stderr) ? ex.ToString() : stderr + "\n" + ex;
                return ExecuteResult.Runtime("Unhandled exception", sw.ElapsedMilliseconds)
                    with { Stdout = capture.Stdout, Stderr = stderr };
            }
        }

        public void Reset()
        {
            _state = null;
            _options = BuildOptions();
            _loader = BuildLoader();
        }

        // Map every already-loaded assembly identity to its live instance so
        // script submissions bind against the running code (one RvtGlobals,
        // one RevitAPI) instead of fresh disk loads in Roslyn's own context.
        private static InteractiveAssemblyLoader BuildLoader()
        {
            var loader = new InteractiveAssemblyLoader();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try { loader.RegisterDependency(asm); }
                catch { /* duplicate identity or unregisterable — skip */ }
            }
            return loader;
        }

        public void Dispose() => Reset();

        private JsonElement? SerializeReturnValue(object? value)
        {
            if (value is null) return null;
            if (_jsonOptions is null) return null;

            try
            {
                return JsonSerializer.SerializeToElement(value, value.GetType(), _jsonOptions);
            }
            catch (Exception ex)
            {
                var marker = new Dictionary<string, string>
                {
                    ["$serialization_error"] = ex.Message,
                };
                return JsonSerializer.SerializeToElement(marker);
            }
        }

        private static ScriptOptions BuildOptions()
        {
            var refs = RoslynReferences.Build(
                typeof(RvtGlobals),
                typeof(Console));

            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.IO",
                    "System.Text",
                    "Autodesk.Revit.DB",
                    "Autodesk.Revit.UI")
                .WithAllowUnsafe(false)
                .WithOptimizationLevel(OptimizationLevel.Debug);
        }

        private static DiagnosticInfo MapDiagnostic(Diagnostic d)
        {
            var span = d.Location.GetMappedLineSpan();
            int? line = span.IsValid ? span.StartLinePosition.Line + 1 : null;
            int? col = span.IsValid ? span.StartLinePosition.Character + 1 : null;
            return new DiagnosticInfo(d.Severity.ToString(), d.GetMessage(), line, col);
        }
    }
}
