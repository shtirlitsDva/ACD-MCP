using System.IO.Pipes;
using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Rvt.Mcp.Bridge
{
    // Liveness probe for the rvt-mcp-{pid} pipe. Virtual so tests inject an
    // in-memory implementation.
    public class RevitPipeProber
    {
        public static string PipeNameFor(int pid) => $"rvt-mcp-{pid}";

        public virtual async Task<bool> IsListeningAsync(
            int pid, TimeSpan timeout, CancellationToken ct = default)
        {
            await using var client = new NamedPipeClientStream(
                ".", PipeNameFor(pid), PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { return false; }
            catch (IOException) { return false; }
        }
    }

    // One-shot pipe client: fresh connection per call, resilient to plugin
    // restarts. Retry policy lives in RevitClient.
    internal sealed class RevitPipeClient
    {
        private readonly int _revitPid;
        public string PipeName => RevitPipeProber.PipeNameFor(_revitPid);

        public RevitPipeClient(int revitPid) => _revitPid = revitPid;

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

        public async Task<JsonRpcResponse> SendOnAsync(
            NamedPipeClientStream client, string method, object? @params, CancellationToken ct)
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
                throw new RvtTransportException(
                    RvtTransportFailure.PipeBroken,
                    "Pipe closed before a response was received.");
            return response;
        }
    }
}
