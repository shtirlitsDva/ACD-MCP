using System.Text.Encodings.Web;
using System.Text.Json;
using Acd.Mcp.Bridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

int? explicitPid = ParseExplicitPid(args);

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport owns stdout. All logging MUST go to stderr or it corrupts
// the JSON-RPC stream. The framework default is stdout — override here.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The stdio channel is a JSON-RPC byte stream, not an HTML document. Relax the
// default HTML-safe encoder so '<', '>', '&' and quotes in tool results (script
// output, drawing names, generic type names, XML) emit literally instead of as
// <-style escapes.
var serializerOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

builder.Services.AddSingleton(new AcadClient(explicitPid));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: serializerOptions)
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();

static int? ParseExplicitPid(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--pid" && int.TryParse(args[i + 1], out var pid))
            return pid;
    }
    return null;
}
