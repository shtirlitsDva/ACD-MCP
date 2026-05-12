using System;
using System.Threading;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class StepBuilderTests
    {
        private static BatchContext NewCtx() =>
            new BatchContext(new BatchStateBag(), BatchPhase.Test, CancellationToken.None);

        [Fact]
        public void RequireTruePredicate_Then_ApplyRuns_Records_Pass()
        {
            var ctx = NewCtx();
            bool appliedRan = false;
            var outcome = ctx.Step("a")
                .Require("ok", () => true)
                .Apply(() => { appliedRan = true; return "done"; });

            Assert.True(appliedRan);
            Assert.IsType<StepOutcome.Pass>(outcome);
            var pass = (StepOutcome.Pass)outcome;
            Assert.Equal("done", pass.Summary);
            Assert.Single(ctx.Steps);
            Assert.False(ctx.HasFailures);
        }

        [Fact]
        public void RequireFalsePredicate_ShortCircuits_To_Skipped_Apply_DoesNotRun()
        {
            var ctx = NewCtx();
            bool appliedRan = false;
            var outcome = ctx.Step("a")
                .Require("nope", () => false)
                .Apply(() => { appliedRan = true; return "done"; });

            Assert.False(appliedRan);
            Assert.IsType<StepOutcome.Skipped>(outcome);
            Assert.Equal("nope", ((StepOutcome.Skipped)outcome).FailingRequirement);
            Assert.False(ctx.HasFailures);
        }

        [Fact]
        public void PredicateThrows_RecordsFailure_And_FlipsHasFailures()
        {
            var ctx = NewCtx();
            var outcome = ctx.Step("a")
                .Require("oops", () => throw new InvalidOperationException("boom"))
                .Apply(() => "done");

            Assert.IsType<StepOutcome.Failure>(outcome);
            Assert.True(ctx.HasFailures);
        }

        [Fact]
        public void ApplyThrows_RecordsFailure_And_FlipsHasFailures()
        {
            var ctx = NewCtx();
            var outcome = ctx.Step("a")
                .Require("ok", () => true)
                .Apply(() => { throw new InvalidOperationException("boom"); });

            Assert.IsType<StepOutcome.Failure>(outcome);
            Assert.True(ctx.HasFailures);
        }

        [Fact]
        public void MultipleRequires_RunInOrder_ShortCircuitsOnFirstFalse()
        {
            var ctx = NewCtx();
            bool secondCalled = false;
            var outcome = ctx.Step("a")
                .Require("first", () => false)
                .Require("second", () => { secondCalled = true; return true; })
                .Apply(() => "done");

            Assert.IsType<StepOutcome.Skipped>(outcome);
            Assert.False(secondCalled);
            Assert.Equal("first", ((StepOutcome.Skipped)outcome).FailingRequirement);
        }

        [Fact]
        public void CtxFail_RecordsFailureStep_And_FlipsHasFailures()
        {
            var ctx = NewCtx();
            ctx.Fail("explicit");
            Assert.True(ctx.HasFailures);
            Assert.Single(ctx.Steps);
            Assert.IsType<StepOutcome.Failure>(ctx.Steps[0]);
        }

        [Fact]
        public void BatchState_ReturnsSameInstanceAcrossCalls()
        {
            var ctx = NewCtx();
            var a = ctx.BatchState<TestCounter>();
            var b = ctx.BatchState<TestCounter>();
            Assert.Same(a, b);
        }

        private sealed class TestCounter { public int Next; }
    }
}
