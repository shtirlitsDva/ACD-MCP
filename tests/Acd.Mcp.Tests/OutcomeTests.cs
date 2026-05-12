using Xunit;
using Acd.Mcp;

namespace Acd.Mcp.Tests;

public class OutcomeTests
{
    [Fact]
    public void Ok_yields_Success_carrying_value()
    {
        var o = Outcome<int>.Ok(42);
        Assert.IsType<Outcome<int>.Success>(o);
        Assert.Equal(42, ((Outcome<int>.Success)o).Value);
    }

    [Fact]
    public void Fail_yields_Failure_carrying_message()
    {
        var ex = new System.InvalidOperationException("boom");
        var o = Outcome<string>.Fail("nope", ex);
        var f = Assert.IsType<Outcome<string>.Failure>(o);
        Assert.Equal("nope", f.Message);
        Assert.Same(ex, f.Cause);
    }

    [Fact]
    public void Match_dispatches_to_correct_branch()
    {
        var success = Outcome<int>.Ok(5);
        var failure = Outcome<int>.Fail("err");

        Assert.Equal("got 5", success.Match(v => $"got {v}", (m, _) => m));
        Assert.Equal("err", failure.Match(v => $"got {v}", (m, _) => m));
    }

    [Fact]
    public void TryGet_returns_true_for_Success()
    {
        var o = Outcome<int>.Ok(7);
        Assert.True(o.TryGet(out var v));
        Assert.Equal(7, v);
    }

    [Fact]
    public void TryGet_returns_false_for_Failure()
    {
        var o = Outcome<int>.Fail("x");
        Assert.False(o.TryGet(out _));
    }
}
