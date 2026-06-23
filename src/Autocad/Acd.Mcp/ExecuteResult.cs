using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acd.Mcp
{
    // Result of executing a snippet. Lives at the project root because it's the
    // wire currency — both transport (Pipe) and execution (Scripting) reference it,
    // and the out-of-process bridge links this file directly.
    //
    // ReturnValueRepr is the human-display string (.ToString() of the value).
    // ReturnValueJson is the DTO-projected JSON when the value is non-null.
    //
    // ReturnValueRepr is [JsonIgnore] — it never goes on the wire. For the agent
    // it is pure waste: for a string return it is byte-identical to
    // ReturnValueJson, and for any other type ReturnValueJson carries equal-or-
    // richer information (the DTO projection, or a self-describing `$unsupported`
    // / `$serialization_error` marker when the value can't be projected). There
    // is no return shape where Repr tells the agent something Json doesn't, and
    // Json is null only when the value itself is null — so dropping Repr removes
    // a duplicate without opening a blind spot. Its one real consumer is the WPF
    // palette (LogEntryViewModel), which reads this record live in-process and
    // never deserializes JSON, so [JsonIgnore] leaves the palette untouched.
    //
    // Stdout / Stderr / Diagnostics are omitted from the wire when empty
    // (WhenWritingNull + the factories pass null for the empty case) — a
    // side-effect-only snippet shouldn't spend tokens on `"stdout":""` and
    // `"diagnostics":[]`.
    //
    // ReturnValueJson is a JsonElement (not a string of JSON) on purpose: the
    // value is already a JSON value, and every hop that re-serializes
    // ExecuteResult (the pipe frame, then the MCP SDK) embeds a JsonElement raw.
    // Typing it as a string would make those serializers escape every quote —
    // double-encoding the payload into a "..." blob, roughly doubling its size.
    // JsonElement round-trips losslessly across both hops; deserialization clones
    // it so it survives the source document's disposal.
    public sealed record ExecuteResult(
        bool Success,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stdout,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stderr,
        [property: JsonIgnore] string? ReturnValueRepr,
        JsonElement? ReturnValueJson,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticInfo[]? Diagnostics,
        long ElapsedMs)
    {
        public static ExecuteResult Ok(string? returnValueRepr, JsonElement? returnValueJson, long elapsedMs) =>
            new(true, null, null, returnValueRepr, returnValueJson, null, elapsedMs);

        public static ExecuteResult CompileError(DiagnosticInfo[] diagnostics, long elapsedMs) =>
            new(false, null, null, null, null, diagnostics, elapsedMs);

        public static ExecuteResult Runtime(string error, long elapsedMs) =>
            new(false, null, error, null, null, null, elapsedMs);
    }

    public sealed record DiagnosticInfo(string Severity, string Message, int? Line, int? Column);
}
