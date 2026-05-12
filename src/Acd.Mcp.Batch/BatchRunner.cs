using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Acd.Mcp.Batch
{
    // Deep module. Owns the entire per-file loop:
    //   - File lease (open exclusive, throw if locked).
    //   - Session lifecycle (open via host, dispose at end).
    //   - Globals construction + delegate invocation.
    //   - Outcome aggregation (per-file Pass/Failure rollup).
    //   - Commit decision (Live + no failures only).
    //   - Two-phase sequencing when Mode == Live:
    //       Phase 1: a complete Test pass. NO commits.
    //       Phase 2: if Phase 1 was 100% Pass, run Live with commits.
    //       Otherwise abort with a reason.
    //   - Cancellation between files; the body can also observe ctx.Token.
    //
    // The runner is `Acd.Mcp.Batch`-pure: it does NOT know what AutoCAD is,
    // does NOT touch the filesystem beyond the lease, does NOT block on UI.
    //
    // Generic over TGlobals: the host produces typed globals, the host
    // configures the script with the matching TGlobals type. The runner only
    // sees IDrawingHost<TGlobals> and BatchScriptHost<TGlobals>.
    public sealed class BatchRunner<TGlobals> where TGlobals : class
    {
        private readonly IDrawingHost<TGlobals> _host;
        private readonly IFileAccessProbe _probe;
        private readonly BatchScriptHost<TGlobals> _scriptHost;

        public BatchRunner(
            IDrawingHost<TGlobals> host,
            IFileAccessProbe probe,
            BatchScriptHost<TGlobals> scriptHost)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _scriptHost = scriptHost ?? throw new ArgumentNullException(nameof(scriptHost));
        }

        // Run a batch over the given file list with the given script body.
        //
        // Returns a BatchRunReport. The report includes per-file results and
        // a top-level cancellation / abort flag.
        //
        // Throws ONLY when the file-lease open throws (i.e. file is locked
        // by another writer) — that's the only situation the spec demands a
        // hard abort. Compile errors return a report with no per-file
        // entries and AbortedReason set. Cancellation closes cleanly with
        // partial results.
        //
        // `progress` (optional) is invoked once per completed file, on the
        // calling thread (synchronously). UI hosts pass a marshalled
        // IProgress<T> implementation if they need dispatcher hand-off.
        public async Task<BatchRunReport> RunAsync(
            string body,
            IReadOnlyList<string> files,
            BatchMode mode,
            CancellationToken ct,
            IProgress<BatchFileResult>? progress = null)
        {
            if (body is null) throw new ArgumentNullException(nameof(body));
            if (files is null) throw new ArgumentNullException(nameof(files));

            var runId = NewRunId();
            var started = DateTimeOffset.Now;

            // Compile up front. A compile failure aborts before any file is
            // touched — the spec is explicit about this.
            var compile = _scriptHost.Compile(body);
            if (compile is Outcome<CompiledScript>.Failure fail)
            {
                return new BatchRunReport(
                    RunId: runId,
                    StartedAt: started,
                    CompletedAt: DateTimeOffset.Now,
                    RequestedMode: mode,
                    Files: files,
                    Results: Array.Empty<BatchFileResult>(),
                    Cancelled: false,
                    AbortedReason: $"Compile failed: {fail.Error.Message}");
            }
            var script = ((Outcome<CompiledScript>.Pass)compile).Value;

            // Cross-file state lives in one bag per run. Same instance for
            // every per-file context, so `ctx.BatchState<T>()` returns the
            // same T everywhere.
            var stateBag = new BatchStateBag();

            // Two-phase orchestration. Test-only: just Phase 1. Live: Phase
            // 1 (test) first; abort if any failure; otherwise Phase 2 (live).
            var aggregated = new List<BatchFileResult>();

            var testPhase = await RunPhaseAsync(
                script, files, BatchPhase.Test, isLive: false,
                stateBag, ct, progress, aggregated).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return Finish(runId, started, mode, files, aggregated,
                    cancelled: true, abortedReason: null);
            }

            if (mode == BatchMode.Test)
            {
                return Finish(runId, started, mode, files, aggregated,
                    cancelled: false, abortedReason: null);
            }

            // Mode == Live: gate on a clean Test pass.
            if (testPhase.FailedCount > 0)
            {
                return Finish(runId, started, mode, files, aggregated,
                    cancelled: false,
                    abortedReason: $"Live pass not started: {testPhase.FailedCount} file(s) failed the internal Test pass.");
            }

            // A run state bag is intentionally REUSED across phases so that
            // any state the Test pass built up is visible to the Live pass.
            // For most scripts this is irrelevant; for cross-file counters
            // it matches the user's mental model ("the same logic runs
            // twice with the same state").
            await RunPhaseAsync(
                script, files, BatchPhase.Live, isLive: true,
                stateBag, ct, progress, aggregated).ConfigureAwait(false);

            return Finish(runId, started, mode, files, aggregated,
                cancelled: ct.IsCancellationRequested, abortedReason: null);
        }

        private async Task<PhaseSummary> RunPhaseAsync(
            CompiledScript script,
            IReadOnlyList<string> files,
            BatchPhase phase,
            bool isLive,
            BatchStateBag stateBag,
            CancellationToken ct,
            IProgress<BatchFileResult>? progress,
            List<BatchFileResult> aggregated)
        {
            int failed = 0;
            foreach (var path in files)
            {
                // Cancellation between files: stop cleanly with partial results.
                if (ct.IsCancellationRequested) break;

                var sw = Stopwatch.StartNew();

                // File-locked → THROW. We do NOT silently skip. The user
                // must intervene. The lease wraps a FileShare.Read open so
                // any concurrent writer makes this throw IOException, which
                // we let propagate.
                FileLease lease;
                try { lease = _probe.OpenLease(path); }
                catch (Exception ex)
                {
                    // Per spec: the entire batch aborts when a file is
                    // inaccessible. We rethrow once the partial results are
                    // recorded.
                    var result = new BatchFileResult(
                        Path: path,
                        Phase: phase,
                        Status: FileOutcomeStatus.Failure,
                        Steps: Array.Empty<StepOutcome>(),
                        Committed: false,
                        Cancelled: false,
                        Error: ex,
                        ElapsedMs: sw.ElapsedMilliseconds);
                    aggregated.Add(result);
                    progress?.Report(result);
                    throw new BatchAbortedException(
                        $"File '{path}' is locked or inaccessible. Batch aborted.", ex);
                }

                BatchFileResult fileResult;
                try
                {
                    using var session = _host.Open(path, lease);
                    var ctx = new BatchContext(stateBag, phase, ct);
                    var globals = _host.BuildGlobals(session, ctx);

                    Exception? bodyError = null;
                    try
                    {
                        await _scriptHost.RunAsync(script, globals, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // User cancelled mid-file. Record + bail.
                        fileResult = new BatchFileResult(
                            Path: path,
                            Phase: phase,
                            Status: FileOutcomeStatus.Failure,
                            Steps: ctx.Steps,
                            Committed: false,
                            Cancelled: true,
                            Error: null,
                            ElapsedMs: sw.ElapsedMilliseconds);
                        aggregated.Add(fileResult);
                        progress?.Report(fileResult);
                        break;
                    }
                    catch (Exception ex)
                    {
                        bodyError = ex;
                    }

                    var status = (bodyError is null && !ctx.HasFailures)
                        ? FileOutcomeStatus.Pass
                        : FileOutcomeStatus.Failure;

                    bool committed = false;
                    if (isLive && status == FileOutcomeStatus.Pass)
                    {
                        try
                        {
                            session.CommitAndSave();
                            committed = true;
                        }
                        catch (Exception ex)
                        {
                            // Commit/Save failed: surface as a file failure
                            // so the user knows that file did NOT persist.
                            bodyError = ex;
                            status = FileOutcomeStatus.Failure;
                        }
                    }

                    fileResult = new BatchFileResult(
                        Path: path,
                        Phase: phase,
                        Status: status,
                        Steps: ctx.Steps,
                        Committed: committed,
                        Cancelled: false,
                        Error: bodyError,
                        ElapsedMs: sw.ElapsedMilliseconds);
                }
                finally
                {
                    lease.Dispose();
                }

                if (fileResult.Status == FileOutcomeStatus.Failure) failed++;
                aggregated.Add(fileResult);
                progress?.Report(fileResult);
            }
            return new PhaseSummary(failed);
        }

        private BatchRunReport Finish(
            string runId,
            DateTimeOffset startedAt,
            BatchMode mode,
            IReadOnlyList<string> files,
            IReadOnlyList<BatchFileResult> results,
            bool cancelled,
            string? abortedReason)
        {
            return new BatchRunReport(
                RunId: runId,
                StartedAt: startedAt,
                CompletedAt: DateTimeOffset.Now,
                RequestedMode: mode,
                Files: files,
                Results: results,
                Cancelled: cancelled,
                AbortedReason: abortedReason);
        }

        private static string NewRunId() =>
            DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private readonly record struct PhaseSummary(int FailedCount);
    }

    // Thrown by BatchRunner when a file lease cannot be opened (i.e. the
    // file is locked or otherwise inaccessible). The caller catches this
    // at the UI boundary; the batch is considered aborted.
    public sealed class BatchAbortedException : Exception
    {
        public BatchAbortedException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
