using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Bridge
{
    // One-shot pipe client. Each call opens a fresh connection, sends a single
    // JSON-RPC request, reads one response, and closes. Slightly more I/O than a
    // persistent connection but simpler and resilient to plugin hot-reload —
    // every call independently rediscovers the pipe and re-resolves the PID.
    //
    // The retry policy lives one layer up in AcadClient so it can re-run
    // PID resolution between attempts (the user typing ACDMCP_START
    // mid-retry should be picked up automatically).
    internal sealed class PipeClient
    {
        private readonly int _autocadPid;
        public string PipeName => PipeProber.PipeNameFor(_autocadPid);

        public PipeClient(int autocadPid)
        {
            _autocadPid = autocadPid;
        }

        // Connect-only. Returns a stream the caller must dispose. Throws
        // TimeoutException on connect deadline; AcadClient wraps that into
        // AcadTransportException with Reason=PipeNotListening after the
        // retry loop gives up.
        public async Task<NamedPipeClientStream> ConnectAsync(int connectTimeoutMs, CancellationToken ct)
        {
            var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Sends one request on an already-connected stream. The caller
        // owns the stream lifetime (open via ConnectAsync, dispose via
        // `await using`). Splitting connect and send lets the retry loop
        // distinguish "couldn't reach plugin" from "plugin replied with
        // an error."
        public async Task<JsonRpcResponse> SendOnAsync(
            NamedPipeClientStream client,
            string method,
            object? @params,
            CancellationToken ct)
        {
            var request = new JsonRpcRequest
            {
                Id = JsonSerializer.SerializeToElement(1),
                Method = method,
                Params = @params is null
                    ? JsonSerializer.SerializeToElement(new { })
                    : JsonSerializer.SerializeToElement(@params, FrameIO.JsonOptions),
            };

            await FrameIO.WriteFrameAsync(client, request, ct).ConfigureAwait(false);
            var response = await FrameIO.ReadFrameAsync<JsonRpcResponse>(client, ct).ConfigureAwait(false);
            if (response is null)
                throw new AcadTransportException(
                    AcadTransportFailure.PipeBroken,
                    "Pipe closed before a response was received.");
            return response;
        }
    }
}
