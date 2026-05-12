using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp.Pipe
{
    // Transport. Listens on a named pipe, frames JSON-RPC requests, hands them to
    // the executor, frames responses back. Knows nothing about threads, documents,
    // or the script session — the executor owns that complexity.
    public sealed class PipeListener : IDisposable
    {
        private readonly AcadExecutor _executor;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _lock = new();

        public string PipeName { get; }
        public bool IsRunning { get; private set; }

        public PipeListener(AcadExecutor executor)
        {
            _executor = executor;
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

            // Bounded so Terminate() never blocks AutoCAD for long. In-flight
            // connection handlers observe CT cancellation and bail.
            SafeBoundary.Run("PipeListener.Stop/cancel",  () => ctsToDispose?.Cancel());
            SafeBoundary.Run("PipeListener.Stop/wait",    () => loopToWait?.Wait(TimeSpan.FromSeconds(2)));
            SafeBoundary.Run("PipeListener.Stop/dispose", () => ctsToDispose?.Dispose());
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
                catch (Exception ex)
                {
                    // Log and continue. The next iteration creates a fresh server.
                    // We don't want a single accept failure to terminate the listener.
                    SafeBoundary.Report(ex, "PipeListener.AcceptLoopAsync");
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
                        req = await FrameIO.ReadFrameAsync<JsonRpcRequest>(server, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // Byte stream is no longer trustworthy. Log the framing
                        // error so we can diagnose bad clients, then drop the
                        // connection; the client can reconnect.
                        SafeBoundary.Report(ex, "PipeListener.HandleConnectionAsync/read");
                        break;
                    }

                    if (req is null) break;
                    var response = await DispatchAsync(req, ct).ConfigureAwait(false);
                    await FrameIO.WriteFrameAsync(server, response, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Per-connection swallow so one bad client cannot kill the listener,
                // but log so we know it happened.
                SafeBoundary.Report(ex, "PipeListener.HandleConnectionAsync");
            }
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
                        _executor.Reset();
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
                        var result = await _executor
                            .ExecuteAsync(codeEl.GetString()!, timeoutMs, ExecutionSource.Mcp, ct)
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
    }
}
