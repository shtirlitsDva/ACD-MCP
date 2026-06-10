using Acd.Mcp.Scripting;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp.Pipe
{
    // Deep module: takes a snippet and a cancellation budget, returns a result.
    // Hides every coordination concern in between:
    //   - marshaling to AutoCAD's main thread via the captured SynchronizationContext
    //   - acquiring a document lock for the snippet's duration
    //   - linking caller cancellation with a per-call timeout
    //   - turning every failure mode into an ExecuteResult instead of an exception
    //   - appending the (code, result, source) tuple to the shared ExecutionLog so
    //     the palette and any future observer see the same stream of activity
    //
    // Transports (named pipe today, HTTP later if needed) hand a string of C# to
    // ExecuteAsync and forward the result. They do not learn anything about threads,
    // documents, locks, logging, or the script session.
    public sealed class AcadExecutor
    {
        private readonly SynchronizationContext _mainSync;
        private readonly ScriptSession _session;
        private readonly ExecutionLog _log;

        public AcadExecutor(SynchronizationContext mainSync, ScriptSession session, ExecutionLog log)
        {
            _mainSync = mainSync;
            _session = session;
            _log = log;
        }

        public async Task<ExecuteResult> ExecuteAsync(
            string code,
            int? timeoutMs,
            ExecutionSource source,
            CancellationToken outerCt)
        {
            var result = await ExecuteCoreAsync(code, timeoutMs, outerCt).ConfigureAwait(false);

            // ExecutionLog.Add isolates its own subscribers, but in case the act
            // of constructing the LogEntry or acquiring the log's lock itself
            // throws we still want the caller to see the result instead of an
            // exception bubbling out.
            SafeBoundary.Run("AcadExecutor.ExecuteAsync/log.Add", () =>
                _log.Add(new LogEntry(DateTimeOffset.Now, source, code, result)));

            return result;
        }

        public void Reset() => _session.Reset();

        private Task<ExecuteResult> ExecuteCoreAsync(string code, int? timeoutMs, CancellationToken outerCt)
        {
            var tcs = new TaskCompletionSource<ExecuteResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            // CTS lifetime tied to the returned Task — AwaitAndDisposeAsync disposes
            // them only after tcs has settled.
            var perCallCts = new CancellationTokenSource();
            if (timeoutMs is int ms && ms > 0) perCallCts.CancelAfter(ms);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, perCallCts.Token);
            var token = linked.Token;

            _mainSync.Post(_ =>
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc is null)
                    {
                        tcs.TrySetResult(ExecuteResult.Runtime("No active document.", 0));
                        return;
                    }

                    // Blocking the main thread here is intentional — running a snippet
                    // IS blocking AutoCAD, the same as any [CommandMethod]. CSharpScript
                    // uses ConfigureAwait(false) internally so continuations land on the
                    // threadpool and don't deadlock against this synchronous wait.
                    using (doc.LockDocument())
                    {
                        var result = _session.ExecuteAsync(code, token).GetAwaiter().GetResult();
                        tcs.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult(ExecuteResult.Runtime(ex.ToString(), 0));
                }
            }, null);

            return AwaitAndDisposeAsync(tcs.Task, perCallCts, linked);
        }

        private static async Task<ExecuteResult> AwaitAndDisposeAsync(
            Task<ExecuteResult> inner, CancellationTokenSource a, CancellationTokenSource b)
        {
            try { return await inner.ConfigureAwait(false); }
            finally { a.Dispose(); b.Dispose(); }
        }
    }
}
