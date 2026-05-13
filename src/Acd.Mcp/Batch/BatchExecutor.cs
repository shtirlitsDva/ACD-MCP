using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Batch;

namespace Acd.Mcp.Batch.Runtime
{
    // Deep module. The plugin's batch entry point. Owns:
    //   - The compile cache (BatchScriptHost<AcadBatchGlobals>).
    //   - The run history (BatchRunHistory).
    //   - The saved-scripts store (SavedScriptStore).
    //   - The editor buffer mirror (EditorBuffer).
    //   - The CurrentScript slot (the live-shared editor's authoritative text).
    //   - The active run's CancellationTokenSource + state flags.
    //
    // Callers:
    //   - The pipe's batch RPC handlers (test-run, propose-script, list-runs, …).
    //   - The BATCH palette's WPF view-model (run / cancel / load saved / save).
    //   - The Manage Scripts window.
    //
    // The executor is constructed at McpPlugin.Initialize and torn down at
    // Terminate. It is thread-safe by virtue of the underlying components
    // and the single-active-run invariant.
    public sealed class BatchExecutor : IDisposable
    {
        private readonly BatchScriptHost<AcadBatchGlobals> _scriptHost;
        private readonly AcadDrawingHost _drawingHost = new();
        private readonly IFileAccessProbe _probe = new DefaultFileAccessProbe();

        // Editor concerns (saved-scripts store, current text slot, IsDirty,
        // mirror file, propose-vs-typing race) live in the shared deep
        // module ScriptEditor. BatchExecutor delegates the public surface
        // that callers historically used (Scripts / Editor / CurrentScript /
        // ProposeScript / OnEditorTextChanged / ScriptProposed) to it, so
        // BatchRpcHandler / BatchViewModel / ManageScriptsViewModel keep
        // working without changes.
        private readonly ScriptEditor _editor;

        private readonly object _runLock = new();
        private CancellationTokenSource? _activeCts;
        private Task<BatchRunReport>? _activeRun;

        public ScriptEditor ScriptEditor => _editor;
        public SavedScriptStore Scripts => _editor.Store;
        public BatchRunHistory History { get; }
        // The mirror file path used to live behind a separate EditorBuffer
        // property exposed by BatchExecutor. ScriptEditor now owns the
        // mirror; expose only the path through this thin façade so the
        // RPC handler doesn't need to reach into the editor.
        public string MirrorPath => _editor.MirrorPath;
        public string CurrentScript => _editor.CurrentText;
        public bool IsDirty => _editor.IsDirty;

        public event EventHandler<ScriptProposedEvent>? ScriptProposed
        {
            add    => _editor.ScriptProposed += value;
            remove => _editor.ScriptProposed -= value;
        }
        public event EventHandler<BatchFileResult>? FileCompleted;
        public event EventHandler<BatchRunReport>? RunCompleted;

        public bool IsRunning
        {
            get { lock (_runLock) return _activeRun is { IsCompleted: false }; }
        }

        // The injected ScriptEditor must be configured with Flavor=Batch.
        // The editor owns the mirror file's lifetime; BatchExecutor does
        // not Dispose it (McpPlugin disposes the editor at shutdown).
        public BatchExecutor(ScriptEditor editor)
        {
            if (editor is null) throw new ArgumentNullException(nameof(editor));
            if (editor.Flavor != ScriptFlavor.Batch)
                throw new ArgumentException(
                    $"BatchExecutor requires a ScriptEditor with Flavor=Batch (got {editor.Flavor}).",
                    nameof(editor));
            _editor = editor;
            _scriptHost = BatchScriptRuntime.CreateHost();
            History = new BatchRunHistory();
        }

        // Agent path: write the script to the saved store (overwriting if a
        // matching name exists) AND propose it to the editor. The editor
        // either accepts immediately (clean) or prompts the user via the
        // UI's unsaved-edits race resolution.
        //
        // Returns the saved record so the caller can echo the path back.
        public SavedScript ProposeScript(string name, string body, string? summary)
            => _editor.ProposeFromAgent(name, body, summary);

        // The palette's editor calls this on text change to keep the
        // current-script slot in sync and to flip IsDirty=true. No event
        // is raised — this IS the user typing, so no one needs to be
        // notified.
        public void OnEditorTextChanged(string text)
            => _editor.OnUserTyped(text);

        // Agent path: kick off a test-mode run against the given files.
        // The agent does NOT supply the script body — the executor uses the
        // current editor content. Returns the run id immediately; the run
        // continues on a threadpool task; results land in History when done.
        //
        // The spec is explicit: the agent has no live-mode trigger; that's
        // a UI button only. So this method is hard-coded to BatchMode.Test
        // and there is no live counterpart on the executor's public surface.
        public string StartTestRun(IReadOnlyList<string> files, BatchOnFailure onFailure = BatchOnFailure.Abort)
            => StartRun(BatchMode.Test, files, onFailure);

        // UI path: kick off a run with the user-chosen mode. Live runs
        // honour the two-phase contract (Test pass first; abort if any
        // Test failure). Mode + On-failure policy come from the palette.
        public string StartRunFromUi(BatchMode mode, IReadOnlyList<string> files, BatchOnFailure onFailure)
            => StartRun(mode, files, onFailure);

        private string StartRun(BatchMode mode, IReadOnlyList<string> files, BatchOnFailure onFailure)
        {
            CancellationTokenSource cts;
            Task<BatchRunReport> task;
            string runId = NewRunId();
            // ScriptEditor's slot has its own lock; snapshot the body
            // first, then take the run lock for the active-run state.
            var body = _editor.CurrentText;
            lock (_runLock)
            {
                if (_activeRun is { IsCompleted: false })
                    throw new InvalidOperationException("A batch run is already in progress. Cancel it first.");
                cts = new CancellationTokenSource();
                _activeCts = cts;
                task = RunCoreAsync(body, files, mode, runId, cts.Token, onFailure);
                _activeRun = task;
            }
            // Wire up history write + RunCompleted dispatch when the task
            // settles. Continuation runs on a threadpool thread; UI hosts
            // marshal back as needed via their own dispatcher.
            _ = task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } report)
                {
                    SafeBoundary.Run("BatchExecutor.HistorySave", () => History.Save(report));
                    SafeBoundary.Run("BatchExecutor.RunCompleted",
                        () => RunCompleted?.Invoke(this, report));

                    // Explicit completion marker — the /acd-mcp:batch skill
                    // tells the agent to watch %LOCALAPPDATA%\Acd.Mcp\log.txt
                    // for this exact line with the Monitor tool, so agents
                    // wake up once instead of polling acd-mcp://batch-runs.
                    int pass = 0;
                    foreach (var r in report.Results)
                        if (r.Status == FileOutcomeStatus.Pass) pass++;
                    SafeBoundary.Info("BatchExecutor",
                        $"BATCH RUN COMPLETED {report.RunId} ({pass}/{report.Results.Count})");
                }
                else if (t.Exception is { } ex)
                {
                    SafeBoundary.Report(ex.GetBaseException(), "BatchExecutor.RunCore");
                }
                lock (_runLock)
                {
                    _activeCts?.Dispose();
                    _activeCts = null;
                    _activeRun = null;
                }
            }, TaskScheduler.Default);

            return runId;
        }

        // The run id format matches BatchRunner.NewRunId so the executor and
        // the runner agree on what a run id looks like (the runner accepts
        // the executor-supplied id and stamps it on the report).
        private static string NewRunId() =>
            DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public void Cancel()
        {
            lock (_runLock) _activeCts?.Cancel();
        }

        // Returns a freshly-built runner instance. We don't cache a single
        // runner because BatchRunner is cheap and stateless across runs;
        // the cache that matters (compiled-script delegates) lives in the
        // shared BatchScriptHost.
        private BatchRunner<AcadBatchGlobals> NewRunner() =>
            new(_drawingHost, _probe, _scriptHost);

        private Task<BatchRunReport> RunCoreAsync(
            string body, IReadOnlyList<string> files, BatchMode mode, string runId, CancellationToken ct,
            BatchOnFailure onFailure)
        {
            // We hand the runner a synchronous IProgress; the executor
            // re-publishes to FileCompleted. WPF hosts marshal back to the
            // dispatcher via their own handler.
            var progress = new SyncProgress<BatchFileResult>(r =>
                SafeBoundary.Run("BatchExecutor.FileCompleted",
                    () => FileCompleted?.Invoke(this, r)));

            return Task.Run(async () =>
            {
                try
                {
                    return await NewRunner().RunAsync(body, files, mode, ct, progress, runId, onFailure)
                        .ConfigureAwait(false);
                }
                catch (BatchAbortedException ex)
                {
                    // File-lease open failed → synthesise a "no results,
                    // aborted" report so history still records the attempt
                    // under the same runId the caller already saw.
                    return new BatchRunReport(
                        RunId: runId,
                        StartedAt: DateTimeOffset.Now,
                        CompletedAt: DateTimeOffset.Now,
                        RequestedMode: mode,
                        Files: files,
                        Results: Array.Empty<BatchFileResult>(),
                        Cancelled: false,
                        AbortedReason: ex.Message);
                }
            }, ct);
        }

        public void Dispose()
        {
            // Snapshot inside the lock, then cancel + wait outside it so we
            // don't deadlock against the continuation (which takes the same
            // lock to clear _activeRun / _activeCts on completion).
            //
            // Bounded wait mirrors PipeListener.Stop — Terminate must not
            // block AutoCAD for long. If the run doesn't honour cancellation
            // within 2 s, the continuation still runs (the task itself is
            // owned by the threadpool), but Dispose returns and the rest of
            // tear-down proceeds.
            Task? runToWait;
            CancellationTokenSource? ctsToCancel;
            lock (_runLock)
            {
                runToWait = _activeRun;
                ctsToCancel = _activeCts;
            }

            SafeBoundary.Run("BatchExecutor.Dispose/cancel", () => ctsToCancel?.Cancel());
            SafeBoundary.Run("BatchExecutor.Dispose/wait",
                () => runToWait?.Wait(TimeSpan.FromSeconds(2)));
            // We deliberately do NOT dispose _activeCts here — the run's
            // continuation owns that and disposes on settle. CTS.Dispose is
            // safe to call multiple times, but leaving it to the
            // continuation keeps the ownership single-rooted.

            // ScriptEditor (and its mirror) is owned by McpPlugin and
            // disposed there alongside the other shared instances.
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }

}
