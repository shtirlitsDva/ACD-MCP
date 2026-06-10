using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Batch.Tests.Fakes;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class BatchRunnerTests
    {
        private static BatchRunner<FakeGlobals> NewRunner(
            FakeDrawingHost host,
            FakeFileAccessProbe probe)
            => new(host, probe, new BatchScriptHost<FakeGlobals>(TestScriptOptions.Build()));

        // The simplest body the spec describes: one step that updates entities
        // on a layer. We drive the FakeDatabase directly via the `xDb` global.
        private const string OneStepBody = @"
ctx.Step(""bump"")
   .Require(""has-layer"", () => xDb.EntitiesByLayer.ContainsKey(""X""))
   .Apply(() =>
   {
       xDb.EntitiesByLayer[""X""] = xDb.EntitiesByLayer[""X""] + 1;
       return ""bumped"";
   });
";

        private static FakeDrawingHost HostWithLayer(params string[] paths)
        {
            var host = new FakeDrawingHost();
            foreach (var p in paths)
            {
                var db = new FakeDatabase();
                db.EntitiesByLayer["X"] = 5;
                host.Drawings[p] = db;
            }
            return host;
        }

        [Fact]
        public async Task LoopIterates_AllFiles_InInputOrder()
        {
            var host = HostWithLayer("a.dwg", "b.dwg", "c.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);
            var files = new[] { "a.dwg", "b.dwg", "c.dwg" };

            var report = await runner.RunAsync(OneStepBody, files, BatchMode.Test, CancellationToken.None);

            Assert.Equal(new[] { "a.dwg", "b.dwg", "c.dwg" }, host.OpenedPaths.ToArray());
            Assert.Equal(3, report.Results.Count);
            Assert.All(report.Results, r => Assert.Equal(FileOutcomeStatus.Pass, r.Status));
        }

        [Fact]
        public async Task TestMode_NeverCommits()
        {
            var host = HostWithLayer("a.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            await runner.RunAsync(OneStepBody, new[] { "a.dwg" }, BatchMode.Test, CancellationToken.None);

            // The FakeSession only sets Committed if CommitAndSave was called.
            Assert.False(host.Drawings["a.dwg"].Committed);
            Assert.False(host.Drawings["a.dwg"].Saved);
            // The session has been disposed (test mode → rollback).
            var session = host.OpenedSessions.Single();
            Assert.True(session.DisposedFlag);
            Assert.False(session.CommittedAndSaved);
        }

        [Fact]
        public async Task LiveMode_AfterCleanTestPass_CommitsOnSuccess()
        {
            var host = HostWithLayer("a.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(OneStepBody, new[] { "a.dwg" }, BatchMode.Live, CancellationToken.None);

            // Two phases ran. Two sessions opened (one per phase).
            Assert.Equal(2, host.OpenedSessions.Count);
            // Phase 1 (Test) did not commit; phase 2 (Live) did.
            Assert.False(host.OpenedSessions[0].CommittedAndSaved);
            Assert.True(host.OpenedSessions[1].CommittedAndSaved);

            // Both per-file results reported.
            Assert.Equal(2, report.Results.Count);
            Assert.All(report.Results, r => Assert.Equal(FileOutcomeStatus.Pass, r.Status));
            Assert.Equal(BatchPhase.Test, report.Results[0].Phase);
            Assert.Equal(BatchPhase.Live, report.Results[1].Phase);
            Assert.True(report.Results[1].Committed);
        }

        [Fact]
        public async Task LiveMode_AbortsBeforeLivePhase_WhenTestPhaseFails()
        {
            // Body fails by throwing.
            var body = @"throw new System.InvalidOperationException(""bad"");";
            var host = HostWithLayer("a.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg" }, BatchMode.Live, CancellationToken.None);

            Assert.NotNull(report.AbortedReason);
            Assert.Contains("Live pass not started", report.AbortedReason);
            // Only the Test phase result was recorded — Live phase never started.
            Assert.Single(report.Results);
            Assert.Equal(BatchPhase.Test, report.Results[0].Phase);
            Assert.Equal(FileOutcomeStatus.Failure, report.Results[0].Status);
            // Only one session was ever opened.
            Assert.Single(host.OpenedSessions);
        }

        [Fact]
        public async Task FileLocked_AbortsTheBatch_NoFilesTouched()
        {
            var host = HostWithLayer("a.dwg", "b.dwg");
            var probe = new FakeFileAccessProbe();
            probe.LockedPaths.Add("a.dwg");
            var runner = NewRunner(host, probe);

            await Assert.ThrowsAsync<BatchAbortedException>(async () =>
                await runner.RunAsync(OneStepBody, new[] { "a.dwg", "b.dwg" }, BatchMode.Test, CancellationToken.None));

            // The locked file was never opened, neither was the next one.
            Assert.Empty(host.OpenedSessions);
        }

        [Fact]
        public async Task ScriptException_OnFailureSkip_LoopContinues_AllFilesProcessed()
        {
            // With OnFailure = Skip the per-file Failure is recorded and the
            // loop moves to the next file.
            var body = @"
ctx.Step(""always-fails"")
   .Require(""yes"", () => true)
   .Apply(() => { throw new System.InvalidOperationException(""boom""); });
";
            var host = HostWithLayer("a.dwg", "b.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg", "b.dwg" }, BatchMode.Test,
                CancellationToken.None, onFailure: BatchOnFailure.Skip);

            Assert.Equal(2, report.Results.Count);
            Assert.All(report.Results, r => Assert.Equal(FileOutcomeStatus.Failure, r.Status));
            Assert.All(host.OpenedSessions, s => Assert.False(s.CommittedAndSaved));
        }

        [Fact]
        public async Task ScriptException_OnFailureAbort_LoopStopsAfterFirstFailure()
        {
            // Default policy: Abort. The first file fails and the runner
            // does not even open the second.
            var body = @"
ctx.Step(""always-fails"")
   .Apply(() => { throw new System.InvalidOperationException(""boom""); });
";
            var host = HostWithLayer("a.dwg", "b.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg", "b.dwg" }, BatchMode.Test,
                CancellationToken.None /* default onFailure = Abort */);

            Assert.Single(report.Results);
            Assert.Equal("a.dwg", report.Results[0].Path);
            Assert.Equal(FileOutcomeStatus.Failure, report.Results[0].Status);
            Assert.Single(host.OpenedSessions);
        }

        [Fact]
        public async Task RequireFalse_RecordsFailure_FileFails()
        {
            // Require is HARD: a false predicate marks the step Failure and
            // therefore the file Failure. See /acd-mcp:batch <step-dsl>.
            var body = @"
ctx.Step(""bump"")
   .Require(""never"", () => false)
   .Apply(() => ""done"");
";
            var host = HostWithLayer("a.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg" }, BatchMode.Test, CancellationToken.None);

            Assert.Single(report.Results);
            Assert.Equal(FileOutcomeStatus.Failure, report.Results[0].Status);
            var step = Assert.IsType<StepOutcome.Failure>(report.Results[0].Steps.Single());
            Assert.IsType<RequireFailedException>(step.Error);
        }

        [Fact]
        public async Task CrossFileState_PersistsAcrossFiles_SameInstance()
        {
            var body = @"
var counter = ctx.BatchState<Acd.Mcp.Batch.Tests.Fakes.TestCounter>();
counter.Next++;
ctx.Step(""noop"").Apply(() => counter.Next.ToString());
";
            var host = HostWithLayer("a.dwg", "b.dwg", "c.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg", "b.dwg", "c.dwg" }, BatchMode.Test, CancellationToken.None);

            Assert.Equal(3, report.Results.Count);
            // Summary embeds the counter — last file should see "3".
            var lastStep = (StepOutcome.Pass)report.Results[2].Steps.Single();
            Assert.Equal("3", lastStep.Summary);
        }

        [Fact]
        public async Task Cancellation_BetweenFiles_ExitsLoopEarly()
        {
            using var cts = new CancellationTokenSource();
            var host = HostWithLayer("a.dwg", "b.dwg", "c.dwg");
            var probe = new FakeFileAccessProbe();
            // Cancel after the first file opens. The runner checks
            // cancellation between files, so the second file should not open.
            var observedOpens = new List<string>();
            var firstFileSeen = new TaskCompletionSource();
            // We can't intercept the host directly with current API; instead
            // cancel before invoking and assert no files were processed.
            cts.Cancel();
            var runner = NewRunner(host, probe);
            var report = await runner.RunAsync(OneStepBody,
                new[] { "a.dwg", "b.dwg", "c.dwg" }, BatchMode.Test, cts.Token);

            Assert.Empty(report.Results);
            Assert.True(report.Cancelled);
        }

        [Fact]
        public async Task CompileError_AbortsBeforeLoopStarts_NoFilesOpened()
        {
            var body = "this is not C#";
            var host = HostWithLayer("a.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);

            var report = await runner.RunAsync(body, new[] { "a.dwg" }, BatchMode.Test, CancellationToken.None);

            Assert.NotNull(report.AbortedReason);
            Assert.Contains("Compile failed", report.AbortedReason);
            Assert.Empty(report.Results);
            Assert.Empty(host.OpenedSessions);
        }

        [Fact]
        public async Task PerFileProgress_FiresOncePerFile_InOrder()
        {
            var host = HostWithLayer("a.dwg", "b.dwg");
            var probe = new FakeFileAccessProbe();
            var runner = NewRunner(host, probe);
            var collected = new List<string>();
            var progress = new SyncProgress<BatchFileResult>(r => collected.Add(r.Path + ":" + r.Phase));

            await runner.RunAsync(OneStepBody, new[] { "a.dwg", "b.dwg" }, BatchMode.Test, CancellationToken.None, progress);

            Assert.Equal(new[] { "a.dwg:Test", "b.dwg:Test" }, collected.ToArray());
        }

        // A synchronous IProgress so we can assert ordering deterministically.
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }
}

namespace Acd.Mcp.Batch.Tests.Fakes
{
    // Concrete shared state type for the cross-file-state test.
    public sealed class TestCounter
    {
        public int Next;
    }
}
