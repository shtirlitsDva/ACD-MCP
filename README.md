# ACD-MCP

An MCP server that runs C# inside a live AutoCAD / Civil 3D process. The client sends C# code; it compiles with Roslyn, runs on AutoCAD's main thread under a document lock, and returns the result. State persists between calls.

## How it works

```
MCP client ─stdio─▶ Acd.Mcp.Bridge.exe ─named pipe─▶ AutoCAD (Acd.Mcp.dll)
```

* **`Acd.Mcp`** — AutoCAD plugin (`net8.0-windows8.0`, x64). Hosts the pipe server (`acd-mcp-{pid}`) and runs each request on the UI thread under `LockDocument()` through a persistent `CSharpScript` session.
* **`Acd.Mcp.Bridge`** — stdio MCP server (`net8.0`). Translates MCP calls to JSON-RPC over the pipe. Auto-discovers AutoCAD; pass `--pid <N>` to disambiguate multiple instances.

## Requirements

* **AutoCAD 2025+** (supplies the .NET 8 runtime the plugin loads into).
* **[.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** — the Bridge runs out-of-process and needs it (else `framework not found`).
* **Windows.**
* To build: **.NET 8 SDK**. AutoCAD is not required — references come from NuGet.

## Build

```powershell
dotnet build Acd.Mcp.sln -c Release -p:Platform=x64
```

Outputs: `src/Acd.Mcp/bin/Release/Acd.Mcp.dll` (load into AutoCAD) and `src/Acd.Mcp.Bridge/bin/Release/Acd.Mcp.Bridge.exe` (register with your MCP client).

## How the plugin loads

Two build flavours, switched by one compiler symbol (`ISOLATED_ALC`, off by default):

| Flavour | Build with | Load with |
|---|---|---|
| **Netload** *(default)* | `dotnet build -c Release` | `NETLOAD`, or the `.bundle` autoload |
| **IsolatedALC** | `-p:IsolatedAlc=true` (or the `FolderProfile` publish profile) | DevReload / NSLOAD |

The marker is an empty `[assembly: CommandClass(NoAutoCommands)]` that stops AutoCAD's `ExtensionLoader` from auto-registering commands. DevReload/NSLOAD byte-load the DLL and register commands themselves via the removable `Utils.AddCommand`, so the marker must be **present** there or the second reload throws `eDuplicateKey`. With `NETLOAD`/`.bundle`, AutoCAD is the only registrar, so it must be **absent** or no commands appear. Gated in `McpPlugin.cs`:

```csharp
#if ISOLATED_ALC
[assembly: CommandClass(typeof(Acd.Mcp.NoAutoCommands))]
public class NoAutoCommands { }
#endif
```

`Acd.Mcp.csproj` defines the symbol from a `Target` (not a `PropertyGroup`) so it still applies when set by the `FolderProfile` publish profile, which is imported after the project body.

### NETLOAD (default)

1. `dotnet build Acd.Mcp.sln -c Release -p:Platform=x64`
2. In AutoCAD: `NETLOAD` → `src/Acd.Mcp/bin/Release/Acd.Mcp.dll`
3. `ACDMCP_PING` to verify (the pipe auto-starts on first idle).

### DevReload / NSLOAD

Build with `-p:IsolatedAlc=true`. [DevReload](https://github.com/shtirlitsDva/DevReload): point it at `src/Acd.Mcp/Acd.Mcp.csproj`. [NSLOAD](https://github.com/shtirlitsDva/Autocad-Civil3d-Tools/tree/master/Acad-C3D-Tools/NSLOAD): publish the `FolderProfile` profile (sets `IsolatedAlc=true`, drops the DLL in the catalogue), then load it from the palette.

## Install

The MCP-client side (register `Bridge.exe`) and the AutoCAD side (load the plugin) are separate steps.

### Claude Code

```
/plugin marketplace add https://github.com/shtirlitsDva/ACD-MCP
/plugin install acd-mcp@acd-mcp
```

Registers `Bridge.exe` and adds skills `/acd-mcp:start|script|batch|add-dto`. Then deploy the bundle:

```powershell
pwsh ~/.claude/plugins/cache/acd-mcp@acd-mcp/*/install-hooks/Install-Bundle.ps1
```

Don't run `Install-Mcp.ps1` here — it double-registers.

### Codex app

Settings → Plugins → Add marketplace → `shtirlitsDva/ACD-MCP` → install **acd-mcp**. Then:

```powershell
pwsh "$env:USERPROFILE\.codex\plugins\cache\acd-mcp\acd-mcp\*\install-hooks\Install-Bundle.ps1"
```

Don't run `Install-Mcp.ps1` here — it double-registers.

### Copilot / Claude Desktop

Download a [release zip](https://github.com/shtirlitsDva/ACD-MCP/releases), extract, then:

```powershell
pwsh install-hooks\Install-Bundle.ps1   # deploy the AutoCAD bundle
pwsh install-hooks\Install-Mcp.ps1      # register with detected clients
```

`Install-Mcp.ps1` writes `~/.codex/config.toml`, `%APPDATA%\Code\User\mcp.json`, or `%APPDATA%\Claude\claude_desktop_config.json`. Flags: `-Clients codex,copilot`, `-WhatIf`. Restart the client afterward.

### Inside AutoCAD

Launch AutoCAD 2025+. The bundle autoloads and auto-starts the pipe. Run `ACDMCP_PALETTE` for the SCRIPT + BATCH palette (propose calls auto-open it). To disable auto-start: create `%LOCALAPPDATA%\Acd.Mcp\config.json` with `{ "auto_start": false }`.

### Uninstall

```powershell
pwsh install-hooks\Uninstall-Mcp.ps1            # Copilot / Claude Desktop only
pwsh install-hooks\Uninstall-Bundle.ps1         # remove the bundle
pwsh install-hooks\Uninstall-Bundle.ps1 -Purge  # also delete DTOs, scripts, history, log
```

Claude Code: `/plugin uninstall acd-mcp@acd-mcp`. Codex app: uninstall from the Plugins panel.

## Build a release

```powershell
pwsh ./scripts/Build-Release.ps1            # → Deploy/acd-mcp-plugin-v<X.Y.Z>.zip
pwsh ./scripts/Build-Release.ps1 -Publish   # also gh release create + upload
```

CI builds + tests on every push; a `v*` tag also builds the release and uploads the zip. So `git tag vX.Y.Z && git push --tags` cuts a release.

## Commands

| Command | Effect |
|---|---|
| `ACDMCP_PING` | Version stamp — sanity check. |
| `ACDMCP_START` / `ACDMCP_STOP` | Start / stop the pipe listener. |
| `ACDMCP_STATUS` | Listener state, PID, pipe name, session state. |
| `ACDMCP_RESET` | Drop the script session (declared variables/usings gone). |
| `ACDMCP_PALETTE` | Open the SCRIPT + BATCH palette. |

The palette shares its script session with the MCP, so a `var` typed in the palette is visible to the next `autocad_script_execute`, and vice versa. Diagnostic log: `%LOCALAPPDATA%\Acd.Mcp\log.txt`.

## MCP surface

Five tools, four resources.

### Tools

* **`autocad_script_execute(code, timeout_ms?)`** — run C# on the main thread under a doc lock. Globals: `Doc`, `Db`, `Ed`, `CivilDoc` (null in non-Civil drawings), `Acd`. Default imports cover `System`, LINQ, IO, Text and `Autodesk.AutoCAD.*`; add `using Autodesk.Civil.DatabaseServices;` yourself when needed. Declarations persist. Returns `ExecuteResult` (`success`, `return_value_repr`, `return_value_json`, `diagnostics`, `stdout`, `stderr`, `elapsed_ms`).
* **`autocad_script_propose(name, body, input_summary?)`** — stage a single-drawing script in the SCRIPT palette for review.
* **`autocad_batch_propose_script(name, body, input_summary?)`** — save + load a batch script into the BATCH palette.
* **`autocad_batch_run_test(name?)`** — TEST-run the batch script over the selected folder/mask; opens each drawing read-shared and rolls back. There is no live-run tool — the user runs Live in person.
* **`autocad_batch_get_selection()`** — return the current folder, mask, recurse flag, and file list.

All tools except `autocad_script_execute` return a discriminated shape — check `ok` first; on failure read `error_message`. `autocad_script_execute` returns `ExecuteResult` directly (compile errors in `diagnostics`, runtime errors in `stderr`).

### Resources

* `acd-mcp://batch-runs/recent{?limit,offset}` — completed runs, newest first.
* `acd-mcp://batch-runs/{run_id}` — per-file result of one run.
* `acd-mcp://batch-runs/last` — most recent run.
* `acd-mcp://dto-system/diagnostics` — DTO files that failed to compile.

## Limitations

* The snippet blocks AutoCAD's main thread. `timeout_ms` cancels at the next `CancellationToken` check; a spin loop can't be interrupted without killing AutoCAD.
* No sandbox — arbitrary C# runs in-process. Trusted-developer tool. The pipe's ACL restricts access to the current user.
* Roslyn-emitted assemblies accumulate; `ACDMCP_RESET` drops session state, an AutoCAD restart frees the memory.
* Modal AutoCAD dialogs block the pipe until closed.

See [`docs/design/architecture.md`](docs/design/architecture.md) for design rationale.

## License

TBD.
