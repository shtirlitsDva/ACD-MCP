# ACD-MCP

A Model Context Protocol server that exposes a **live C# script session inside a running AutoCAD process**. An MCP client — Claude Code, Codex, GitHub Copilot, or Claude Desktop — sends C# code; the snippet compiles via Roslyn, runs on AutoCAD's main thread under a document lock, and returns its output — with state persisting between calls. Civil 3D objects are reachable from the same scripting surface.

Effectively: a dotnet command line attached to AutoCAD, driven by an LLM.

## How it works

```
MCP client ─stdio─▶ Acd.Mcp.Bridge.exe ─named pipe─▶ AutoCAD (Acd.Mcp.dll)
                      (MCP server,                       (plugin: pipe listener,
                       translates MCP                      CSharpScript session,
                       ⇄ JSON-RPC)                         main-thread dispatch)
```

* **`Acd.Mcp`** — AutoCAD plugin (`net8.0-windows8.0`, x64). Implements `IExtensionApplication`, hosts a named-pipe server (`acd-mcp-{pid}`), marshals each request onto AutoCAD's UI thread under `doc.LockDocument()`, runs it through a persistent `CSharpScript` session.
* **`Acd.Mcp.Bridge`** — external stdio MCP server (`net8.0`). Translates MCP tool calls into JSON-RPC over the pipe. Auto-discovers the AutoCAD process; takes `--pid <N>` to disambiguate multiple instances.

The plugin and bridge share `Pipe/Protocol.cs` and `ExecuteResult.cs` via linked compile — one source of truth for the wire format.

## Requirements

End-user runtime:

* **AutoCAD 2025 or newer.** The plugin uses .NET 8 collectible `AssemblyLoadContext`; AutoCAD itself supplies the .NET 8 Desktop Runtime the plugin loads into.
* **.NET 8 Runtime** on the same machine — `Acd.Mcp.Bridge.exe` runs out-of-process as a console app (`net8.0`, not `-windows`). Without it the Bridge exits immediately with `framework not found`. Get it from <https://dotnet.microsoft.com/download/dotnet/8.0> (the "Runtime" download, not the SDK).
* **Windows.** Named pipes are Windows-style; AutoCAD is Windows-only anyway.

Build-only:

* **.NET 8 SDK** (or newer).

> AutoCAD is **not** required to build. The plugin's AutoCAD 2025 + Civil 3D 2025 reference
> assemblies come from NuGet (`AutoCAD.NET` 25.0.1 + `Speckle.Civil3D.API` 2025.0.0, both
> `ExcludeAssets="runtime"`). CI on stock GitHub runners builds the full solution.

## Build

```powershell
dotnet build Acd.Mcp.sln -c Debug -p:Platform=x64
```

Build outputs:

* `src/Acd.Mcp/bin/Debug/Acd.Mcp.dll` — load this into AutoCAD.
* `src/Acd.Mcp.Bridge/bin/Debug/Acd.Mcp.Bridge.exe` — register this with your MCP client.

## How the plugin loads (non-standard)

> **Heads-up for AutoCAD plugin developers reading this code.** ACD-MCP does **not** use AutoCAD's stock `ExtensionLoader` + `[CommandClass]` auto-scan path. Two loader choices are supported, both using the same collectible-ALC + removable-commands pattern. If you've never seen this pattern before, the sections below explain why it exists.

### Why a custom loader

The plugin needs two things that the stock loader cannot give:

1. **A collectible `AssemblyLoadContext`** so a fresh build of `Acd.Mcp.dll` can replace the running copy without restarting AutoCAD (hot reload). AutoCAD's stock loader pins the assembly in the default ALC for the lifetime of the process.
2. **Removable commands.** `Utils.AddCommand` registers a `[CommandMethod]` in a way that can be un-registered when the ALC unloads. `CommandClass.AddCommand` (what AutoCAD's auto-scan uses) is permanent — reloading the assembly a second time yields `eDuplicateKey`.

Both supported loaders solve these the same way: byte-load the dll into a collectible ALC, reflect `[CommandMethod]` attributes, and register each through `Utils.AddCommand`. ACD-MCP itself stays loader-agnostic.

### The `NoAutoCommands` trick (DEBUG only)

Under DevReload, the plugin is byte-loaded into a collectible ALC and DevReload's `CommandRegistrar` is responsible for registering commands. We do NOT want AutoCAD's `ExtensionLoader` to also auto-scan the plugin's `[CommandClass]` attributes — that would call the permanent `CommandClass.AddCommand` path and survive the next hot-reload, producing duplicate-key errors.

The defense lives at the top of [`src/Acd.Mcp/McpPlugin.cs`](src/Acd.Mcp/McpPlugin.cs):

```csharp
#if DEBUG
[assembly: CommandClass(typeof(Acd.Mcp.NoAutoCommands))]
#endif

#if DEBUG
public class NoAutoCommands { }
#endif
```

An assembly-level `[CommandClass]` pointing at an empty type tells `ExtensionLoader`: "I've declared my command classes; don't auto-scan the rest of the assembly." The loader then trusts the explicit list (an empty class with zero `[CommandMethod]`s) and doesn't touch the real commands on `McpPlugin`. DevReload's `CommandRegistrar` registers those instead — and the registration can be undone on unload, which is the whole point.

Release builds drop the trick (`#if DEBUG`) because NSLOAD takes the same responsibility through a different mechanism (it byte-loads into its own collectible ALC and reflects the attributes itself, regardless of what `[CommandClass]` declarations the assembly carries).

### Supported loaders

| Loader | Use case | Repository |
|---|---|---|
| **DevReload** | Developer hot-reload loop. `<prefix>LOAD` / `<prefix>UNLOAD` / `<prefix>DEV` commands; iterate on the source and reload without restarting AutoCAD. | <https://github.com/shtirlitsDva/DevReload> |
| **NSLOAD** | End-user release loader. Same collectible-ALC + removable-commands pattern, but driven by a CSV catalogue and a managed loader palette instead of dev iteration. | <https://github.com/shtirlitsDva/Autocad-Civil3d-Tools/tree/master/Acad-C3D-Tools/NSLOAD> |

Pick the one that matches your role:

* **Developing ACD-MCP itself?** Install DevReload, point it at `src/Acd.Mcp/Acd.Mcp.csproj`, pick a command prefix (e.g. `ACDMCP`), and use `ACDMCPLOAD` / `ACDMCPUNLOAD` / `ACDMCPDEV` for the inner loop. See DevReload's README for full instructions. Then run `ACDMCP_START` to open the pipe.
* **Shipping ACD-MCP to end users alongside other in-house plugins?** Register it in NSLOAD's CSV catalogue and let users load it via the NSLOAD palette. This is the path Damgaard's other AutoCAD/Civil 3D tools follow.
* **Just installing ACD-MCP as a standalone end-user?** Use the AutoCAD bundle install path below — `ACD-MCP.bundle` autoloads via AutoCAD's standard mechanism. Release builds work under that path because `NoAutoCommands` is DEBUG-only.

## Install

Three install paths — pick the one that matches your client. All end with the same step: deploy the AutoCAD bundle.

| Your MCP client                | Path                                                              |
|--------------------------------|-------------------------------------------------------------------|
| **Claude Code**                | [Path A — Claude Code](#path-a--claude-code)                      |
| **Codex (app)**                | [Path B — Codex app](#path-b--codex-app)                          |
| **GitHub Copilot / Claude Desktop** | [Path C — Copilot / Claude Desktop](#path-c--copilot--claude-desktop) |

> **`Acd.Mcp.Bridge.exe` needs the .NET 8 Runtime** on the user's machine (see [Requirements](#requirements)). The Bridge exits with `framework not found` if it's missing. AutoCAD's bundled .NET runtime is not enough — Bridge runs out-of-process.

### Path A — Claude Code

Two commands. Then a single PowerShell script for the AutoCAD side.

1. **Add the marketplace and install the plugin** inside any Claude Code session:

   ```
   /plugin marketplace add https://github.com/shtirlitsDva/ACD-MCP
   /plugin install acd-mcp@acd-mcp
   ```

   That registers `Bridge.exe` in Claude Code's MCP roster automatically (via the plugin's `.mcp.json`) and surfaces four skills: `/acd-mcp:start`, `/acd-mcp:script`, `/acd-mcp:batch`, `/acd-mcp:add-dto`.

   > `Bridge.exe` and its .NET 8 dependencies are committed to `bin/` at the repo root. Claude Code's marketplace install only fetches what's in git, so the binary lives there. `scripts/Build-Release.ps1` refreshes `bin/` and reminds the maintainer to commit. Users get it just by `/plugin install` — no separate download.

2. **Deploy the AutoCAD bundle.** The Claude plugin host cannot write into `%APPDATA%\Autodesk\`, so this step is separate. Run once:

   ```powershell
   pwsh ~/.claude/plugins/cache/acd-mcp@acd-mcp/*/install-hooks/Install-Bundle.ps1
   ```

   Copies `ACD-MCP.bundle` into `%APPDATA%\Autodesk\ApplicationPlugins\` (refuses if AutoCAD is running — close it first, or pass `-Force`).

3. Continue with [Inside AutoCAD](#inside-autocad).

> Do **not** run `Install-Mcp.ps1` — it would double-register `acd-mcp` in Claude Code's roster.

### Path B — Codex app

Two clicks in the Codex app's Plugins panel. Then a single PowerShell script for the AutoCAD side.

1. **Add the marketplace and install the plugin** inside the Codex app:

   * Open **Settings → Plugins → Add marketplace**.
   * Paste: `shtirlitsDva/ACD-MCP` and confirm.
   * From the plugin list, install **acd-mcp**.

   That registers `Bridge.exe` in Codex's MCP roster automatically (via the plugin's `.codex-plugin/mcp.json`) and surfaces the same four skills as Claude Code (`start`, `script`, `batch`, `add-dto`).

   > `Bridge.exe` and its .NET 8 dependencies are committed to `bin/` at the repo root — the Codex plugin install fetches them along with the manifest, same as the Claude Code path.

2. **Deploy the AutoCAD bundle.** The Codex plugin host cannot write into `%APPDATA%\Autodesk\`, so this step is separate. Run once:

   ```powershell
   pwsh "$env:USERPROFILE\.codex\plugins\cache\acd-mcp\acd-mcp\*\install-hooks\Install-Bundle.ps1"
   ```

   Copies `ACD-MCP.bundle` into `%APPDATA%\Autodesk\ApplicationPlugins\` (refuses if AutoCAD is running — close it first, or pass `-Force`).

3. Continue with [Inside AutoCAD](#inside-autocad).

> Do **not** run `Install-Mcp.ps1` — it would double-register `acd-mcp` in Codex's roster.

### Path C — Copilot / Claude Desktop

Two independent scripts from the extracted release zip.

1. **Download the latest release zip** from [GitHub Releases](https://github.com/shtirlitsDva/ACD-MCP/releases) (or build it locally — see [Build a release](#build-a-release)).
2. **Extract** the zip somewhere stable, e.g. `C:\Tools\acd-mcp\`.
3. **Deploy the AutoCAD bundle:**

   ```powershell
   pwsh C:\Tools\acd-mcp\install-hooks\Install-Bundle.ps1
   ```

   Copies `ACD-MCP.bundle` into `%APPDATA%\Autodesk\ApplicationPlugins\` (refuses if AutoCAD is running — close it first, or pass `-Force`).

4. **Register the MCP server with each detected client:**

   ```powershell
   pwsh C:\Tools\acd-mcp\install-hooks\Install-Mcp.ps1
   ```

   Auto-detects installed clients and writes the right config in each:

   | Client            | File written                                                 |
   |-------------------|--------------------------------------------------------------|
   | Codex (CLI, VS Code extension, or Desktop app — all share the same file) | `~/.codex/config.toml` — `[mcp_servers.acd-mcp]` |
   | GitHub Copilot    | `%APPDATA%\Code\User\mcp.json` — `servers.acd-mcp`           |
   | Claude Desktop    | `%APPDATA%\Claude\claude_desktop_config.json` — `mcpServers.acd-mcp` |

   Pass `-Clients codex,copilot` to target specific clients (omit Claude Desktop), or `-Clients none` to register nothing (path validation only). Add `-WhatIf` for a true dry run.

   The installer prefers each client's official CLI (`codex mcp add`, `code --add-mcp`) and falls back to direct config-file edits only when the CLI isn't on PATH. Re-runs are idempotent — entries are updated in place, not duplicated.

   > **Note**: Run `Install-Mcp.ps1` from the **extracted release zip**, not from inside a Claude Code plugin cache (`~/.claude/plugins/cache/acd-mcp@*/`). Plugin cache paths change on every plugin update, so any MCP entry registered with a cache path would break on the next update.

5. **Restart your AI client(s)** so they pick up the new MCP server.

### Inside AutoCAD

Common to both paths. After the bundle is in place:

1. Launch AutoCAD 2025+. The bundle autoloads.
2. Run `ACDMCP_START` to open the named pipe.
3. (Optional) Run `ACDMCP_PALETTE` for the dockable SCRIPT + BATCH palette.

### Uninstall

Mirror of install:

| Your install path     | Removal                                                              |
|-----------------------|----------------------------------------------------------------------|
| Path A — Claude Code  | `/plugin uninstall acd-mcp@acd-mcp` + `Uninstall-Bundle.ps1`         |
| Path B — Codex app    | uninstall from Codex's Plugins panel + `Uninstall-Bundle.ps1`        |
| Path C — others       | `Uninstall-Mcp.ps1` + `Uninstall-Bundle.ps1`                         |

```powershell
pwsh install-hooks\Uninstall-Mcp.ps1             # deregister from Copilot / Claude Desktop (Path C only)
pwsh install-hooks\Uninstall-Bundle.ps1          # remove the AutoCAD bundle
pwsh install-hooks\Uninstall-Bundle.ps1 -Purge   # also delete DTOs, saved scripts, batch-run history, log
```

## Build a release

`scripts/Build-Release.ps1` is the release pipeline. It runs locally **and** in CI — AutoCAD/Civil 3D references come from NuGet, no AutoCAD install required.

```powershell
pwsh ./scripts/Build-Release.ps1            # build + assemble + zip → Deploy/acd-mcp-plugin-v<X.Y.Z>.zip
pwsh ./scripts/Build-Release.ps1 -Publish   # same, then gh release create + upload
```

CI (`.github/workflows/ci.yml`):
* every push: builds the full solution (incl. the AutoCAD plugin DLL) and runs all tests
* tag push `v*`: also runs `Build-Release.ps1` and uploads the zip to a GitHub Release

So `git tag v0.2.0 && git push --tags` is sufficient to cut a release — no manual local step.

## Commands inside AutoCAD

| Command | Effect |
|---|---|
| `ACDMCP_PING` | Print version stamp to the editor — sanity check. |
| `ACDMCP_START` | Start the named-pipe listener. |
| `ACDMCP_STOP` | Stop the listener. |
| `ACDMCP_STATUS` | Report listener state, PID, pipe name, session state. |
| `ACDMCP_RESET` | Drop the script session — variables/usings declared so far are gone. |
| `ACDMCP_PALETTE` | Open the dockable palette with two tabs — **SCRIPT** (AvalonEdit + C# highlighting + live execution log, shares the script session with the MCP so `var x = 5` typed in the palette is visible to the LLM's next `autocad_script_execute` call, and vice versa) and **BATCH** (folder + mask + file list + Test/Live slide-switch + Manage Scripts). Wiring the palette also activates the `batch.*` and `script.*` plugin RPC methods (`autocad_batch_*` / `autocad_script_propose` MCP tools require it). |

### Palette / WPF note

The palette uses WPF. Under a custom loader (DevReload or NSLOAD), add these to the plugin's shared-assemblies list so XAML types resolve consistently across the collectible ALC boundary: `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`. (AvalonEdit and CommunityToolkit.Mvvm are loaded only by the plugin and stay in its ALC — no sharing needed.)

The plugin's `SharedAssemblies.Config.json` template ships these defaults plus the AutoCAD-managed types and the V18 contracts split (`Acd.Mcp.Api`, `Acd.Mcp.Contracts`). MSBuild copies the template to `bin/Debug/` per build, so a fresh clone + `dotnet build` is self-sufficient.

### Diagnostic log

Every exception caught at a plugin boundary (commands, pipe handlers, the palette dispatcher, async-task safety nets) is written to:

```
%LOCALAPPDATA%\Acd.Mcp\log.txt
```

Same text is also echoed to AutoCAD's command line and to `System.Diagnostics.Trace` (visible in DebugView). Each entry has timestamp, context tag, and `ex.ToString()` with stack trace. If something inside the plugin misbehaves, this file is the first place to look.

## MCP surface

Five tools, four resources. Tool annotations follow the MCP spec (`ReadOnly` / `Destructive` / `Idempotent` / `OpenWorld`).

### Response shape (batch tools + script-propose tool)

`autocad_batch_propose_script`, `autocad_batch_run_test`, `autocad_batch_get_selection`, and `autocad_script_propose` all return a **discriminated success-shape**. Always check `ok` first:

```
{ ok: true,  ...payload fields..., error_code: null, error_message: null }   # success
{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  ...payload fields all null }                                                # failure
```

The bridge never throws on plugin-rejected failures — those used to be stripped to a generic "An error occurred invoking ..." by the MCP SDK. The plugin's verbatim message now travels on the success path in `error_message`. Typical `ok: false` cases: palette not open, no folder/mask selected, empty editor buffer. See `CRASH_TEST_V2_JOURNAL.md#g4` for the design rationale.

`autocad_script_execute` is the exception — it returns its own structured `ExecuteResult` (`success` / `stdout` / `stderr` / `diagnostics`), not the discriminated shape. Snippet failures already travel inside the success envelope (compile errors → `diagnostics`; runtime exceptions → `stderr`); no thrown path needs wrapping.

### Tools

* **`autocad_script_execute(code, timeout_ms?)`** — *not-read-only, destructive, not-idempotent, open-world.*

  Run C# inside AutoCAD's main thread under a document lock against the active drawing. Globals: `Doc` (active `Document`), `Db` (`Database`), `Ed` (`Editor`), `CivilDoc` (active `CivilDocument` — null in non-Civil drawings), `Acd` (DTO + DataProvider façade). Default imports: `System` + LINQ + IO + Text plus `Autodesk.AutoCAD.{ApplicationServices,DatabaseServices,Geometry,EditorInput,Runtime}`. `Autodesk.Civil.*` is **NOT** in the defaults — it defines its own `Entity` which would collide; add `using Autodesk.Civil.DatabaseServices;` at the top of a submission when needed. Top-level declarations persist between calls. Returns an `ExecuteResult` with `success`, `return_value_repr`, `return_value_json` (DTO-projected), `diagnostics`, `stdout`, `stderr`, `elapsed_ms`.

* **`autocad_batch_propose_script(name, script_body, input_summary?)`** — *not-read-only, not-destructive, idempotent, open-world.*

  Save a batch-flavour C# script to `%APPDATA%\Acd.Mcp\scripts\batch\<name>.csx` and push it into the BATCH palette's live editor. The agent should read `%LOCALAPPDATA%\Acd.Mcp\buffer-batch.csx` first so it doesn't trample in-progress user edits. Success payload: `{ saved_as, name, replaced_dirty }`. See `/acd-mcp:batch`.

* **`autocad_batch_run_test(name?)`** — *read-only, not-destructive, not-idempotent, open-world.*

  Kick off a TEST-mode run against the BATCH palette's currently-selected folder + mask. No-arg form runs the live editor buffer; with `name`, loads that saved script first. Test mode opens each drawing read-shared, runs inside a transaction, then rolls back — nothing on disk changes. Success payload: `{ run_id, pending, results_resource, note }`. **There is no `autocad_batch_run_live`** — Live mode requires the user to flip the slide-switch and click Run in person.

* **`autocad_batch_get_selection()`** — *read-only, not-destructive, idempotent, open-world.*

  Return what TEST would operate on right now. Success payload: `{ folder, mask, recurse, files: [...], count }`. The agent cannot change these — only the user can, via the palette.

* **`autocad_script_propose(name, script_body, input_summary?)`** — *not-read-only, not-destructive, idempotent, open-world.*

  Save a single-drawing C# script to `%APPDATA%\Acd.Mcp\scripts\script\<name>.csx` and stage it in the SCRIPT palette tab's editor for the user to review/edit before running. Same response shape as the batch propose tool: `{ saved_as, name, replaced_dirty }`. On a clean editor the proposal inline-promotes (`replaced_dirty: false`); on a dirty editor the user is prompted asynchronously (`replaced_dirty: true`) — see `/acd-mcp:script` for the full workflow. The agent should read `%LOCALAPPDATA%\Acd.Mcp\buffer-script.csx` before calling to capture any user hand-edits.

### Resources

* **`acd-mcp://batch-runs/recent{?limit,offset}`** — paginated newest-first list of completed batch runs. Default `limit=20`, max 100. Each entry has run id, timestamps, mode, pass/fail counts, cancellation flag.
* **`acd-mcp://batch-runs/{run_id}`** — full per-file result of a specific run. Step-level outcomes (which `Require` predicates passed, which `Apply` summaries ran, which exceptions were caught), elapsed timings, cancellation status.
* **`acd-mcp://batch-runs/last`** — convenience alias for the most recent run.
* **`acd-mcp://dto-system/diagnostics`** — live list of every DTO file in `dto-system/` or `dto-user/` that failed to compile. Each entry: source, header type, resolved type, message, line, column, error code. The `/acd-mcp:add-dto` skill points the agent here.

## Architecture & limitations

See [`docs/design/architecture.md`](docs/design/architecture.md) for the full design rationale. Key things to know:

* **The snippet blocks AutoCAD's main thread for its duration.** Same as any `[CommandMethod]`. A cooperative `timeout_ms` cancels at the next `CancellationToken` check; a snippet that spins without observing it cannot be interrupted without killing AutoCAD.
* **No sandbox.** Arbitrary C# inside the AutoCAD process means full process privileges. Treat this as a trusted developer tool. The named pipe's default ACL restricts access to the current user session.
* **Roslyn-emitted assemblies accumulate.** Each `autocad_script_execute` call emits a fresh script assembly into the default `AssemblyLoadContext` (which is not collectible). `ACDMCP_RESET` drops the script state; an AutoCAD restart frees the assembly memory.
* **Modal AutoCAD dialogs deadlock the pipe.** If AutoCAD is showing a modal (plot preview, etc.), the message pump is busy and main-thread marshaling waits until the modal closes. Use `timeout_ms` to surface this.

## License

TBD.
