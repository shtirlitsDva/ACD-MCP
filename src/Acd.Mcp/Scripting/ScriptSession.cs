using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Acd.Mcp.Scripting
{
    // Persistent C# script session. State (variables, usings declared at top level)
    // carries from one call to the next via ScriptState.ContinueWithAsync.
    //
    // Reset() drops the state; Roslyn-emitted assemblies from previous calls remain
    // loaded in the default ALC for the process lifetime (acceptable; documented).
    public sealed class ScriptSession
    {
        private readonly AcadGlobals _globals = new();
        private ScriptState? _state;
        private ScriptOptions _options = BuildOptions();

        public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var capture = new ConsoleCapture();
            try
            {
                _state = _state is null
                    ? await CSharpScript
                        .RunAsync<object?>(code, _options, _globals, typeof(AcadGlobals), ct)
                        .ConfigureAwait(false)
                    : await _state
                        .ContinueWithAsync(code, _options, ct)
                        .ConfigureAwait(false);

                return ExecuteResult.Ok(_state.ReturnValue?.ToString(), sw.ElapsedMilliseconds)
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
                // Runtime exceptions go into the stderr stream alongside any prior
                // Console.Error.Write output the snippet produced before throwing.
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

        private static ScriptOptions BuildOptions()
        {
            // Hand the script the same surface the host plugin has. Filtering out
            // dynamic assemblies (no Location) and any with an empty Location avoids
            // MetadataReference.CreateFromFile throwing.
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToArray();

            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.IO",
                    "System.Text",
                    "Autodesk.AutoCAD.ApplicationServices",
                    "Autodesk.AutoCAD.DatabaseServices",
                    "Autodesk.AutoCAD.Geometry",
                    "Autodesk.AutoCAD.EditorInput",
                    "Autodesk.AutoCAD.Runtime")
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
