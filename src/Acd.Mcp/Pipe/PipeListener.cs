using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Scripting;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp.Pipe
{
    // Named-pipe server that accepts JSON-RPC requests and dispatches them.
    // Listener runs on a threadpool task; per-connection handlers run on their
    // own tasks. The 'execute' method marshals onto AutoCAD's main thread via
    // the SynchronizationContext captured at plugin Initialize().
    public sealed class PipeListener : IDisposable
    {
        private readonly SynchronizationContext _mainSync;
        private readonly ScriptSession _session;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _lock = new();

        public string PipeName { get; }
        public bool IsRunning { get; private set; }

        public PipeListener(SynchronizationContext mainSync, ScriptSession session)
        {
            _mainSync = mainSync;
            _session = session;
            PipeName = $"acd-mcp-{Process.GetCurrentProcess().Id}";
        }

        public void Start()
        {
            lock (_lock)
            {
                if (IsRunning) return;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                IsRunning = true;
            }
        }

        public void Stop()
        {
            Task? loopToWait;
            CancellationTokenSource? ctsToDispose;
            lock (_lock)
            {
                if (!IsRunning) return;
                IsRunning = false;
                loopToWait = _loop;
                ctsToDispose = _cts;
                _loop = null;
                _cts = null;
            }

            try { ctsToDispose?.Cancel(); } catch { }
            // Give the loop a moment to wind down; in-flight connection handlers
            // will see CT cancellation and bail. Bounded wait so Terminate() never
            // blocks AutoCAD for long.
            try { loopToWait?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { ctsToDispose?.Dispose(); } catch { }
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    var conn = server;
                    server = null; // ownership transferred to the handler task
                    _ = Task.Run(() => HandleConnectionAsync(conn, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Swallow listener faults — the next loop iteration creates a
                    // fresh server. Production telemetry/logging belongs in Slice 5.
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }

        private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            try
            {
                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    JsonRpcRequest? req;
                    try
                    {
                        req = await FrameIO.ReadRequestAsync(server, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        // Bad framing — the byte stream is no longer trustworthy.
                        // Drop the connection; the client can reconnect.
                        break;
                    }

                    if (req is null) break;
                    var response = await DispatchAsync(req, ct).ConfigureAwait(false);
                    await FrameIO.WriteResponseAsync(server, response, ct).ConfigureAwait(false);
                }
            }
            catch { /* per-connection swallow; listener continues */ }
            finally { server.Dispose(); }
        }

        private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest req, CancellationToken ct)
        {
            try
            {
                switch (req.Method)
                {
                    case "ping":
                        return JsonRpcResponse.Ok(req.Id, new
                        {
                            autocad_pid = Process.GetCurrentProcess().Id,
                            autocad_version = Application.Version.ToString(),
                            mcp_version = typeof(PipeListener).Assembly.GetName().Version?.ToString() ?? "0.0",
                        });

                    case "reset":
                        _session.Reset();
                        return JsonRpcResponse.Ok(req.Id, new { ok = true });

                    case "execute":
                        if (req.Params.ValueKind != JsonValueKind.Object ||
                            !req.Params.TryGetProperty("code", out var codeEl) ||
                            codeEl.ValueKind != JsonValueKind.String)
                        {
                            return JsonRpcResponse.Err(req.Id, ErrorCodes.InvalidParams,
                                "execute requires params.code (string)");
                        }
                        int? timeoutMs = null;
                        if (req.Params.TryGetProperty("timeout_ms", out var toEl) &&
                            toEl.ValueKind == JsonValueKind.Number &&
                            toEl.TryGetInt32(out var to))
                        {
                            timeoutMs = to;
                        }
                        var result = await ExecuteOnMainThreadAsync(codeEl.GetString()!, timeoutMs, ct)
                            .ConfigureAwait(false);
                        return JsonRpcResponse.Ok(req.Id, result);

                    default:
                        return JsonRpcResponse.Err(req.Id, ErrorCodes.MethodNotFound,
                            $"Method not found: {req.Method}");
                }
            }
            catch (Exception ex)
            {
                return JsonRpcResponse.Err(req.Id, ErrorCodes.InternalError, ex.ToString());
            }
        }

        private async Task<ExecuteResult> ExecuteOnMainThreadAsync(string code, int? timeoutMs, CancellationToken outerCt)
        {
            var tcs = new TaskCompletionSource<ExecuteResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var perCallCts = new CancellationTokenSource();
            if (timeoutMs is int ms && ms > 0) perCallCts.CancelAfter(ms);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt, perCallCts.Token);
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

                    // Doc lock covers any DB writes the snippet may perform. We
                    // block the main thread for the duration of the script —
                    // CSharpScript's internal awaits use ConfigureAwait(false),
                    // so continuations land on the threadpool and don't deadlock
                    // against this synchronous wait.
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

            return await tcs.Task.ConfigureAwait(false);
        }
    }
}
