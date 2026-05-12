<!--
ACD-MCP architecture proposal.
Status: draft v2 (after reading DevReload).
Target: AutoCAD 2025+ (.NET 8) only.
-->

<context>
<input-1>The two ChatGPT transcripts under `docs/research/`.</input-1>
<input-2>The user's existing DevReload project at `C:\Users\MichailGolubjev\Desktop\GitHub\shtirlitsDva\DevReload\`.</input-2>

DevReload already implements the entire reloadable-plugin pattern the ChatGPT convo
sketched, and does it more thoroughly than the convo proposed:
  - Collectible `IsolatedPluginContext : AssemblyLoadContext` with `AssemblyDependencyResolver`,
    stream-loaded bytes (no file lock), shared-assemblies fall-through for WPF XAML.
  - `PluginHost<TPlugin>` that loads/unloads the ALC and finds an `IExtensionApplication`.
  - `CommandRegistrar` using `Autodesk.AutoCAD.Internal.Utils.AddCommand` /
    `RemoveCommand` so commands can actually be unregistered before unload — AutoCAD's
    permanent `CommandClass.AddCommand` cannot be undone, which is the subtlety the
    ChatGPT convo missed.
  - `{PREFIX}LOAD` / `{PREFIX}DEV` / `{PREFIX}UNLOAD` per registered plugin.
  - WPF management palette, plugins.json config, VS project discovery for adding plugins.

Implication: the "loader" half of my v1 design is already built. ACD-MCP should be a
vanilla plugin that registers with DevReload during development, not a parallel
loader/reloadable pair. That collapses the project count from four to two and removes a
whole class of bugs (cross-ALC interface identity, stale-version detection, command
cleanup) because DevReload already handles them.
</context>

<goal>
Expose a live, stateful C# REPL inside a running AutoCAD process as an MCP tool, so an
MCP client (Claude Desktop, etc.) can execute arbitrary .NET against the active drawing.

Concretely: `execute_csharp` tool call from outside → snippet runs on AutoCAD's main
thread under a document lock, with `App`/`Doc`/`Db`/`Ed` globals pre-bound, with state
persisted across calls. Errors and stdout come back in the tool response.
</goal>

<non-goals>
- Sandboxing the snippet — no sandbox exists on .NET 8. This is a trusted developer tool.
- Supporting AutoCAD 2024 and earlier (.NET Framework). DevReload is 2025+ only; we
  inherit that constraint.
- A REPL palette UI. Nice extra but not part of the MCP slice. Could be added later.
- Reimplementing what DevReload already does (collectible ALC, removable commands).
</non-goals>

<solution-layout>
One solution, two projects.

<project name="Acd.Mcp">
  Target: `net8.0-windows8.0`, `x64`, plugin DLL.
  References: AutoCAD managed APIs (`accoremgd`, `acdbmgd`, `acmgd`, `AdWindows`),
              `Microsoft.CodeAnalysis.CSharp.Scripting` (NuGet).
  
  This is a *vanilla* AutoCAD plugin. It does NOT contain a custom loader. It implements
  `IExtensionApplication`, registers a few `[CommandMethod]`s, and is consumed by either:
    (a) NETLOAD / autoload bundle in production, or
    (b) DevReload during development, registered with `MCPLOAD` / `MCPDEV` / `MCPUNLOAD`.
  
  Both modes work without any code change in this project — that's the whole point of
  DevReload's design.
  
  Layout:
    - `McpPlugin.cs` — `[assembly: ExtensionApplication(typeof(McpPlugin))]`, holds
      `IExtensionApplication.Initialize / Terminate`. Wires up the listener and script
      session. Captures the main-thread `SynchronizationContext`.
    - `Commands.cs` — `ACDMCP_START`, `ACDMCP_STOP`, `ACDMCP_STATUS`, `ACDMCP_RESET`.
      Manual control surface for the user inside AutoCAD. Useful even without an MCP
      client connected.
    - `Pipe/PipeListener.cs` — named pipe `acd-mcp-{pid}`, length-prefixed JSON frames,
      one task per accepted client, per-request main-thread dispatch.
    - `Pipe/Protocol.cs` — request/response DTOs serialized with `System.Text.Json`.
    - `Scripting/ScriptSession.cs` — wraps `ScriptState`. One session per plugin
      lifetime, `Reset()` drops it.
    - `Scripting/AcadGlobals.cs` — `App`, `Doc`, `Db`, `Ed` properties that re-resolve
      every access so switching drawings between calls Just Works.
    - `Scripting/ConsoleCapture.cs` — `IDisposable` that redirects `Console.Out/Err` for
      the duration of a single call. Sets are process-global; we accept that.
</project>

<project name="Acd.Mcp.Bridge">
  Target: `net8.0`, console app. No AutoCAD references.
  References: `ModelContextProtocol` (NuGet, official C# MCP SDK).
  
  This is the MCP server the client (Claude Desktop, etc.) actually talks to over stdio.
  It is a thin proxy between MCP and our named pipe.
  
  Layout:
    - `Program.cs` — boots the MCP server with one tool registered.
    - `PipeClient.cs` — connects to the AutoCAD pipe, sends requests, awaits replies.
      Auto-discovers: enumerates `acad.exe` processes, picks unique one, otherwise
      requires `--pid <n>`. Errors out cleanly if AutoCAD is not running.
    - `Tools/ExecuteCsharpTool.cs` — `execute_csharp(code, timeout_ms?)` MCP tool.
      Optionally later: `reset_session`, `ping`.
</project>

No Contract project, no Loader project, no Reloadable project. Single plugin DLL +
single bridge EXE.
</solution-layout>

<wire-protocol>
Between Bridge (external) and Acd.Mcp plugin (inside AutoCAD), over a named pipe.

Frame: length-prefixed JSON.
  [4-byte big-endian payload length][UTF-8 JSON payload]

Why length-prefixed and not newline-delimited: code payloads contain arbitrary newlines.

JSON-RPC 2.0 method surface:
  - `execute(code: string, timeout_ms?: number) -> ExecuteResult`
  - `reset() -> { ok: true }`
  - `ping() -> { autocad_pid: number, autocad_version: string, mcp_version: string }`

`ExecuteResult`:
  { success: bool,
    stdout: string,
    stderr: string,
    returnValueRepr: string?,
    diagnostics: [{ severity, message, line?, column?, file? }, ...],
    elapsed_ms: number }

This wire format is *internal*. The Bridge translates it to MCP. Keeping them separate
means we can add Bridge tools later (e.g. `list_drawing_entities`, `screenshot`) without
touching the Plugin until those tools need new server-side support — at which point we
add a new RPC method.
</wire-protocol>

<threading-model>
This is the part the ChatGPT conversation ignored and DevReload doesn't need to solve
(DevReload commands always run on the main thread already). For us it matters because
the pipe listener runs on background threads.

<facts>
1. AutoCAD managed APIs run on the main thread, under a document lock for any DB writes.
2. The pipe listener and per-connection readers are on threadpool threads.
3. AutoCAD's main thread has a WinForms message loop. During `IExtensionApplication.Initialize`,
   `SynchronizationContext.Current` is the `WindowsFormsSynchronizationContext` bound to it.
</facts>

<approach>
1. `McpPlugin.Initialize()` snapshots `_mainSync = SynchronizationContext.Current`. If
   null, fail loudly — something is wrong with the host.
2. Pipe listener `Task` runs on `Task.Run`. Each connection becomes its own loop reading
   frames.
3. Per request, the reader builds a `TaskCompletionSource<ExecuteResult>`, then:
       _mainSync.Post(_ => { /* on main thread */ ... tcs.SetResult(result); }, null);
       var result = await tcs.Task;
4. Inside the posted delegate (now on the main thread):
       var doc = Application.DocumentManager.MdiActiveDocument;
       if (doc == null) { tcs.SetResult(noDocResult); return; }
       using (doc.LockDocument())
       {
           var result = _session.Execute(code, ct).GetAwaiter().GetResult();
           tcs.SetResult(result);
       }
   The `.GetAwaiter().GetResult()` blocks the main thread for the duration of the
   snippet. That's intentional — running a snippet IS blocking AutoCAD's main thread,
   the same way any `[CommandMethod]` does.
5. Cancellation by `timeout_ms`: cooperative via `CancellationToken`. Snippets that
   spin without checking the token cannot be interrupted without process termination.
   Document this; do not pretend it's solvable.
</approach>

<deadlock-warning>
The pipe handler must never be invoked synchronously from the main thread (e.g. via a
`[CommandMethod]` that calls into it). It must always be on a threadpool thread when it
does `_mainSync.Post(...).Wait()`. By construction this holds, because the listener is
on `Task.Run` and the per-connection readers are inside that listener. Worth a comment
in code so a future maintainer doesn't break the invariant.
</deadlock-warning>

<modal-dialog-warning>
If AutoCAD is showing a modal dialog (plot preview, etc.), `_mainSync.Post` queues but
the message pump is busy with the modal. The call hangs until the user dismisses the
dialog. We don't try to solve this; `timeout_ms` surfaces it.
</modal-dialog-warning>
</threading-model>

<csharpscript-session>
<choice>
`Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript` with a persistent `ScriptState`,
not raw `CSharpCompilation` + `Assembly.Load`. Reasons covered in chat: top-level
statements, state persistence, `Globals`, cleaner diagnostics.
</choice>

<globals>
    public sealed class AcadGlobals
    {
        public Autodesk.AutoCAD.ApplicationServices.Document Doc =>
            Application.DocumentManager.MdiActiveDocument!;
        public Autodesk.AutoCAD.DatabaseServices.Database Db => Doc.Database;
        public Autodesk.AutoCAD.EditorInput.Editor Ed => Doc.Editor;
        public Type AppType => typeof(Application);
    }
Re-resolved per access → no stale doc handle after the user switches drawings.
</globals>

<imports-and-references>
Imports (`ScriptOptions.WithImports(...)`):
  System, System.Collections.Generic, System.Linq, System.IO, System.Text,
  Autodesk.AutoCAD.ApplicationServices, Autodesk.AutoCAD.DatabaseServices,
  Autodesk.AutoCAD.Geometry, Autodesk.AutoCAD.EditorInput, Autodesk.AutoCAD.Runtime.

References (`ScriptOptions.WithReferences(...)`):
  All `AppDomain.CurrentDomain.GetAssemblies()` that have a non-empty `Location`.
  Built once when the session starts, refreshed on `Reset()`.
  This deliberately gives snippets the same surface the host plugin has.

Allow unsafe: false. Optimization level: Debug (faster compile, better diagnostics).
</imports-and-references>

<execute-flow>
For each call:
    using (var capture = ConsoleCapture.Start())
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _state = _state == null
                ? await CSharpScript.RunAsync<object?>(code, _options, _globals, _ct)
                : await _state.ContinueWithAsync(code, _options, _ct);
            return new ExecuteResult(
                success: true,
                stdout: capture.Stdout,
                stderr: capture.Stderr,
                returnValueRepr: _state.ReturnValue?.ToString(),
                diagnostics: Array.Empty<Diagnostic>(),
                elapsed_ms: sw.ElapsedMilliseconds);
        }
        catch (CompilationErrorException cex)
        {
            return new ExecuteResult(success: false,
                stdout: capture.Stdout,
                stderr: capture.Stderr,
                returnValueRepr: null,
                diagnostics: cex.Diagnostics.Select(MapDiagnostic).ToArray(),
                elapsed_ms: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ExecuteResult(success: false,
                stdout: capture.Stdout,
                stderr: capture.Stderr + "\n" + ex.ToString(),
                returnValueRepr: null,
                diagnostics: Array.Empty<Diagnostic>(),
                elapsed_ms: sw.ElapsedMilliseconds);
        }
    }
</execute-flow>

<reset>
`Reset()`: drop `_state` to null. Roslyn-emitted assemblies from previous calls are then
eligible for collection — but the default ALC is not collectible, so they leak for the
process lifetime. Acceptable; document the leak and recommend AutoCAD restart for very
long sessions. Not worth the complexity of emitting into a separate collectible ALC for v1.

If the user wants a *true* reset (free the script-emitted assembly memory), the path is:
unload the entire Acd.Mcp plugin via DevReload's `MCPUNLOAD` and reload it. We get this
for free from DevReload; no extra code needed.
</reset>
</csharpscript-session>

<dev-vs-production>
<development>
Register Acd.Mcp with DevReload. Workflow:
  1. AutoCAD running, DevReload palette open, Acd.Mcp registered as a plugin.
  2. Edit Acd.Mcp code in VS.
  3. Hit `MCPDEV` (or DevReload palette → "Reload") → DevReload rebuilds + reloads.
  4. Pipe listener restarts on `Initialize`. Bridge client may need to reconnect.
This is the same inner loop the user already has for other plugins.
</development>

<production>
End-user deployment does NOT require DevReload.
Package as an autoload bundle (`.bundle` folder with `PackageContents.xml`) referencing
`Acd.Mcp.dll`. AutoCAD loads it normally on startup. Pipe listener starts in `Initialize`.

Bridge is shipped separately (or alongside): a folder containing `Acd.Mcp.Bridge.exe` +
deps. The user registers it as an MCP server in their MCP client config.
</production>

<reconnect-on-reload>
When the plugin is hot-reloaded by DevReload, the pipe listener of the old instance is
torn down in `Terminate()`. The bridge's open pipe handle goes invalid. The bridge needs
a simple reconnect-on-EOF policy. Single retry with backoff is enough for the dev case.
</reconnect-on-reload>
</dev-vs-production>

<build-slices>
Each slice independently verifiable in real AutoCAD 2025.

<slice number="1" name="Skeleton plugin + ACDMCP_PING command">
  Goal: prove project setup, AutoCAD references, DevReload registration work.
  Deliverable: `Acd.Mcp` plugin with `IExtensionApplication` + `[CommandMethod("ACDMCP_PING")]`
  that writes "pong vN" with a build counter to the editor. No pipe, no Roslyn yet.
  Acceptance:
    1. Register with DevReload. `MCPLOAD`. `ACDMCP_PING` → "pong v1".
    2. Bump counter, `MCPDEV`, `ACDMCP_PING` → "pong v2".
</slice>

<slice number="2" name="CSharpScript session driven by a command">
  Goal: prove Roslyn scripting works against AutoCAD APIs from a vanilla plugin.
  Deliverable: `ScriptSession`, `AcadGlobals`. Temporary `[CommandMethod("ACDMCP_EVAL")]`
  that prompts for a single line and runs it through the session.
  Acceptance:
    1. `ACDMCP_EVAL` → `Ed.WriteMessage("\nhi");` prints "hi".
    2. `ACDMCP_EVAL` → `var x = 1 + 1;` returns null.
    3. `ACDMCP_EVAL` → `x * 10;` returns 20 (state persists).
    4. `ACDMCP_EVAL` → broken code, get diagnostics; no crash.
    5. `ACDMCP_EVAL` → `using (var tr = Db.TransactionManager.StartTransaction()) { ... draw a Circle ... tr.Commit(); }` actually draws.
</slice>

<slice number="3" name="Named pipe listener + main-thread marshaling">
  Goal: snippets run on the right thread under doc lock, over a pipe.
  Deliverable: `PipeListener`, `Protocol`, sync-context capture, `ACDMCP_START` /
  `ACDMCP_STOP` / `ACDMCP_STATUS` commands.
  Acceptance:
    1. From PowerShell or a tiny dotnet harness, send `{"method":"ping"}` → pong with PID.
    2. Send `execute` with code → output visible in AutoCAD, response received.
    3. Send code that calls `Thread.CurrentThread.ManagedThreadId` and inside
       `Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(...)` —
       confirms execution on UI thread.
    4. Send code that throws → structured error response, AutoCAD stable.
    5. Kill the connection mid-request — listener does not crash, accepts next connection.
</slice>

<slice number="4" name="External MCP bridge">
  Goal: Claude Desktop can call `execute_csharp` end-to-end.
  Deliverable: `Acd.Mcp.Bridge` console app, `execute_csharp` MCP tool.
  Acceptance:
    1. Register bridge in `claude_desktop_config.json`.
    2. Ask Claude to "draw a circle at origin radius 5". Tool call lands, circle appears.
    3. Stop AutoCAD. Next tool call returns a clean error (no hang).
    4. Reload Acd.Mcp via `MCPDEV` mid-session. Bridge reconnects on next call.
</slice>

<slice number="5" name="Polish">
  - `reset_session` MCP tool.
  - Diagnostic formatting with file/line/col.
  - Optional `ping` tool for liveness checks.
  - One-page README covering install steps for both dev and prod paths.
</slice>
</build-slices>

<risks-and-open-questions>
1. **Pipe ACL.** Default named-pipe ACL grants access to the current user session only,
   which matches the threat model (a local trusted developer). We set `PipeSecurity`
   explicitly to make it intentional rather than implicit.
2. **Roslyn-emitted assembly leak.** Script-emitted assemblies pile up in the default
   ALC across calls. Mitigation: `Reset()` drops the `ScriptState`; for hard reset, the
   user does `MCPUNLOAD` + `MCPLOAD` in DevReload, which collects everything. Document this.
3. **MCP SDK churn.** `ModelContextProtocol` NuGet is young. Pin the version in
   `Acd.Mcp.Bridge`. Revisit when 1.0 lands.
4. **Multiple AutoCAD instances.** Pipe name includes `{pid}`; Bridge takes `--pid`.
   Auto-discovery works only when there's exactly one `acad.exe`.
5. **Security posture.** Anyone on the pipe runs arbitrary code in AutoCAD. The ACL
   restricts to the same user — same as DevReload's existing pattern.
6. **`x64` only, `net8.0-windows8.0`.** Inherited from DevReload conventions.
7. **AutoCAD reference path.** Same `$(AutoCADPath)` MSBuild property pattern as
   DevReload, with the same `Directory.Build.props` override mechanism. We will copy
   that file into this repo so both repos remain independently buildable.
</risks-and-open-questions>

<questions-for-the-user>
Before I start Slice 1:

1. **DevReload as the development host — OK?**
   The plan assumes you register Acd.Mcp inside DevReload during development. That
   gives you the `MCPLOAD`/`MCPDEV`/`MCPUNLOAD` workflow for free. The plugin itself
   has no DevReload dependency at runtime — production install is a regular AutoCAD
   autoload bundle. Sound right, or do you want Acd.Mcp to be standalone-loadable
   even in dev (e.g. you don't want to require DevReload to iterate)?

2. **Repo location.** ACD-MCP is currently its own repo at this path. Do you want it
   to stay separate, or move under `shtirlitsDva/DevReload/example/` as a third example
   plugin? I'd recommend separate — different audience, different release cadence —
   but flag it.

3. **First tool only `execute_csharp`?**
   The plan ships v1 with one MCP tool. Anything else you want in v1 (e.g. `reset_session`,
   `ping`, `list_loaded_assemblies`)? More tools means more slice-5 polish, not more risk.

4. **Bridge transport.** Stdio is the most-supported MCP transport. Are you targeting a
   client (e.g. an HTTP-only one) that would prefer HTTP loopback instead? If not, stdio.

5. **Production deployment shape.** Two artifacts (plugin bundle + bridge exe), or one
   installer that bundles both and writes the MCP config? I'd start with "two artifacts,
   manual config" and build an installer only if friction warrants it.

Annotate inline where you disagree. If the answers are "yes / separate / just execute_csharp /
stdio / two artifacts, manual", say so and I'll start Slice 1.
</questions-for-the-user>
