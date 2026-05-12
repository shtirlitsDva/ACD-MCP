using System.Text.Json;

namespace Acd.Mcp.Serialization
{
    // Pipe RPC surface for DTO diagnostics. The pipe listener forwards any
    // method that starts with "dto." to this handler.
    //
    // One method for now:
    //   dto.diagnostics — current list of compile failures, one entry per
    //                     DTO file that didn't successfully compile.
    public sealed class DtoRpcHandler
    {
        private readonly DtoDiagnostics _diagnostics;

        public DtoRpcHandler(DtoDiagnostics diagnostics)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            return method switch
            {
                "dto.diagnostics" => Task.FromResult<object?>(GetDiagnostics()),
                _ => Task.FromResult<object?>(null),
            };
        }

        private object GetDiagnostics()
        {
            var entries = _diagnostics.All.Select(f => new
            {
                source = f.Source,
                header_type = f.HeaderType,
                resolved_type = f.ResolvedType?.FullName,
                message = f.Message,
                line = f.Line,
                column = f.Column,
                error_code = f.ErrorCode,
            }).ToList();
            return new
            {
                count = entries.Count,
                entries,
            };
        }
    }
}
