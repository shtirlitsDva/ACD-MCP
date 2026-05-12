using System.Linq;
using Acd.Mcp.Batch.Tests.Fakes;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class BatchScriptHostTests
    {
        private static BatchScriptHost<FakeGlobals> Host() =>
            new(TestScriptOptions.Build());

        [Fact]
        public void Compile_Pass_OnValidBody()
        {
            var host = Host();
            var body = "var x = 1 + 2;";
            var result = host.Compile(body);
            Assert.True(result.IsPass);
        }

        [Fact]
        public void Compile_Failure_OnBrokenBody_Carries_Diagnostics()
        {
            var host = Host();
            var body = "this is not valid C#";
            var result = host.Compile(body);
            Assert.True(result.IsFailure);
            var ex = ((Outcome<CompiledScript>.Failure)result).Error;
            var compileEx = Assert.IsType<BatchCompilationException>(ex);
            Assert.NotEmpty(compileEx.Diagnostics);
        }

        [Fact]
        public void Compile_Caches_ByBodyHash()
        {
            var host = Host();
            var r1 = host.Compile("var x = 1;");
            var r2 = host.Compile("var x = 1;");
            Assert.True(r1.IsPass);
            Assert.True(r2.IsPass);
            var s1 = ((Outcome<CompiledScript>.Pass)r1).Value;
            var s2 = ((Outcome<CompiledScript>.Pass)r2).Value;
            Assert.Same(s1, s2);
        }

        [Fact]
        public void Compile_DistinctBodies_ProduceDistinctCachedScripts()
        {
            var host = Host();
            var r1 = host.Compile("var x = 1;");
            var r2 = host.Compile("var x = 2;");
            var s1 = ((Outcome<CompiledScript>.Pass)r1).Value;
            var s2 = ((Outcome<CompiledScript>.Pass)r2).Value;
            Assert.NotSame(s1, s2);
        }

        [Fact]
        public void Diagnostics_Line_IsRelativeToTheBody()
        {
            var host = Host();
            var body = "var x = 1;\nthisIsBroken();";
            var result = host.Compile(body);
            Assert.True(result.IsFailure);
            var ex = (BatchCompilationException)((Outcome<CompiledScript>.Failure)result).Error;
            // The error must come from line 2 (the broken call).
            var lineSpan = ex.Diagnostics.First().Location.GetMappedLineSpan();
            Assert.True(lineSpan.IsValid);
            Assert.Equal(1, lineSpan.StartLinePosition.Line);
        }
    }
}
