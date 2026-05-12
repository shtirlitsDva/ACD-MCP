using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Api;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        private readonly JsonSerializerOptions? _jsonOptions;
        private ScriptState? _state;
        private ScriptOptions _options = BuildOptions();

        // Read-only views for UI inspectors. CurrentState may be null before the
        // first successful submission and after Reset(). Both surfaces are safe
        // to read from the main thread between ExecuteAsync calls; readers must
        // not mutate the returned ScriptState.
        public ScriptState? CurrentState => _state;
        public AcadGlobals Globals => _globals;

        // jsonOptions is the DTO-aware serializer the caller (plugin bootstrap)
        // constructs. When null we fall back to ToString() only — keeps the
        // session usable in tests that don't care about the JSON path.
        public ScriptSession(JsonSerializerOptions? jsonOptions = null)
        {
            _jsonOptions = jsonOptions;
        }

        public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            using var capture = new ConsoleCapture();

            // REPL convenience: if the user's last statement is a bare expression
            // ending with ';' (e.g. "x * 10;"), strip that semicolon so CSharpScript
            // treats the trailing expression as the submission's return value.
            // Without this, "x * 10;" is CS0201 ("Only assignment, call, increment,
            // decrement, await, and new object expressions can be used as a
            // statement"). Same convention as LINQPad / dotnet-script / F# Interactive.
            var submission = AutoReturnTrailingExpression(code);

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

        // Best-effort: a serializer error must never break the REPL response.
        // The most common failure mode is touching a closed AutoCAD entity
        // (script returned a reference whose owning Transaction is gone); we
        // capture and report instead of throwing.
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
                return JsonSerializer.Serialize(new { serialization_error = ex.Message });
            }
        }

        private static ScriptOptions BuildOptions()
        {
            // Anchor on AcadGlobals so the globalsType is always resolvable even
            // when AppDomain enumeration misses the plugin assembly (DevReload's
            // byte[]-loaded ALC). See RoslynReferences for the why.
            var refs = RoslynReferences.Build(typeof(AcadGlobals));

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

        // If the last statement of the submission is an ExpressionStatement whose
        // expression isn't legal as a statement (i.e. not an assignment, call,
        // increment, decrement, await, or object-creation), drop its trailing
        // semicolon. Roslyn's script kind then treats the dangling expression as
        // the submission's return value and we display it as "=> result" in the
        // log. If the last statement is anything else, the source is returned
        // unchanged.
        //
        // This runs on every submission. The parse is cheap (microseconds) and
        // catches the most common REPL ergonomic failure ("var x = 5; x + 1;").
        private static string AutoReturnTrailingExpression(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;

            SyntaxTree tree;
            try
            {
                tree = CSharpSyntaxTree.ParseText(code,
                    new CSharpParseOptions(kind: SourceCodeKind.Script));
            }
            catch
            {
                // Parser shouldn't throw for any input — but if it did, fall
                // through and let the compiler report the real error.
                return code;
            }

            var root = (CompilationUnitSyntax)tree.GetRoot();
            var lastMember = root.Members.LastOrDefault();
            if (lastMember is not GlobalStatementSyntax gs) return code;
            if (gs.Statement is not ExpressionStatementSyntax exprStmt) return code;
            if (IsValidExpressionStatement(exprStmt.Expression)) return code;

            var semi = exprStmt.SemicolonToken;
            if (semi.IsMissing || !semi.IsKind(SyntaxKind.SemicolonToken)) return code;

            return code.Remove(semi.SpanStart, semi.Span.Length);
        }

        // The C# spec's allowed-as-statement expression categories. Everything
        // else (binary expression, member access, literal, identifier, cast,
        // etc.) is CS0201 and is the candidate for auto-return.
        private static bool IsValidExpressionStatement(ExpressionSyntax expr) => expr switch
        {
            AssignmentExpressionSyntax => true,
            InvocationExpressionSyntax => true,
            ObjectCreationExpressionSyntax => true,
            ImplicitObjectCreationExpressionSyntax => true,
            AwaitExpressionSyntax => true,
            PostfixUnaryExpressionSyntax => true,
            PrefixUnaryExpressionSyntax pu =>
                pu.IsKind(SyntaxKind.PreIncrementExpression) ||
                pu.IsKind(SyntaxKind.PreDecrementExpression),
            _ => false,
        };

        private static DiagnosticInfo MapDiagnostic(Diagnostic d)
        {
            var span = d.Location.GetMappedLineSpan();
            int? line = span.IsValid ? span.StartLinePosition.Line + 1 : null;
            int? col = span.IsValid ? span.StartLinePosition.Character + 1 : null;
            return new DiagnosticInfo(d.Severity.ToString(), d.GetMessage(), line, col);
        }
    }
}
