using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Acd.Mcp.Serialization
{
    // Tracks every DTO file that failed to compile (or whose header type
    // couldn't be resolved). The DtoConverter consults this to enrich the
    // `$unsupported` marker with a `reason` field carrying the live
    // diagnostic — line/column/CSxxxx — so the agent can fix the error
    // without reading the SafeBoundary log.
    //
    // Two dictionaries:
    //   _byType    — failure keyed by resolved Type. Looked up by the converter.
    //   _bySource  — failure keyed by source tag (e.g. "user:Circle.csx"). Used
    //                when the @dto header type didn't resolve at all (e.g. typo
    //                in the namespace). Surfaces via the diagnostics resource
    //                so the user still sees orphan failures.
    //
    // Both dictionaries are cleared on every DtoLoader.ReloadAll so stale
    // entries from a now-fixed file don't linger.
    public sealed class DtoDiagnostics
    {
        private readonly ConcurrentDictionary<Type, DtoCompileFailure> _byType = new();
        private readonly ConcurrentDictionary<string, DtoCompileFailure> _bySource = new();

        public void RecordFailure(DtoCompileFailure failure)
        {
            if (failure is null) throw new ArgumentNullException(nameof(failure));
            _bySource[failure.Source] = failure;
            if (failure.ResolvedType is not null)
                _byType[failure.ResolvedType] = failure;
        }

        // Called by the loader after a successful compile of a particular
        // file: any previously-recorded failure for this source should be
        // cleared so the agent doesn't see stale errors.
        public void ClearForSource(string source)
        {
            if (_bySource.TryRemove(source, out var prior) && prior.ResolvedType is not null)
            {
                // Only remove the type entry if it's the SAME failure we
                // recorded (another file may have failed for the same type
                // — unlikely, but be defensive).
                _byType.TryRemove(new KeyValuePair<Type, DtoCompileFailure>(prior.ResolvedType, prior));
            }
        }

        public void Clear()
        {
            _byType.Clear();
            _bySource.Clear();
        }

        public DtoCompileFailure? TryGet(Type t)
        {
            return _byType.TryGetValue(t, out var f) ? f : null;
        }

        public IReadOnlyList<DtoCompileFailure> All =>
            _bySource.Values.OrderBy(f => f.Source, StringComparer.Ordinal).ToList();

        // Parse Roslyn's compile-error text into structured fields. Roslyn
        // emits per-diagnostic lines like:
        //   (12,8): error CS1061: 'Circle' does not contain a definition for 'X'
        // We surface the FIRST diagnostic — multiple errors are typically
        // cascading from the first.
        private static readonly Regex DiagPattern = new(
            @"^\s*\((?<line>\d+),(?<col>\d+)\):\s*(?:error|warning)\s+(?<code>CS\d+):\s*(?<message>.+)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static (int? line, int? column, string? code, string message) ParseFirstDiagnostic(string raw)
        {
            var m = DiagPattern.Match(raw ?? "");
            if (!m.Success) return (null, null, null, raw ?? "");
            int.TryParse(m.Groups["line"].Value, out var line);
            int.TryParse(m.Groups["col"].Value, out var col);
            return (line, col, m.Groups["code"].Value, m.Groups["message"].Value.Trim());
        }
    }

    // One per failed compile attempt. The fields are deliberately flat so
    // the JSON shape exposed via acd-mcp://dto-system/diagnostics is easy
    // for the agent to parse.
    public sealed record DtoCompileFailure(
        string Source,         // e.g. "user:Circle.csx"
        string HeaderType,     // the // @dto: <Type> string, or "" if no header
        Type? ResolvedType,    // null when HeaderType couldn't be resolved
        string Message,        // first-diagnostic message
        int? Line,
        int? Column,
        string? ErrorCode);    // e.g. "CS1061"
}
