using System.Collections.Concurrent;
using Acd.Mcp;
using Autodesk.Revit.UI;

namespace Rvt.Mcp
{
    // The Revit twin of AcadExecutor. AutoCAD marshals snippets via
    // SynchronizationContext + LockDocument; Revit's only legal door into
    // API context is an ExternalEvent, so this class is both the executor
    // and the IExternalEventHandler. Snippets run when Revit raises the
    // event (UI idle) — being in API context replaces AutoCAD's document
    // lock entirely.
    //
    // Same ApiContextRunner work-queue pattern as RevitDevReload (separate
    // repo — small deliberate duplication until a shared package exists).
    public sealed class RvtExecutor : IExternalEventHandler
    {
        private sealed class WorkItem
        {
            public WorkItem(string code, CancellationToken ct)
            {
                Code = code;
                Ct = ct;
                Done = new TaskCompletionSource<ExecuteResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string Code { get; }
            public CancellationToken Ct { get; }
            public TaskCompletionSource<ExecuteResult> Done { get; }
        }

        private readonly ConcurrentQueue<WorkItem> _queue = new();
        private RvtScriptSession? _session;
        private ExternalEvent? _event;

        // Must run inside API context (OnStartup): both ExternalEvent.Create
        // and the UIApplication hand-off happen there.
        public void Attach(UIApplication uiApp, System.Text.Json.JsonSerializerOptions jsonOptions)
        {
            _session = new RvtScriptSession(new RvtGlobals(uiApp), jsonOptions);
            _event = ExternalEvent.Create(this);
        }

        public bool IsAttached => _event != null;

        public async Task<ExecuteResult> ExecuteAsync(
            string code, int? timeoutMs, CancellationToken outerCt)
        {
            if (_event is null)
                return ExecuteResult.Runtime("Rvt.Mcp executor not attached.", 0);

            using var perCall = new CancellationTokenSource();
            if (timeoutMs is int ms && ms > 0) perCall.CancelAfter(ms);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, perCall.Token);

            var item = new WorkItem(code, linked.Token);
            _queue.Enqueue(item);
            _event.Raise();

            // The timeout must also cover "Revit never went idle" (modal
            // dialog up, long command running) — not just snippet runtime.
            var timeoutTask = timeoutMs is int t && t > 0
                ? Task.Delay(t + 1000, CancellationToken.None)
                : Task.Delay(Timeout.Infinite, outerCt);

            var winner = await Task.WhenAny(item.Done.Task, timeoutTask).ConfigureAwait(false);
            if (winner != item.Done.Task)
            {
                return ExecuteResult.Runtime(
                    "Timed out waiting for Revit API context (modal dialog or long-running command?). " +
                    "The snippet may still run when Revit becomes idle.", timeoutMs ?? 0);
            }
            return await item.Done.Task.ConfigureAwait(false);
        }

        public void Reset() => _session?.Reset();

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    // Blocking Revit's UI thread here is intentional — running a
                    // snippet IS blocking Revit, the same as any command.
                    // CSharpScript uses ConfigureAwait(false) internally so the
                    // synchronous wait cannot deadlock.
                    var result = _session!.ExecuteAsync(item.Code, item.Ct)
                        .GetAwaiter().GetResult();
                    item.Done.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.Done.TrySetResult(ExecuteResult.Runtime(ex.ToString(), 0));
                }
            }
        }

        public string GetName() => "Rvt.Mcp.RvtExecutor";
    }
}
