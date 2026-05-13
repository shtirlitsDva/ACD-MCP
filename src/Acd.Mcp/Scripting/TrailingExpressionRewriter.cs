using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acd.Mcp.Scripting
{
    // REPL ergonomic: if the user's last statement is a bare value-shaped
    // expression ending with `;` (e.g. `x * 10;`, `42;`, `new List<int> { 1, 2 };`),
    // strip the trailing semicolon so CSharpScript treats the dangling
    // expression as the submission's return value. Same convention as
    // LINQPad / `dotnet-script` / F# Interactive, with one deliberate
    // deviation: we ALSO auto-return object-creation expressions
    // (`new T(...);`), since "construct and discard" is almost never the
    // intent in a REPL — see <auto-return-rules> below.
    //
    // Pure Roslyn syntax work; no AutoCAD dependency. Lives in its own
    // file so the test project can Compile-Include it without dragging
    // the entire ScriptSession surface (which DOES need AutoCAD).
    internal static class TrailingExpressionRewriter
    {
        public static string AutoReturnTrailingExpression(string code)
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
            if (IsSideEffectStatement(exprStmt.Expression)) return code;

            var semi = exprStmt.SemicolonToken;
            if (semi.IsMissing || !semi.IsKind(SyntaxKind.SemicolonToken)) return code;

            return code.Remove(semi.SpanStart, semi.Span.Length);
        }

        // <auto-return-rules>
        // Expression-statement categories the REPL treats as "side-effect
        // intent": we LEAVE the trailing `;` in place and submit as a void
        // statement. Everything else — literals, binary expressions, member
        // access, array creation, anonymous-object creation, AND object
        // creation — has the semicolon stripped so the value flows back as
        // the submission's return.
        //
        // The C# spec also permits `new T(...)` as an expression statement,
        // but in a REPL the intent of `new List<int> { 1, 2, 3 };` is
        // overwhelmingly "give me the list", not "construct and discard".
        // We deviate from the spec here deliberately; users who really want
        // a side-effect-only constructor write `_ = new Foo();` instead.
        // </auto-return-rules>
        internal static bool IsSideEffectStatement(ExpressionSyntax expr) => expr switch
        {
            AssignmentExpressionSyntax => true,
            InvocationExpressionSyntax => true,
            AwaitExpressionSyntax => true,
            PostfixUnaryExpressionSyntax => true,
            PrefixUnaryExpressionSyntax pu =>
                pu.IsKind(SyntaxKind.PreIncrementExpression) ||
                pu.IsKind(SyntaxKind.PreDecrementExpression),
            _ => false,
        };
    }
}
