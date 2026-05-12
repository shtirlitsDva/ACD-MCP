namespace Acd.Mcp
{
    // Result of executing a snippet. Lives at the project root because it's the
    // wire currency — both transport (Pipe) and execution (Scripting) reference it,
    // and the out-of-process bridge links this file directly.
    //
    // ReturnValueRepr is the human-display string (.ToString() of the value).
    // ReturnValueJson is the DTO-projected JSON when the value is non-null; an
    // agent consuming the MCP tool can parse this directly, while the palette
    // shows ReturnValueRepr for at-a-glance display.
    public sealed record ExecuteResult(
        bool Success,
        string? Stdout,
        string? Stderr,
        string? ReturnValueRepr,
        string? ReturnValueJson,
        DiagnosticInfo[] Diagnostics,
        long ElapsedMs)
    {
        public static ExecuteResult Ok(string? returnValueRepr, string? returnValueJson, long elapsedMs) =>
            new(true, null, null, returnValueRepr, returnValueJson, [], elapsedMs);

        public static ExecuteResult CompileError(DiagnosticInfo[] diagnostics, long elapsedMs) =>
            new(false, null, null, null, null, diagnostics, elapsedMs);

        public static ExecuteResult Runtime(string error, long elapsedMs) =>
            new(false, null, error, null, null, [], elapsedMs);
    }

    public sealed record DiagnosticInfo(string Severity, string Message, int? Line, int? Column);
}
