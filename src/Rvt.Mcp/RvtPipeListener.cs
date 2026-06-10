using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Rvt.Mcp
{
    // Transport: named pipe `rvt-mcp-{pid}`, JSON-RPC over length-prefixed
    // frames (Acd.Mcp.Pipe.FrameIO, linked). Mirrors Acd.Mcp's PipeListener
    // minus the AutoCAD-side extras (extra method handlers, execution log) —
    // the V1 surface is ping / reset / execute.
    public sealed class RvtPipeListener : IDisposable
    {
        private readonly RvtExecutor _executor;
        private readonly string _revitVersion;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _lock = new();

        public string PipeName { get; }
        public bool IsRunning { get; private set; }

        public RvtPipeListener(RvtExecutor executor, string revitVersion)
        {
            _executor = executor;
            _revitVersion = revitVersion;
            PipeName = $"rvt-mcp-{Process.GetCurrentProcess().Id}";
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
                    // A single accept failure must not kill the listener; the
                    // next iteration creates a fresh server instance.
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
                    catch
                    {
                        break; // framing error or cancellation — drop the connection
                    }

                    if (req is null) break;
                    var response = await DispatchAsync(req, ct).ConfigureAwait(false);
                    await FrameIO.WriteFrameAsync(server, response, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Per-connection swallow so one bad client can't kill the listener.
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
                            revit_pid = Process.GetCurrentProcess().Id,
                            revit_version = _revitVersion,
                            mcp_version = typeof(RvtPipeListener).Assembly.GetName().Version?.ToString() ?? "0.0",
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
                            .ExecuteAsync(codeEl.GetString()!, timeoutMs, ct)
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
