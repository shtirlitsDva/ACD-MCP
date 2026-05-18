<doc-overview>
Diagnosis of "tool unavailable after AutoCAD restart" and code-review findings for
fragility in `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\ACD-MCP`.

Scope: only the bridge + plugin lifecycle was reviewed; not the Batch/Script engines themselves.
</doc-overview>

<diagnosis>
<short-answer>
Yes — restarting AutoCAD while a long-lived Claude Code session is active can absolutely cause the symptom the other session reported. But **not because the MCP tool list changed mid-session.** The tool list is statically baked at bridge startup and never mutates. What broke was almost certainly the *runtime call* — the bridge accepted the call, but the AutoCAD plugin returned a hard error because some prerequisite (palette open / pipe up / single-instance discovery) wasn't satisfied in the new AutoCAD. The other agent then summarised that as "the tool isn't available," which is imprecise but understandable.
</short-answer>

<most-likely-root-cause-palette-not-reopened>
`autocad_repl_propose_script` and `autocad_batch_propose_script` route through pipe methods `script.*` / `batch.*`. The plugin **only registers those RPC handlers when the user runs `ACDMCP_PALETTE` inside AutoCAD**:

```csharp
// src/Acd.Mcp/McpPlugin.cs:283-318
[CommandMethod("ACDMCP_PALETTE")]
public static void ShowPalette() => SafeBoundary.Run("ACDMCP_PALETTE", () =>
{
    ...
    _batchRpc  = new BatchRpcHandler(_batchExecutor!, _palette.BatchViewModel);
    _scriptRpc = new ScriptRpcHandler(_scriptEditor!);
    _palette.Visible = true;
});
```

```csharp
// src/Acd.Mcp/McpPlugin.cs:328-340
if (method.StartsWith("script."))
{
    if (_scriptRpc is null)
        throw new InvalidOperationException(
            "SCRIPT palette is not open. Run ACDMCP_PALETTE inside AutoCAD first.");
    ...
}
```

So when you restart AutoCAD:
1. New AutoCAD starts. Plugin loads.
2. You run `ACDMCP_START` to open the pipe (RELEASE mode requires this).
3. Session B (long-lived) finally tries `autocad_repl_propose_script` — its bridge connects to the new pipe fine (PID is re-resolved per call, so it picks up the new AutoCAD automatically).
4. Plugin throws `InvalidOperationException("SCRIPT palette is not open...")`.
5. Bridge propagates that as `AcadRpcException`, the tool wrapper returns an error in its envelope, and the agent reads it as "tool not available."

`autocad_execute_csharp` works fine in the same scenario because it doesn't depend on the palette — its handler is registered when `ACDMCP_START` runs, not later. So you get the asymmetric symptom you described.

**Fix path for the immediate symptom:** run `ACDMCP_PALETTE` in the new AutoCAD instance. The other Claude session should then succeed.
</most-likely-root-cause-palette-not-reopened>

<other-restart-failure-modes-ranked>
- **Pipe not yet listening (RELEASE mode).** If `ACDMCP_START` wasn't run yet in the new AutoCAD, `NamedPipeClientStream.ConnectAsync` times out at 5 s and the tool reports "pipe unavailable." Recovers automatically once `ACDMCP_START` is run — no agent state to invalidate.
- **Multiple `acad.exe` processes.** `AutoCadDiscovery.ResolvePid` (bridge side) throws "Multiple AutoCAD instances found" if it sees more than one — a zombie acad.exe from a crashed previous run breaks every call until killed.
- **Bridge process killed.** If the bridge subprocess actually died (e.g. OOM, uncaught exception in the SDK), Claude Code would mark the entire MCP server as down — but that's all-or-nothing, not selective. You'd lose `autocad_execute_csharp` too, which contradicts the reported symptom. So this isn't what happened here.
</other-restart-failure-modes-ranked>

<why-the-tool-list-itself-cannot-vanish>
The bridge registers tools with `WithToolsFromAssembly()` at startup. The list is fixed for the bridge process lifetime; there's no path to remove or hide tools at runtime, and the bridge does not send `notifications/tools/list_changed`. So Session B and Session A see the *same* tool list. The difference is what each tool *does* when called.
</why-the-tool-list-itself-cannot-vanish>
</diagnosis>

<fragility-findings>
Below is what I'd flag in `ACD-MCP` after this exercise. Ranked roughly by impact-on-user-frustration.

<finding-1-palette-gating-is-the-design-smell>
The handler-registration design conflates "the pipe is up" with "the UI is open." That's the proximate cause of your reported bug, and it'll keep biting you across every AutoCAD restart, drawing-load cycle, or palette-close.

Locations:
- `src/Acd.Mcp/McpPlugin.cs:309-316` (registration in `ShowPalette`)
- `src/Acd.Mcp/McpPlugin.cs:328-340` (dispatcher throws when null)

The comment at lines 310-315 acknowledges the trade-off explicitly: gating on palette-open avoids "silent staging into the void." That's a reasonable concern, but the chosen remedy is worse than the disease — the user pays the cost on every restart.

Cleaner shapes, ranked:
1. **Register the RPC handlers at `ACDMCP_START`, not at palette open.** The script-editor and batch-VM dependencies become lazily resolved at *dispatch* time instead of registration time: the handler can auto-open the palette if it isn't open yet, or buffer the proposal and surface a notice. The agent's UX stays clean either way.
2. **Auto-open the palette on `ACDMCP_START`.** Less elegant (couples two commands), but if you don't want the lazy-init complexity, this kills the bug instantly.
3. **Keep gating, but signal degraded state.** The error message is already good ("Run ACDMCP_PALETTE inside AutoCAD first.") — bubble it up to the agent more prominently, e.g. as a structured error code Claude Code can recognise. Lowest churn, highest cognitive load.

I'd take #1.
</finding-1-palette-gating-is-the-design-smell>

<finding-2-explicit-pid-pin-survives-restart>
`AcadClient` is a singleton with the explicit PID captured at construction:

```csharp
// src/Acd.Mcp.Bridge/Program.cs:15
builder.Services.AddSingleton(new AcadClient(explicitPid));
```

```csharp
// src/Acd.Mcp.Bridge/AutoCadDiscovery.cs:17-31
public static int ResolvePid(int? explicitPid)
{
    if (explicitPid is int pid)
    {
        try { _ = Process.GetProcessById(pid); return pid; }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"No process with PID {pid}.");
        }
    }
    ...
}
```

If anyone ever launches the bridge with `--pid <N>` in the MCP config, that PID is welded to the bridge for its whole life. The first AutoCAD restart kills the bridge dead — every subsequent call throws "No process with PID <N>" with no automatic recovery.

The current default (no `--pid`) re-resolves per call and survives restarts gracefully, but the `--pid` path is a footgun for anyone reading the README and pinning a PID.

Fix: treat `--pid` as a *preference*, not a *pin*. If the explicit PID is dead, fall through to `FindAutoCadPids()` and either auto-pick the sole instance or surface a clear "your pinned PID is gone" error.
</finding-2-explicit-pid-pin-survives-restart>

<finding-3-multi-instance-discovery-is-brittle>
`ResolvePid` throws when more than one `acad.exe` is running:

```csharp
// src/Acd.Mcp.Bridge/AutoCadDiscovery.cs:40-42
_ => throw new InvalidOperationException(
    $"Multiple AutoCAD instances found (PIDs: {string.Join(", ", found)}). " +
    "Pass --pid <PID> to disambiguate."),
```

This catches the user out in three common situations:
- A zombie/hung previous AutoCAD that didn't fully exit before restart.
- Civil 3D + plain AutoCAD coexisting on the same workstation.
- Multiple genuine AutoCAD sessions (e.g. one for batch, one for live work).

The good signal of "this is the AutoCAD I want to talk to" is **the one that has the `acd-mcp-{pid}` pipe listening.** That's exactly what disambiguates ours from theirs.

Fix: in the multi-instance branch, probe each candidate PID by trying a fast `NamedPipeClientStream.ConnectAsync` against `acd-mcp-{pid}` with a tight 100-200 ms timeout. The one(s) that accept are ours. If exactly one accepts, use it; if zero do, none of them have the plugin loaded; if more than one, *now* fall back to "pass --pid."

Bonus: this also makes finding-2 cleaner — the pinned PID is verified to actually have the pipe.
</finding-3-multi-instance-discovery-is-brittle>

<finding-4-5s-timeout-and-no-backoff>
`PipeClient.SendAsync` connects with a 5-second timeout per call and no retry:

```csharp
// src/Acd.Mcp.Bridge/PipeClient.cs:24-36
public async Task<JsonRpcResponse> SendAsync(
    string method,
    object? @params,
    int connectTimeoutMs = 5000,
    CancellationToken ct = default)
{
    await using var client = new NamedPipeClientStream(
        ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    try { await client.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false); }
    catch (TimeoutException) { ... }
    ...
}
```

In practice, AutoCAD's plugin doesn't always have the pipe up the moment a user "restarts" it — drawing load, AECC stack initialisation, the user typing `ACDMCP_START` — easily 10-30 s. The first call from the agent after restart hits the 5 s wall, returns a hard error, and the agent may give up or report "not available."

Fix: at the transport layer, retry the connect 2-3 times with a short backoff (200 ms → 800 ms → 2 s) before surfacing the failure. Keeps the happy path at 5 s, but tolerates the restart window.
</finding-4-5s-timeout-and-no-backoff>

<finding-5-source-binary-drift>
The source on disk has the tool renaming refactor underway:

| In source (`src/Acd.Mcp.Bridge/Tools/*.cs`) | Deployed (`%USERPROFILE%\.claude\plugins\cache\acd-mcp\...`) |
| --- | --- |
| `autocad_script_execute` | `autocad_execute_csharp` |
| `autocad_script_propose` | `autocad_repl_propose_script` |
| `autocad_batch_propose_script` | `autocad_batch_propose_script` (matches) |
| `autocad_batch_run_test` | `autocad_batch_run_test` (matches) |
| `autocad_batch_get_selection` | `autocad_batch_get_selection` (matches) |

The deployed plugin doesn't reflect the new tool names. Two corollaries:
- Anyone reading current source will be confused about which names actually work in their session until the rename ships.
- The `script-editor-refactor` worktree, the rename, the design doc in `docs/design/script-editor-refactor-v1.md`, and the deployed plugin are three separate states. Worth either shipping the rename quickly or marking it "in progress" prominently.

This isn't a fragility-of-the-code issue per se — it's a release-hygiene issue — but it surfaced as part of this review, so flagging it (per global rule #3).
</finding-5-source-binary-drift>

<finding-6-debug-vs-release-divergence-in-autostart>
DEBUG auto-starts the pipe listener on `Application.Idle`; RELEASE requires the user to run `ACDMCP_START`. (Per the plugin-side review.)

The asymmetry means anyone testing on a debug build won't notice the missing-start gotcha that a release-build user will hit on every fresh AutoCAD session. Either:
- Auto-start in RELEASE too (with an opt-out for users who want manual control), or
- Add a notice on plugin load that explicitly tells the user "run `ACDMCP_START` to enable the agent connection."

I'd lean toward auto-start in RELEASE, on the principle that the user installed this plugin specifically so the agent could reach in — the manual command is friction without benefit.
</finding-6-debug-vs-release-divergence-in-autostart>

<finding-7-no-list-changed-notification>
The bridge never emits `notifications/tools/list_changed`. That's fine *given* the current design (tools are static), but means the agent can never be told "right now, palette is closed, so script/batch propose tools will fail." If you keep palette-gating (rejecting my recommendation in finding-1), then surfacing this state via tool annotations or a separate "status" resource is the next-best mitigation.

Lower priority; pre-empted by finding-1 if you fix that.
</finding-7-no-list-changed-notification>
</fragility-findings>

<recommended-next-steps>
1. **Quick unblock for the other Claude session right now:** run `ACDMCP_PALETTE` inside your current AutoCAD (the one this very session is talking to). The other session's propose-script tools should start working immediately, because the pipe is the same and the handler will now be wired.
2. **One-line fix that kills 80% of the bug class:** in `McpPlugin.cs`, move the `_scriptRpc` / `_batchRpc` construction out of `ShowPalette` and into `TryEnsureCore` (or the `ACDMCP_START` path). Side-effect-free for the palette-open path, eliminates the "agent calls before palette" failure.
3. **Slightly bigger but worth it:** make the bridge probe the pipe to disambiguate multi-instance AutoCAD (finding-3) and add a 2-3 step backoff on connect (finding-4). That makes restart genuinely transparent end-to-end.
4. **Release hygiene:** decide whether the tool rename is shipping soon, and if not, either revert or finish it. The half-renamed state is a documentation hazard.
</recommended-next-steps>
</content>