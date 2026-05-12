namespace Acd.Mcp.Scripting
{
    public sealed record ExecuteResult(
        bool Success,
        string? Stdout,
        string? Stderr,
        string? ReturnValueRepr,
        DiagnosticInfo[] Diagnostics,
        long ElapsedMs)
    {
        public static ExecuteResult Ok(string? returnValueRepr, long elapsedMs) =>
            new(true, null, null, returnValueRepr, [], elapsedMs);

        public static ExecuteResult CompileError(DiagnosticInfo[] diagnostics, long elapsedMs) =>
            new(false, null, null, null, diagnostics, elapsedMs);

        public static ExecuteResult Runtime(string error, long elapsedMs) =>
            new(false, null, error, null, [], elapsedMs);
    }

    public sealed record DiagnosticInfo(string Severity, string Message, int? Line, int? Column);
}
