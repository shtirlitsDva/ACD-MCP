using System.Diagnostics;
using System.Text.Json;
using Acd.Mcp;
using Acd.Mcp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

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
                _state = _state is null
                    ? await CSharpScript
                        .RunAsync<object?>(submission, _options, _globals, typeof(RvtGlobals), ct)
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
