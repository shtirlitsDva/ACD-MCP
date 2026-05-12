using System;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class OutcomeTests
    {
        [Fact]
        public void Pass_MatchesGenericFunc()
        {
            Outcome<int> o = Outcome.Pass(42);
            var got = o.Match(
                onPass: v => "p" + v,
                onSkip: r => "s" + r,
                onFailure: e => "f" + e.Message);
            Assert.Equal("p42", got);
        }

        [Fact]
        public void Skip_MatchesGenericFunc()
        {
            Outcome<int> o = Outcome.Skip<int>("nope");
            Assert.Equal("snope", o.Match(v => "p", r => "s" + r, e => "f"));
        }

        [Fact]
        public void Failure_MatchesGenericFunc()
        {
            Outcome<int> o = Outcome.Failure<int>(new InvalidOperationException("boom"));
            Assert.Equal("fboom", o.Match(v => "p", r => "s", e => "f" + e.Message));
        }

        [Fact]
        public void FlagsExposeTheCase()
        {
            Outcome<int> p = Outcome.Pass(1);
            Outcome<int> s = Outcome.Skip<int>("x");
            Outcome<int> f = Outcome.Failure<int>(new Exception());

            Assert.True(p.IsPass);
            Assert.False(p.IsSkip);
            Assert.False(p.IsFailure);

            Assert.False(s.IsPass);
            Assert.True(s.IsSkip);
            Assert.False(s.IsFailure);

            Assert.False(f.IsPass);
            Assert.False(f.IsSkip);
            Assert.True(f.IsFailure);
        }

        [Fact]
        public void Hierarchy_IsSealed()
        {
            // Asserting at the type level that no external subclass can be
            // constructed: every nested derived record is `sealed`, and the
            // base class has a private-protected constructor.
            var pass = typeof(Outcome<int>.Pass);
            var skip = typeof(Outcome<int>.Skip);
            var fail = typeof(Outcome<int>.Failure);

            Assert.True(pass.IsSealed);
            Assert.True(skip.IsSealed);
            Assert.True(fail.IsSealed);
        }
    }
}
