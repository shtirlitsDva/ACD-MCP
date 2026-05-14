using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Xunit;

namespace Acd.Mcp.Batch.Tests;

// Regression pin for issue #1. The actual eFilerError is an OS-level
// interaction with AutoCAD's Database and is reproduced in
// tests/repro/Test-EfilerErrorRepro.csx — that's the high-fidelity
// artifact. THIS test pins the runner's behavioural contract: in
// Mode=Live, each file is opened TWICE (once for Test phase, once for
// Live), and the Live session is the one that commits. If a future
// refactor accidentally drops the second pass or commits during Test,
// this test fails before the change ships.
public class BatchRunnerTwoPhaseTests
{
    [Fact]
    public async Task RunAsync_LiveMode_opens_each_file_twice_and_commits_only_in_live()
    {
        var host = new RecordingHost();
        var probe = new NoopProbe();
        var script = new BatchScriptHost<NoopGlobals>(
            ScriptOptions.Default.WithReferences(typeof(NoopGlobals).Assembly));
        var runner = new BatchRunner<NoopGlobals>(host, probe, script);

        var files = new[] { @"X:\a.dwg", @"X:\b.dwg" };
        var report = await runner.RunAsync(
            body: "// noop",
            files: files,
            mode: BatchMode.Live,
            ct: default);

        Assert.Null(report.AbortedReason);
        Assert.False(report.Cancelled);
        // Each file opened once for Test, once for Live = 4 opens total.
        Assert.Equal(4, host.OpenLog.Count);
        Assert.Equal(new[] { @"X:\a.dwg", @"X:\b.dwg", @"X:\a.dwg", @"X:\b.dwg" }, host.OpenLog);
        // Only Live phase commits — exactly two CommitAndSave calls.
        Assert.Equal(2, host.CommitLog.Count);
        // Every session disposed (4 opens → 4 disposes).
        Assert.Equal(4, host.DisposeLog.Count);
    }

    private sealed class NoopGlobals { }

    private sealed class NoopProbe : IFileAccessProbe
    {
        public FileLease OpenLease(string path) => new(path);
    }

    private sealed class RecordingHost : IDrawingHost<NoopGlobals>
    {
        public List<string> OpenLog { get; } = new();
        public List<string> CommitLog { get; } = new();
        public List<string> DisposeLog { get; } = new();

        public IBatchSession Open(string path, FileLease lease)
        {
            OpenLog.Add(path);
            return new RecordingSession(path, CommitLog, DisposeLog);
        }

        public NoopGlobals BuildGlobals(IBatchSession session, IBatchContext ctx) => new();
    }

    private sealed class RecordingSession : IBatchSession
    {
        private readonly List<string> _commits;
        private readonly List<string> _disposes;

        public RecordingSession(string path, List<string> commits, List<string> disposes)
        {
            Path = path;
            _commits = commits;
            _disposes = disposes;
        }

        public string Path { get; }
        public void CommitAndSave() => _commits.Add(Path);
        public void Dispose() => _disposes.Add(Path);
    }
}
