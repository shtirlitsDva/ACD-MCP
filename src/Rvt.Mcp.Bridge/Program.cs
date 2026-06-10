using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rvt.Mcp.Bridge;

int? explicitPid = ParseArgValue(args, "--pid");

// --selftest "<code>" runs one execute round-trip against the live Revit and
// prints the raw ExecuteResult JSON — the smoke-test path that needs no MCP
// client. Also accepts --ping for a transport-only check.
if (Array.IndexOf(args, "--ping") >= 0)
{
    var client = new RevitClient(explicitPid);
    var pong = await client.CallRawAsync("ping", null);
    Console.WriteLine(pong.GetRawText());
    return 0;
}

if (GetArgString(args, "--selftest") is string code)
{
    var client = new RevitClient(explicitPid);
    var result = await client.ExecuteAsync(code, timeoutMs: 120_000);
    Console.WriteLine(JsonSerializer.Serialize(result, Acd.Mcp.Pipe.FrameIO.JsonOptions));
    return result.Success ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport owns stdout — all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new RevitClient(explicitPid));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static int? ParseArgValue(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name && int.TryParse(args[i + 1], out var v))
            return v;
    }
    return null;
}

static string? GetArgString(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}
