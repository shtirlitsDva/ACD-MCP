using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acd.Mcp.Pipe
{
    // JSON-RPC 2.0 over a named pipe with length-prefixed frames:
    //   [4-byte big-endian length][UTF-8 JSON payload]
    // Length prefix avoids escaping arbitrary newlines in user code.
    //
    // This file is intentionally AutoCAD-free so it can be shared with the
    // out-of-process bridge via a linked compile item.

    public sealed class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("method")] public string Method { get; set; } = "";
        [JsonPropertyName("params")] public JsonElement Params { get; set; }
    }

    public sealed class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement Id { get; set; }

        [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcError? Error { get; set; }

        public static JsonRpcResponse Ok(JsonElement id, object? result) =>
            new() { Id = id, Result = result };

        public static JsonRpcResponse Err(JsonElement id, int code, string message) =>
            new() { Id = id, Error = new JsonRpcError { Code = code, Message = message } };
    }

    public sealed class JsonRpcError
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    public static class ErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        // -32000..-32099 reserved for server-defined errors.
        public const int ExecuteError = -32000;
    }

    public static class FrameIO
    {
        private const int MaxFrameBytes = 16 * 1024 * 1024;

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        // Returns null on clean disconnect before a new frame starts.
        public static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct) where T : class
        {
            var lenBuf = new byte[4];
            if (!await ReadExactAsync(stream, lenBuf, 0, 4, ct).ConfigureAwait(false))
                return null;

            int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len <= 0 || len > MaxFrameBytes)
                throw new InvalidDataException($"Frame length out of range: {len}");

            var payload = new byte[len];
            if (!await ReadExactAsync(stream, payload, 0, len, ct).ConfigureAwait(false))
                throw new EndOfStreamException("Truncated frame payload.");

            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }

        public static async Task WriteFrameAsync<T>(Stream stream, T payload, CancellationToken ct)
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            var lenBuf = new byte[4]
            {
                (byte)((data.Length >> 24) & 0xFF),
                (byte)((data.Length >> 16) & 0xFF),
                (byte)((data.Length >> 8) & 0xFF),
                (byte)(data.Length & 0xFF),
            };
            await stream.WriteAsync(lenBuf, ct).ConfigureAwait(false);
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buf.AsMemory(offset + total, count - total), ct).ConfigureAwait(false);
                if (n == 0) return total == count;
                total += n;
            }
            return true;
        }
    }
}
