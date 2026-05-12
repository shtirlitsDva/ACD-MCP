using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Acd.Mcp.Batch
{
    // Compiles a script body into a callable delegate, parameterised on a
    // host-provided globals type. The globals type is what the script body
    // calls into for `xDb`, `xTx`, `ctx`, helpers etc. The runtime is
    // generic over that type — `Acd.Mcp.Batch` never references AutoCAD.
    //
    // Caching:
    //   - Key is SHA256(body) — same body, same delegate, regardless of
    //     mode or flavor. Mode is runtime-passed via globals; flavor is a
    //     header comment with no semantic effect on compilation.
    //   - Cache lives on the host instance. Hosts created per-run are cheap.
    //     Long-lived hosts amortise compile cost across many runs.
    //
    // Compile errors:
    //   - Surfaced via CompilationErrorException → mapped to a
    //     BatchScriptDiagnostic list. The runner displays them to the user
    //     and refuses to start the loop. No file is touched.
    //
    // Diagnostic line/column reporting:
    //   - Roslyn already reports positions relative to the script source.
    //     We don't wrap the script in a synthetic outer template, so the
    //     positions are 1:1 with the user-visible body.
    public sealed class BatchScriptHost<TGlobals> where TGlobals : class
    {
        private readonly ScriptOptions _options;
        private readonly ConcurrentDictionary<string, Lazy<CompiledScript>> _cache = new();

        public BatchScriptHost(ScriptOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        // Returns either a compiled script or a list of diagnostics. Pure
        // function — no side effects beyond cache population.
        public Outcome<CompiledScript> Compile(string body)
        {
            var key = Hash(body);
            // Lazy<T> ensures concurrent callers with the same key share a
            // single compile attempt and a single exception, not duplicate
            // work.
            var lazy = _cache.GetOrAdd(key, _ => new Lazy<CompiledScript>(() =>
            {
                var script = CSharpScript.Create(body, _options, typeof(TGlobals));
                // Forces compilation right now; without this the first
                // RunAsync call would surface CompilationErrorException
                // instead of letting us catch it here.
                var diags = script.Compile();
                return new CompiledScript(script, diags);
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var compiled = lazy.Value;
                var errors = compiled.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToArray();
                if (errors.Length == 0) return Outcome.Pass(compiled);
                // Replace the cached entry so a subsequent edit can retry.
                _cache.TryRemove(key, out _);
                return Outcome.Failure<CompiledScript>(new BatchCompilationException(errors));
            }
            catch (CompilationErrorException cex)
            {
                _cache.TryRemove(key, out _);
                return Outcome.Failure<CompiledScript>(
                    new BatchCompilationException(cex.Diagnostics.ToArray()));
            }
            catch (Exception ex)
            {
                _cache.TryRemove(key, out _);
                return Outcome.Failure<CompiledScript>(ex);
            }
        }

        public async Task<ScriptState<object?>> RunAsync(
            CompiledScript script, TGlobals globals, CancellationToken ct)
            => await script.Script.RunAsync(globals, ct).ConfigureAwait(false);

        private static string Hash(string body)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // A compiled script plus its diagnostics (warnings + errors).
    public sealed class CompiledScript
    {
        public Script<object?> Script { get; }
        public Microsoft.CodeAnalysis.Diagnostic[] Diagnostics { get; }

        public CompiledScript(Script<object?> script, System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
        {
            Script = script;
            Diagnostics = diagnostics.ToArray();
        }
    }

    // Carries Roslyn diagnostics through the Outcome.Failure case. The runner
    // displays them to the user as compile errors and aborts the loop.
    public sealed class BatchCompilationException : Exception
    {
        public Microsoft.CodeAnalysis.Diagnostic[] Diagnostics { get; }

        public BatchCompilationException(Microsoft.CodeAnalysis.Diagnostic[] diagnostics)
            : base("Script body failed to compile. " + diagnostics.Length + " error(s).")
        {
            Diagnostics = diagnostics;
        }
    }
}
