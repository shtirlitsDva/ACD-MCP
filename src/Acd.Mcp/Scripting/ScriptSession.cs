using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
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
        private ScriptState? _state;
        private ScriptOptions _options = BuildOptions();

        // Read-only views for UI inspectors. CurrentState may be null before the
        // first successful submission and after Reset(). Both surfaces are safe
        // to read from the main thread between ExecuteAsync calls; readers must
        // not mutate the returned ScriptState.
        public ScriptState? CurrentState => _state;
        public AcadGlobals Globals => _globals;

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
            // Hand the script the same surface the host plugin has. Two-path
            // resolution per assembly:
            //   - non-empty Location  → MetadataReference.CreateFromFile(path)
            //   - byte[]-loaded ALC   → TryGetRawMetadata → AssemblyMetadata.Create
            //
            // The second path matters specifically for the Acd.Mcp plugin itself
            // when running under DevReload's IsolatedPluginContext (LoadFromStream
            // gives Location=""). Without it the script can't see AcadGlobals.Doc
            // / .Db / .Ed even though they're passed as globals at runtime, because
            // Roslyn needs metadata for the globalsType's assembly to bind names.
            var refs = new List<MetadataReference>();
            var seen = new HashSet<Assembly>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                AddAssemblyReference(asm, refs, seen);

            // Belt-and-braces: explicitly ensure the assembly containing AcadGlobals
            // is referenced. AppDomain enumeration is usually sufficient, but this
            // guarantees the globalsType is resolvable even if some future loader
            // hides it from the AppDomain scan.
            AddAssemblyReference(typeof(AcadGlobals).Assembly, refs, seen);

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

        private static void AddAssemblyReference(
            Assembly asm, List<MetadataReference> refs, HashSet<Assembly> seen)
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
                // script session. Silently skip; missing references will surface
                // as CS errors against any code that actually depends on this
                // assembly, which is the correct outcome.
            }
        }

        // Build a MetadataReference for an assembly that has no on-disk Location
        // (typically a byte[]-loaded plugin in a custom AssemblyLoadContext). Uses
        // System.Reflection.Metadata's TryGetRawMetadata, which exposes the PE
        // metadata blob the runtime already holds in memory — zero file I/O.
        private static unsafe bool TryGetInMemoryReference(Assembly asm, out MetadataReference reference)
        {
            reference = null!;
            if (!asm.TryGetRawMetadata(out byte* blob, out int length)) return false;

            var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
            var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
            reference = assemblyMetadata.GetReference();
            return true;
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
