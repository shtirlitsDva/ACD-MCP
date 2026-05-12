# ACD-MCP

A Model Context Protocol server that exposes a **live C# REPL inside a running AutoCAD process**. An MCP client (Claude Desktop, etc.) sends C# code; the snippet compiles via Roslyn, runs on AutoCAD's main thread under a document lock, and returns its output — with state persisting between calls.

Effectively: a dotnet command line attached to AutoCAD, driven by an LLM.

## How it works

```
Claude Desktop ─stdio─▶ Acd.Mcp.Bridge.exe ─named pipe─▶ AutoCAD (Acd.Mcp.dll)
                          (MCP server,                       (plugin: pipe listener,
                           translates MCP                      CSharpScript session,
                           ⇄ JSON-RPC)                         main-thread dispatch)
```

* **`Acd.Mcp`** — AutoCAD plugin (`net8.0-windows8.0`, x64). Implements `IExtensionApplication`, hosts a named-pipe server (`acd-mcp-{pid}`), marshals each request onto AutoCAD's UI thread under `doc.LockDocument()`, runs it through a persistent `CSharpScript` session.
* **`Acd.Mcp.Bridge`** — external stdio MCP server (`net8.0`). Translates MCP tool calls into JSON-RPC over the pipe. Auto-discovers the AutoCAD process; takes `--pid <N>` to disambiguate multiple instances.

The plugin and bridge share `Pipe/Protocol.cs` and `ExecuteResult.cs` via linked compile — one source of truth for the wire format.

## Requirements

* AutoCAD 2025 or newer (the plugin uses .NET 8 collectible `AssemblyLoadContext`).
* .NET 8 SDK (or newer) to build.
* Windows. Named pipes are Windows-style; the plugin and AutoCAD are Windows-only anyway.

## Build

```powershell
dotnet build Acd.Mcp.sln -c Debug -p:Platform=x64
```

If your AutoCAD is not at `C:\Program Files\Autodesk\AutoCAD 2025`, override the path:

```powershell
copy Directory.Build.props.user.example Directory.Build.props.user
# edit Directory.Build.props.user, set <AutoCADPath>
```

Build outputs:

* `src/Acd.Mcp/bin/Debug/Acd.Mcp.dll` — load this into AutoCAD.
* `src/Acd.Mcp.Bridge/bin/Debug/Acd.Mcp.Bridge.exe` — register this with your MCP client.

## Run — development (with DevReload)

Recommended inner loop. Requires [DevReload](https://github.com/shtirlitsDva/DevReload) installed in AutoCAD.

1. In AutoCAD, run `DEVRELOAD` to open the management palette.
2. Add a plugin pointing at `src/Acd.Mcp/Acd.Mcp.csproj`. Pick a command prefix, e.g. `MCP`.
3. Run `MCPLOAD` (or your-prefix + `LOAD`).
4. Run `ACDMCP_START` to start the pipe listener.
5. Iterate on Acd.Mcp; `MCPDEV` rebuilds and hot-reloads. The pipe restarts; the bridge reconnects automatically on its next call.

## Run — production (no DevReload)

1. Package `Acd.Mcp.dll` + its dependencies as an AutoCAD autoload bundle (`*.bundle` folder with `PackageContents.xml`), or NETLOAD manually.
2. Inside AutoCAD, run `ACDMCP_START` to start the listener. (Optionally autoload this command via your bundle.)
3. Register the bridge with your MCP client. For Claude Desktop, add to `%APPDATA%\Claude\claude_desktop_config.json`:

   ```json
   {
     "mcpServers": {
       "autocad": {
         "command": "C:\\path\\to\\Acd.Mcp.Bridge.exe"
       }
     }
   }
   ```

   Pass `"args": ["--pid", "12345"]` if you run multiple AutoCAD instances.

## Commands inside AutoCAD

| Command | Effect |
|---|---|
| `ACDMCP_PING` | Print version stamp to the editor — sanity check. |
| `ACDMCP_START` | Start the named-pipe listener. |
| `ACDMCP_STOP` | Stop the listener. |
| `ACDMCP_STATUS` | Report listener state, PID, pipe name, session state. |
| `ACDMCP_RESET` | Drop the script session — variables/usings declared so far are gone. |
| `ACDMCP_PALETTE` | Open the dockable REPL palette (AvalonEdit + C# highlighting + live execution log). Shares the script session with the MCP, so `var x = 5` typed in the palette is visible to the LLM's next `autocad_execute_csharp` call, and vice versa. |

### Palette / WPF note

The palette uses WPF. Under DevReload, add these to the plugin's shared-assemblies list so XAML types resolve consistently across the collectible ALC boundary: `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`. (AvalonEdit and CommunityToolkit.Mvvm are loaded only by the plugin and stay in its ALC — no sharing needed.)

### Diagnostic log

Every exception caught at a plugin boundary (commands, pipe handlers, the palette dispatcher, async-task safety nets) is written to:

```
%LOCALAPPDATA%\Acd.Mcp\log.txt
```

Same text is also echoed to AutoCAD's command line and to `System.Diagnostics.Trace` (visible in DebugView). Each entry has timestamp, context tag, and `ex.ToString()` with stack trace. If something inside the plugin misbehaves, this file is the first place to look.

## MCP tools exposed

* **`autocad_execute_csharp(code, timeout_ms?)`** — run C# inside AutoCAD. Annotated as not-read-only, destructive, not-idempotent, open-world.

  Globals available in scope: `Doc` (active `Document`), `Db` (its `Database`), `Ed` (its `Editor`). The full `Autodesk.AutoCAD.*` namespaces are imported. Variables declared at top level persist into the next call.

  Returns an `ExecuteResult` object with `success`, `returnValueRepr`, `diagnostics` (compile errors with file/line/col), `stdout`, `stderr`, `elapsedMs`.

## Architecture & limitations

See [`docs/design/architecture.md`](docs/design/architecture.md) for the full design rationale. Key things to know:

* **The snippet blocks AutoCAD's main thread for its duration.** Same as any `[CommandMethod]`. A cooperative `timeout_ms` cancels at the next `CancellationToken` check; a snippet that spins without observing it cannot be interrupted without killing AutoCAD.
* **No sandbox.** Arbitrary C# inside the AutoCAD process means full process privileges. Treat this as a trusted developer tool. The named pipe's default ACL restricts access to the current user session.
* **Roslyn-emitted assemblies accumulate.** Each `autocad_execute_csharp` call emits a fresh script assembly into the default `AssemblyLoadContext` (which is not collectible). `ACDMCP_RESET` drops the script state; an AutoCAD restart frees the assembly memory.
* **Modal AutoCAD dialogs deadlock the pipe.** If AutoCAD is showing a modal (plot preview, etc.), the message pump is busy and main-thread marshaling waits until the modal closes. Use `timeout_ms` to surface this.

## License

TBD.
