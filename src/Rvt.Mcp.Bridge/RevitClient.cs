using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp;
using Acd.Mcp.Pipe;

namespace Rvt.Mcp.Bridge
{
    // Public front for "talking to Revit" — discovery, connect retries,
    // JSON-RPC envelope handling. Mirrors Acd.Mcp.Bridge.AcadClient.
    public sealed class RevitClient
    {
        private readonly int? _explicitPid;
        private readonly RevitDiscovery _discovery;
        private readonly ConnectRetryPolicy _retry;

        public RevitClient(
            int? explicitPid = null,
            RevitDiscovery? discovery = null,
            ConnectRetryPolicy? retry = null)
        {
            _explicitPid = explicitPid;
            _discovery = discovery ?? RevitDiscovery.Default;
            _retry = retry ?? ConnectRetryPolicy.Default;
        }

        public async Task<ExecuteResult> ExecuteAsync(
            string code, int? timeoutMs, CancellationToken ct = default)
        {
            var response = await SendAsync("execute", new { code, timeout_ms = timeoutMs }, ct)
                .ConfigureAwait(false);

            return DecodeResult<ExecuteResult>(response)
                ?? throw new RvtTransportException(
                    RvtTransportFailure.PipeBroken,
                    "Server returned an empty execute result.");
        }

        public async Task<JsonElement> CallRawAsync(string method, object? @params, CancellationToken ct = default)
        {
            var response = await SendAsync(method, @params, ct).ConfigureAwait(false);
            if (response.Error is { } err) throw new RvtRpcException(err.Code, err.Message);
            if (response.Result is JsonElement el) return el;
            throw new RvtTransportException(
                RvtTransportFailure.PipeBroken,
                "Unexpected response shape (no result, no error).");
        }

        private async Task<JsonRpcResponse> SendAsync(string method, object? @params, CancellationToken ct)
        {
            RvtTransportException? lastTransient = null;

            for (int attempt = 0; attempt < _retry.AttemptTimeoutsMs.Count; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                PidResolution resolution;
                try
                {
                    resolution = await _discovery.ResolveAsync(_explicitPid, ct).ConfigureAwait(false);
                }
                catch (RvtTransportException ex) when (IsRetryable(ex))
                {
                    lastTransient = ex;
                    await Task.Delay(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                    continue;
                }

                if (resolution.IsTransient)
                {
                    lastTransient = new RvtTransportException(
                        RvtTransportFailure.PipeNotListening,
                        $"Revit PID {resolution.Pid} is up, but pipe " +
                        $"'{RevitPipeProber.PipeNameFor(resolution.Pid)}' isn't listening yet. " +
                        "Is the Rvt.Mcp add-in deployed (and Revit past its first idle)?");
                    await Task.Delay(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                    continue;
                }

                var pipe = new RevitPipeClient(resolution.Pid);
                NamedPipeClientStream stream;
                try
                {
                    stream = await pipe.ConnectAsync(_retry.AttemptTimeoutsMs[attempt], ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    lastTransient = new RvtTransportException(
                        RvtTransportFailure.PipeNotListening,
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
                        lastTransient = new RvtTransportException(
                            RvtTransportFailure.PipeBroken,
                            $"Pipe '{pipe.PipeName}' broke mid-call: {ex.Message}", ex);
                        continue;
                    }
                }
            }

            throw lastTransient ?? new RvtTransportException(
                RvtTransportFailure.PipeNotListening,
                "Connect retries exhausted.");
        }

        private static bool IsRetryable(RvtTransportException ex) =>
            ex.Reason is RvtTransportFailure.PipeNotListening
                      or RvtTransportFailure.PipeBroken;

        private static T? DecodeResult<T>(JsonRpcResponse response)
        {
            if (response.Error is { } err)
                throw new RvtRpcException(err.Code, err.Message);

            if (response.Result is JsonElement el)
                return el.Deserialize<T>(FrameIO.JsonOptions);

            throw new RvtTransportException(
                RvtTransportFailure.PipeBroken,
                "Unexpected response shape (no result, no error).");
        }
    }

    // Protocol-level error reply from the plugin (transport worked).
    public sealed class RvtRpcException : Exception
    {
        public int Code { get; }
        public RvtRpcException(int code, string message) : base(message) { Code = code; }
    }
}
