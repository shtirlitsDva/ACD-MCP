using System.Text.Json;

namespace Acd.Mcp
{
    // Result of executing a snippet. Lives at the project root because it's the
    // wire currency — both transport (Pipe) and execution (Scripting) reference it,
    // and the out-of-process bridge links this file directly.
    //
    // ReturnValueRepr is the human-display string (.ToString() of the value).
    // ReturnValueJson is the DTO-projected JSON when the value is non-null.
    //
    // It is a JsonElement (not a string of JSON) on purpose: the value is
    // already a JSON value, and every hop that re-serializes ExecuteResult (the
    // pipe frame, then the MCP SDK) embeds a JsonElement raw. Typing it as a
    // string would make those serializers escape every quote — double-encoding
    // the payload into a "..." blob, roughly doubling its size. JsonElement
    // round-trips losslessly across both hops; deserialization clones it so it
    // survives the source document's disposal. The palette still shows
    // ReturnValueRepr for at-a-glance display.
    public sealed record ExecuteResult(
        bool Success,
        string? Stdout,
        string? Stderr,
        string? ReturnValueRepr,
        JsonElement? ReturnValueJson,
        DiagnosticInfo[] Diagnostics,
        long ElapsedMs)
    {
        public static ExecuteResult Ok(string? returnValueRepr, JsonElement? returnValueJson, long elapsedMs) =>
            new(true, null, null, returnValueRepr, returnValueJson, [], elapsedMs);

        public static ExecuteResult CompileError(DiagnosticInfo[] diagnostics, long elapsedMs) =>
            new(false, null, null, null, null, diagnostics, elapsedMs);

        public static ExecuteResult Runtime(string error, long elapsedMs) =>
            new(false, null, error, null, null, [], elapsedMs);
    }

    public sealed record DiagnosticInfo(string Severity, string Message, int? Line, int? Column);
}
