using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Bridge
{
    // One-shot pipe client. Each call opens a fresh connection, sends a single
    // JSON-RPC request, reads one response, and closes. Slightly more I/O than a
    // persistent connection but simpler and resilient to plugin hot-reload —
    // every call independently rediscovers the pipe.
    //
    // Internal: callers should use AcadClient, which exposes typed methods instead
    // of the raw (method, params) shape.
    internal sealed class PipeClient
    {
        private readonly int _autocadPid;
        public string PipeName => $"acd-mcp-{_autocadPid}";

        public PipeClient(int autocadPid)
        {
            _autocadPid = autocadPid;
        }

        public async Task<JsonRpcResponse> SendAsync(
            string method,
            object? @params,
            int connectTimeoutMs = 5000,
            CancellationToken ct = default)
        {
            await using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new IOException(
                    $"Could not connect to AutoCAD pipe '{PipeName}'. Is the Acd.Mcp listener running? " +
                    "(Run ACDMCP_START inside AutoCAD.)");
            }

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
                throw new IOException("Pipe closed before a response was received.");
            return response;
        }
    }
}
