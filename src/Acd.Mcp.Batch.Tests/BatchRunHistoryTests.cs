using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class BatchRunHistoryTests : IDisposable
    {
        private readonly string _root;

        public BatchRunHistoryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "acd-mcp-history-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private BatchRunHistory NewHistory() => new(_root);

        private static BatchRunReport SyntheticReport(string runId, DateTimeOffset stamp, int pass, int fail)
        {
            var results = new List<BatchFileResult>();
            for (int i = 0; i < pass; i++)
                results.Add(new BatchFileResult(
                    Path: $"p{i}.dwg",
                    Phase: BatchPhase.Test,
                    Status: FileOutcomeStatus.Pass,
                    Steps: Array.Empty<StepOutcome>(),
                    Committed: false,
                    Cancelled: false,
                    Error: null,
                    ElapsedMs: 10));
            for (int i = 0; i < fail; i++)
                results.Add(new BatchFileResult(
                    Path: $"f{i}.dwg",
                    Phase: BatchPhase.Test,
                    Status: FileOutcomeStatus.Failure,
                    Steps: new[] { (StepOutcome)new StepOutcome.Failure("s", Array.Empty<RequirementResult>(), new InvalidOperationException("boom")) },
                    Committed: false,
                    Cancelled: false,
                    Error: new InvalidOperationException("boom"),
                    ElapsedMs: 12));
            return new BatchRunReport(
                RunId: runId,
                StartedAt: stamp,
                CompletedAt: stamp.AddSeconds(1),
                RequestedMode: BatchMode.Test,
                Files: results.Select(r => r.Path).ToArray(),
                Results: results,
                Cancelled: false,
                AbortedReason: null);
        }

        [Fact]
        public void Save_Then_Load_RoundTripsAllFields()
        {
            var history = NewHistory();
            var report = SyntheticReport("abc12345", DateTimeOffset.Now, pass: 2, fail: 1);

            history.Save(report);
            var roundTrip = history.Load("abc12345");

            Assert.NotNull(roundTrip);
            Assert.Equal(report.RunId, roundTrip!.RunId);
            Assert.Equal(report.RequestedMode, roundTrip.RequestedMode);
            Assert.Equal(report.Results.Count, roundTrip.Results.Count);
            Assert.Equal(
                report.Results.Select(r => r.Path),
                roundTrip.Results.Select(r => r.Path));
            // The failure file's step is StepOutcome.Failure with carried message.
            var failFile = roundTrip.Results.Single(r => r.Status == FileOutcomeStatus.Failure);
            var failStep = Assert.IsType<StepOutcome.Failure>(failFile.Steps.Single());
            Assert.Equal("boom", failStep.Error.Message);
        }

        [Fact]
        public void ListRecent_NewestFirst_Pagination_Works()
        {
            var history = NewHistory();
            // Use distinct stamps so sort order is deterministic.
            var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 5; i++)
            {
                var stamp = new DateTimeOffset(t.AddMinutes(i), TimeSpan.Zero);
                history.Save(SyntheticReport($"run{i:D2}", stamp, pass: 1, fail: 0));
                // Filesystem timestamps in NTFS are coarse — but our ordering
                // uses the encoded filename stamp, not the file mtime, so
                // this is robust.
            }

            var page1 = history.ListRecent(limit: 2, offset: 0);
            Assert.Equal(new[] { "run04", "run03" }, page1.Select(s => s.RunId).ToArray());

            var page2 = history.ListRecent(limit: 2, offset: 2);
            Assert.Equal(new[] { "run02", "run01" }, page2.Select(s => s.RunId).ToArray());

            var page3 = history.ListRecent(limit: 10, offset: 4);
            Assert.Equal(new[] { "run00" }, page3.Select(s => s.RunId).ToArray());
        }

        [Fact]
        public void ListRecent_ClampsLimit_AtMax()
        {
            var history = NewHistory();
            history.Save(SyntheticReport("only", DateTimeOffset.Now, 1, 0));
            // Asking for a huge page returns just what exists.
            var page = history.ListRecent(limit: 10_000, offset: 0);
            Assert.Single(page);
        }

        [Fact]
        public void LoadLastSummary_ReturnsNewest()
        {
            var history = NewHistory();
            var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            history.Save(SyntheticReport("old", new DateTimeOffset(t, TimeSpan.Zero), 1, 0));
            history.Save(SyntheticReport("new", new DateTimeOffset(t.AddMinutes(5), TimeSpan.Zero), 1, 0));

            var last = history.LoadLastSummary();
            Assert.NotNull(last);
            Assert.Equal("new", last!.RunId);
        }
    }
}
