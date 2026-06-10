using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Bridge
{
    // Public front for everything Bridge code (MCP tools, host) needs
    // about "talking to AutoCAD". Hides:
    //   - AutoCAD instance discovery (PID enumeration / explicit override
    //     / pipe-probed disambiguation)
    //   - Connect retries across the AutoCAD restart window
    //   - JSON-RPC method names and parameter shapes
    //   - Response envelope unwrapping (error vs result)
    //
    // Discovery + connect both happen per call so AutoCAD restarts (or
    // DevReload reloads) are transparent. The cost is one process
    // enumeration + a 150ms-budget probe per attempt — well under the
    // round-trip time of the snippets the bridge proxies.
    public sealed class AcadClient
    {
        private readonly int? _explicitPid;
        private readonly AutoCadDiscovery _discovery;
        private readonly ConnectRetryPolicy _retry;

        public AcadClient(
            int? explicitPid = null,
            AutoCadDiscovery? discovery = null,
            ConnectRetryPolicy? retry = null)
        {
            _explicitPid = explicitPid;
            _discovery = discovery ?? AutoCadDiscovery.Default;
            _retry = retry ?? ConnectRetryPolicy.Default;
        }

        public async Task<ExecuteResult> ExecuteAsync(
            string code,
            int? timeoutMs,
            CancellationToken ct = default)
        {
            var response = await SendAsync("execute", new { code, timeout_ms = timeoutMs }, ct)
                .ConfigureAwait(false);

            return DecodeResult<ExecuteResult>(response)
                ?? throw new AcadTransportException(
                    AcadTransportFailure.PipeBroken,
                    "Server returned an empty execute result.");
        }

        // Generic typed call for non-execute methods (batch.*, ping, etc.).
        // Tools / resources use this to invoke the plugin's RPC surface and
        // get a typed result back.
        public async Task<T> CallAsync<T>(string method, object? @params, CancellationToken ct = default)
        {
            var response = await SendAsync(method, @params, ct).ConfigureAwait(false);
            return DecodeResult<T>(response)
                ?? throw new AcadTransportException(
                    AcadTransportFailure.PipeBroken,
                    $"Server returned an empty result for '{method}'.");
        }

        // Raw call — returns the JsonElement result for callers that want
        // to format their own response (MCP resources returning JSON text).
        public async Task<JsonElement> CallRawAsync(string method, object? @params, CancellationToken ct = default)
        {
            var response = await SendAsync(method, @params, ct).ConfigureAwait(false);
            if (response.Error is { } err) throw new AcadRpcException(err.Code, err.Message);
            if (response.Result is JsonElement el) return el;
            throw new AcadTransportException(
                AcadTransportFailure.PipeBroken,
                "Unexpected response shape (no result, no error).");
        }

        // Retry loop: re-resolve PID per attempt (so the user typing
        // ACDMCP_START mid-retry is picked up), connect with that
        // attempt's deadline, and on success do the one-shot request/
        // response. AcadTransportException with transient reasons triggers
        // another iteration; everything else propagates.
        private async Task<JsonRpcResponse> SendAsync(string method, object? @params, CancellationToken ct)
        {
            AcadTransportException? lastTransient = null;

            for (int attempt = 0; attempt < _retry.AttemptTimeoutsMs.Count; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                PidResolution resolution;
                try
                {
                    resolution = await _discovery.ResolveAsync(_explicitPid, ct).ConfigureAwait(false);
                }
                catch (AcadTransportException ex) when (IsRetryable(ex))
                {
                    lastTransient = ex;
                    await Task.Delay(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                    continue;
                }

                if (resolution.IsTransient)
                {
                    // Plugin process is up but pipe isn't listening yet
                    // (drawing-load / ACDMCP_START race). Treat as
                    // transient: wait this attempt's quantum then try
                    // again. The wait doubles as the connect timeout
                    // budget for this iteration.
                    lastTransient = new AcadTransportException(
                        AcadTransportFailure.PipeNotListening,
                        $"AutoCAD PID {resolution.Pid} is up, but pipe " +
                        $"'{PipeProber.PipeNameFor(resolution.Pid)}' isn't listening yet. " +
                        "Did you run ACDMCP_START?");
                    await Task.Delay(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                    continue;
                }

                var pipe = new PipeClient(resolution.Pid);
                NamedPipeClientStream stream;
                try
                {
                    stream = await pipe.ConnectAsync(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    lastTransient = new AcadTransportException(
                        AcadTransportFailure.PipeNotListening,
                        $"Connect to pipe '{pipe.PipeName}' timed out.");
                    continue;
                }

                await using (stream)
                {
                    try
                    {
                        return await pipe.SendOnAsync(stream, method, @params, ct).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        // Post-connect I/O failure — pipe was torn down
                        // mid-call (plugin hot-reload, ACDMCP_STOP).
                        // Retry: a fresh connect may land on a freshly
                        // restarted listener.
                        lastTransient = new AcadTransportException(
                            AcadTransportFailure.PipeBroken,
                            $"Pipe '{pipe.PipeName}' broke mid-call: {ex.Message}", ex);
                        continue;
                    }
                }
            }

            throw lastTransient ?? new AcadTransportException(
                AcadTransportFailure.PipeNotListening,
                "Connect retries exhausted.");
        }

        private static bool IsRetryable(AcadTransportException ex) =>
            ex.Reason is AcadTransportFailure.PipeNotListening
                      or AcadTransportFailure.PipeBroken;

        private static T? DecodeResult<T>(JsonRpcResponse response)
        {
            if (response.Error is { } err)
                throw new AcadRpcException(err.Code, err.Message);

            if (response.Result is JsonElement el)
                return el.Deserialize<T>(FrameIO.JsonOptions);

            throw new AcadTransportException(
                AcadTransportFailure.PipeBroken,
                "Unexpected response shape (no result, no error).");
        }
    }

    // Transport / protocol errors only. Snippet compile/runtime errors travel
    // inside ExecuteResult (Success=false) and do NOT throw.
    public sealed class AcadRpcException : Exception
    {
        public int Code { get; }
        public AcadRpcException(int code, string message) : base(message) { Code = code; }
    }
}
