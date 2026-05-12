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

        private readonly object _runLock = new();
        private CancellationTokenSource? _activeCts;
        private Task<BatchRunReport>? _activeRun;

        public SavedScriptStore Scripts { get; }
        public BatchRunHistory History { get; }
        public EditorBuffer Editor { get; }

        // The authoritative current script content. Both agent (via
        // ProposeScript) and user (via the palette editor) write to it.
        // Notified on change so the UI can sync.
        private string _currentScript = "";
        public string CurrentScript
        {
            get { lock (_runLock) return _currentScript; }
        }

        public event EventHandler<BatchScriptProposed>? ScriptProposed;
        public event EventHandler<BatchFileResult>? FileCompleted;
        public event EventHandler<BatchRunReport>? RunCompleted;

        public bool IsRunning
        {
            get { lock (_runLock) return _activeRun is { IsCompleted: false }; }
        }

        public BatchExecutor()
        {
            _scriptHost = BatchScriptRuntime.CreateHost();
            Scripts = new SavedScriptStore();
            History = new BatchRunHistory();
            Editor = new EditorBuffer();
        }

        // Agent path: write the script to the saved store (overwriting if a
        // matching name exists) AND propose it to the editor. The editor
        // either accepts immediately (clean) or prompts the user via the
        // UI's unsaved-edits race resolution.
        //
        // Returns the path on disk so the caller can echo it back.
        public SavedScript ProposeScript(string name, string body, string? summary)
        {
            var saved = Scripts.Save(ScriptFlavor.Batch, name, body, summary);

            // Update the current-script slot. The UI subscribes to
            // ScriptProposed and decides what to do (accept silently if no
            // unsaved edits, otherwise prompt).
            BatchScriptProposed evt;
            lock (_runLock)
            {
                evt = new BatchScriptProposed(saved, _currentScript);
                _currentScript = saved.Body;
            }
            Editor.SetText(saved.Body);
            ScriptProposed?.Invoke(this, evt);
            return saved;
        }

        // The palette's editor calls this on text change to keep the
        // current-script slot in sync. No event is raised — this IS the
        // user typing, so no one needs to be notified.
        public void OnEditorTextChanged(string text)
        {
            lock (_runLock) _currentScript = text;
            Editor.SetText(text);
        }

        // Agent path: kick off a test-mode run against the given files.
        // The agent does NOT supply the script body — the executor uses the
        // current editor content. Returns the run id immediately; the run
        // continues on a threadpool task; results land in History when done.
        //
        // The spec is explicit: the agent has no live-mode trigger; that's
        // a UI button only. So this method is hard-coded to BatchMode.Test
        // and there is no live counterpart on the executor's public surface.
        public string StartTestRun(IReadOnlyList<string> files)
            => StartRun(BatchMode.Test, files);

        // UI path: kick off a run with the user-chosen mode. Live runs
        // honour the two-phase contract (Test pass first; abort if any
        // Test failure). Mode comes from the slide-switch.
        public string StartRunFromUi(BatchMode mode, IReadOnlyList<string> files)
            => StartRun(mode, files);

        private string StartRun(BatchMode mode, IReadOnlyList<string> files)
        {
            CancellationTokenSource cts;
            Task<BatchRunReport> task;
            string body;
            string runId = NewRunId();
            lock (_runLock)
            {
                if (_activeRun is { IsCompleted: false })
                    throw new InvalidOperationException("A batch run is already in progress. Cancel it first.");
                body = _currentScript;
                cts = new CancellationTokenSource();
                _activeCts = cts;
                task = RunCoreAsync(body, files, mode, runId, cts.Token);
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
            string body, IReadOnlyList<string> files, BatchMode mode, string runId, CancellationToken ct)
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
                    return await NewRunner().RunAsync(body, files, mode, ct, progress, runId)
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
            SafeBoundary.Run("BatchExecutor.Dispose/cancel", () =>
            {
                lock (_runLock) _activeCts?.Cancel();
            });
            SafeBoundary.Run("BatchExecutor.Dispose/editor", () => Editor.Dispose());
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SyncProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }

    // Raised when ProposeScript runs. The UI looks at the proposed body
    // vs the editor's current content and decides whether to silently
    // accept (no unsaved edits) or prompt the user (dirty).
    public sealed record BatchScriptProposed(SavedScript Saved, string PreviousEditorText);
}
