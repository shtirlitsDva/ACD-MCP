using Xunit;
using Acd.Mcp.Scripting;

namespace Acd.Mcp.Tests;

// Pins the auto-return semantics documented in the /acd-mcp:script skill's
// <trailing-expression-return-and-auto-return-gotchas> section. The cases
// here are exactly the rows in the table users / agents read.
public class TrailingExpressionRewriterTests
{
    [Theory]
    [InlineData("42;",                       "42")]           // literal
    [InlineData("x * 10;",                   "x * 10")]       // binary
    [InlineData("Doc.Name;",                 "Doc.Name")]     // member access
    [InlineData("new { foo = 1 };",          "new { foo = 1 }")]   // anonymous obj
    [InlineData("new int[] { 1, 2, 3 };",    "new int[] { 1, 2, 3 }")] // array
    [InlineData("new List<int> { 1, 2 };",   "new List<int> { 1, 2 }")] // object creation
    [InlineData("new MyService();",          "new MyService()")]        // object creation, no init
    public void Strips_trailing_semicolon_on_value_shaped_expressions(string input, string expected)
    {
        Assert.Equal(expected, TrailingExpressionRewriter.AutoReturnTrailingExpression(input));
    }

    [Theory]
    [InlineData("someMethod();")]   // invocation — side-effect intent
    [InlineData("x = 5;")]          // assignment
    [InlineData("x++;")]            // postfix increment
    [InlineData("++x;")]            // prefix increment
    [InlineData("--x;")]            // prefix decrement
    [InlineData("x--;")]            // postfix decrement
    public void Leaves_side_effect_statements_alone(string input)
    {
        Assert.Equal(input, TrailingExpressionRewriter.AutoReturnTrailingExpression(input));
    }

    [Fact]
    public void Strips_trailing_only_on_the_last_statement()
    {
        // Earlier statements are untouched even if they look value-shaped.
        // Only the trailing one's `;` is candidate for stripping.
        var input = "var a = 1;\nvar b = 2;\na + b;";
        var expected = "var a = 1;\nvar b = 2;\na + b";
        Assert.Equal(expected, TrailingExpressionRewriter.AutoReturnTrailingExpression(input));
    }

    [Fact]
    public void Empty_input_passes_through()
    {
        Assert.Equal("", TrailingExpressionRewriter.AutoReturnTrailingExpression(""));
        Assert.Equal("   ", TrailingExpressionRewriter.AutoReturnTrailingExpression("   "));
    }

    [Fact]
    public void Discard_pattern_keeps_object_creation_as_statement()
    {
        // Users who want side-effect-only `new` use the discard assignment;
        // assignment IS a side-effect statement, so the `;` stays and the
        // value isn't returned. This is the documented escape hatch in
        // the /script skill's <auto-return-gotchas>.
        var input = "_ = new MyService();";
        Assert.Equal(input, TrailingExpressionRewriter.AutoReturnTrailingExpression(input));
    }
}
