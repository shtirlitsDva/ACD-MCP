using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Ui;

namespace Acd.Mcp.Pipe
{
    // Pipe RPC surface for plugin-side status queries. Single method:
    //   acdmcp.status — returns a structured snapshot of every capability
    //                   the bridge can route to. Used by the agent's
    //                   skill to self-diagnose when a tool call fails;
    //                   also surfaced through `ACDMCP_STATUS` for users.
    //
    // The payload shape is stable on purpose — the agent's skill
    // (acd-mcp:script / :batch) reads the JSON and branches on the
    // `ready` / `degraded` / `unavailable` value per capability.
    internal sealed class StatusRpcHandler
    {
        private readonly Func<StatusSnapshot> _snapshot;

        public StatusRpcHandler(Func<StatusSnapshot> snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            return method switch
            {
                "acdmcp.status" => Task.FromResult<object?>(_snapshot()),
                _ => Task.FromResult<object?>(null),
            };
        }
    }

    // Wire shape for acdmcp.status. Each capability carries one of
    // "ready" / "degraded:<reason>" / "unavailable:<reason>" so the
    // agent can branch on a single string compare per capability.
    public sealed record StatusSnapshot(
        string version,
        int pid,
        string pipe,
        CapabilityState script_execute,
        CapabilityState script_propose,
        CapabilityState batch_propose,
        CapabilityState batch_run_test,
        CapabilityState batch_list_files,
        CapabilityState dto);

    public sealed record CapabilityState(string status, string? reason);
}
