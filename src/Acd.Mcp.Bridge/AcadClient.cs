using System.Text.Json;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Bridge
{
    // Public front for everything Bridge code (MCP tools, host) needs about
    // "talking to AutoCAD". Hides:
    //   - AutoCAD instance discovery (PID enumeration / explicit override)
    //   - JSON-RPC method names and parameter shapes
    //   - the named-pipe transport
    //   - response envelope unwrapping (error vs result)
    //
    // PID resolution happens per call so AutoCAD restarts (or DevReload reloads)
    // are transparent. The cost is one process enumeration per call (~ms).
    public sealed class AcadClient
    {
        private readonly int? _explicitPid;

        public AcadClient(int? explicitPid = null)
        {
            _explicitPid = explicitPid;
        }

        public async Task<ExecuteResult> ExecuteAsync(
            string code,
            int? timeoutMs,
            CancellationToken ct = default)
        {
            var response = await SendAsync("execute", new { code, timeout_ms = timeoutMs }, ct)
                .ConfigureAwait(false);

            return DecodeResult<ExecuteResult>(response)
                ?? throw new IOException("Server returned an empty execute result.");
        }

        private async Task<JsonRpcResponse> SendAsync(string method, object? @params, CancellationToken ct)
        {
            int pid = AutoCadDiscovery.ResolvePid(_explicitPid);
            var pipe = new PipeClient(pid);
            return await pipe.SendAsync(method, @params, ct: ct).ConfigureAwait(false);
        }

        private static T? DecodeResult<T>(JsonRpcResponse response)
        {
            if (response.Error is { } err)
                throw new AcadRpcException(err.Code, err.Message);

            if (response.Result is JsonElement el)
                return el.Deserialize<T>(FrameIO.JsonOptions);

            // Result was an in-memory object that round-tripped through System.Text.Json
            // as a JsonElement above; if we got here the response was malformed.
            throw new IOException("Unexpected response shape (no result, no error).");
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
