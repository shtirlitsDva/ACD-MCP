using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Api;
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
    //
    // Implements IDisposable so the plugin can register it with the central
    // ResourceManager and have its state dropped on Terminate alongside the
    // other long-lived resources. Dispose() delegates to Reset() — there are
    // no unmanaged handles, only the accumulated ScriptState chain that we
    // want released for GC.
    public sealed class ScriptSession : IDisposable
    {
        private readonly AcadGlobals _globals;
        private readonly JsonSerializerOptions? _jsonOptions;
        private ScriptState? _state;
        private ScriptOptions _options = BuildOptions();

        // Read-only views for UI inspectors. CurrentState may be null before the
        // first successful submission and after Reset(). Both surfaces are safe
        // to read from the main thread between ExecuteAsync calls; readers must
        // not mutate the returned ScriptState.
        public ScriptState? CurrentState => _state;
        public AcadGlobals Globals => _globals;

        // globals carries the live `Acd.DataProvider` façade alongside Doc/
        // Db/Ed/CivilDoc, so REPL submissions can read entity metadata via
        // the same pattern DTO bodies use. jsonOptions is the DTO-aware
        // serializer the caller (plugin bootstrap) constructs. When null we
        // fall back to ToString() only — keeps the session usable in tests
        // that don't care about the JSON path.
        public ScriptSession(AcadGlobals globals, JsonSerializerOptions? jsonOptions = null)
        {
            _globals = globals ?? throw new ArgumentNullException(nameof(globals));
            _jsonOptions = jsonOptions;
        }

        public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var capture = new ConsoleCapture();

            // REPL convenience: if the user's last statement is a bare value-
            // shaped expression ending with ';' (e.g. "x * 10;", "new List<int>{1,2};"),
            // strip that semicolon so CSharpScript treats the trailing expression
            // as the submission's return value. See TrailingExpressionRewriter
            // for the exact rules.
            var submission = TrailingExpressionRewriter.AutoReturnTrailingExpression(code);

            try
            {
                _state = _state is null
                    ? await CSharpScript
                        .RunAsync<object?>(submission, _options, _globals, typeof(AcadGlobals), ct)
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

        public void Dispose() => Reset();

        // Best-effort: a serializer error must never break the REPL response.
        // The most common failure mode is touching a closed AutoCAD entity
        // (script returned a reference whose owning Transaction is gone); we
        // capture and report instead of throwing.
        //
        // The "$serialization_error" key shares the `$` prefix with the
        // converter's `$unsupported` marker. Agents pattern-match on the
        // leading `$` to spot serializer-emitted sentinels — keeping both
        // markers in the same family avoids a second pattern.
        private string? SerializeReturnValue(object? value)
        {
            if (value is null) return null;
            if (_jsonOptions is null) return null;

            try
            {
                return JsonSerializer.Serialize(value, value.GetType(), _jsonOptions);
            }
            catch (Exception ex)
            {
                // Dictionary is the simplest way to emit a property whose name
                // starts with `$` — C# identifiers cannot, so anonymous types
                // can't carry that key directly.
                var marker = new Dictionary<string, string>
                {
                    ["$serialization_error"] = ex.Message,
                };
                return JsonSerializer.Serialize(marker);
            }
        }

        private static ScriptOptions BuildOptions()
        {
            // Anchor on AcadGlobals so the globalsType is always resolvable even
            // when AppDomain enumeration misses the plugin assembly (DevReload's
            // byte[]-loaded ALC). System.Console is force-loaded so
            // ConsoleCapture's redirected stdout/stderr actually accept a
            // call (otherwise CS0103 'Console' does not exist — System.Console
            // is lazy-loaded on demand and the AppDomain scan doesn't see it).
            var refs = RoslynReferences.Build(
                typeof(AcadGlobals),
                typeof(System.Console));

            // Imports deliberately exclude Autodesk.Civil.* — those namespaces
            // define Entity and DBObject too, which collide with the AutoCAD
            // types every REPL snippet uses. Users that need the Civil surface
            // add `using Autodesk.Civil.DatabaseServices;` (or a `using` alias)
            // at the top of their submission — explicit, not implicit.
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
