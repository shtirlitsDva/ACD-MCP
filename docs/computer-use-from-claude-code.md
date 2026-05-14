<computer-use-from-claude-code>

<purpose>
Recipe for letting Claude Code drive AutoCAD / Civil 3D autonomously — no human at the keyboard, no pixel-clicking, no MCP-server install. Discovered during the v2 crash-test session (2026-05-13). 10/10 success rate, ~14 ms per cycle.

The intent is to capture **how** so it survives this conversation and can be re-used by a sibling test app that drives Civil 3D the same way.
</purpose>

<the-core-trick>
**Don't push pixels. Reflect into the plugin's own object graph.**

The ACD-MCP plugin already exposes an in-process C# REPL via `autocad_execute_csharp`. The REPL runs on AutoCAD's main thread under `Doc.LockDocument()`. From inside that REPL, you can reach every singleton the plugin owns — palettes, view-models, executors, RPC handlers — via reflection through the plugin's static fields. Every `[RelayCommand]` method on those view-models is therefore a button the agent can press.

This is not "computer use" in the Anthropic sense; it's better, because it's deterministic, sub-20 ms, and survives DPI / theme / ribbon-collapse changes.

The trade-off: this only works for plugins **you own the source of**. For third-party UI surfaces (Autodesk's own dialogs, ribbon, license popups), you still need UI Automation (see `<powershell-uia-fallback>`).
</the-core-trick>

<the-static-field-handle>
The entry point is `Acd.Mcp.McpPlugin`'s static fields. Reach them with `BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Instance`. Important ones as of the May-2026 build:

| Field | Type | What it gives you |
|---|---|---|
| `_palette` | `ReplPaletteSet` | The host PaletteSet; entry to both REPL and BATCH controls. |
| `_executor` | `AcadExecutor` | The pipe-side REPL executor. |
| `_batchExecutor` | `BatchExecutor` | The batch runtime. Owns `_scriptHost`, `_runner`, `RunCompleted` / `FileCompleted` events. |
| `_batchRpc` | `BatchRpcHandler` | Holds `_uiState` — the live `BatchViewModel` you want for AFK BATCH testing. |
| `_session` | `ScriptSession` | The REPL's Roslyn session. |
| `_dtoRegistry` | `DtoRegistry` | Registered DTO projections. |

The cleanest BATCH-VM handle is `McpPlugin._batchRpc._uiState`. It's durable across palette tab-switches and visual-tree de-realization (which is why the WPF visual-tree walk failed mid-session).

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Acd.Mcp");
var pluginT = asm.GetType("Acd.Mcp.McpPlugin");
var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
       | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

var rpc = pluginT.GetField("_batchRpc", bf).GetValue(null);
var vm  = rpc.GetType().GetField("_uiState", bf).GetValue(rpc);
var vmT = vm.GetType();          // Acd.Mcp.Batch.Ui.BatchViewModel
```

`BatchViewModel` exposes (writable): `Folder`, `Mask`, `Recurse`, `LiveSelected`. (read-only): `Files`, `Results`, `MatchedSummary`, `StatusLine`, `IsRunning`. (commands): `RefreshCommand`, `BrowseCommand`, `RunCommand`, `CancelCommand`, `ManageScriptsCommand`.
</the-static-field-handle>

<driving-the-batch-palette>
End-to-end recipe for an AFK Test run, single REPL submission:

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Acd.Mcp");
var pluginT = asm.GetType("Acd.Mcp.McpPlugin");
var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
       | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
var rpc = pluginT.GetField("_batchRpc", bf).GetValue(null);
var vm  = rpc.GetType().GetField("_uiState", bf).GetValue(rpc);
var vmT = vm.GetType();
var executor = pluginT.GetField("_batchExecutor", bf).GetValue(null);
var execT = executor.GetType();

// 1. Set palette state.
vmT.GetProperty("Folder").SetValue(vm, @"X:\path\to\drawings");
vmT.GetProperty("Mask").SetValue(vm, "*.dwg");
vmT.GetProperty("Recurse").SetValue(vm, false);
vmT.GetProperty("LiveSelected").SetValue(vm, false);  // Test mode

// 2. Refresh files (use the IRelayCommand — Refresh() is private).
var refreshCmd = vmT.GetProperty("RefreshCommand").GetValue(vm);
refreshCmd.GetType().GetMethod("Execute", new[] { typeof(object) })
          .Invoke(refreshCmd, new object[] { null });

// 3. Subscribe to RunCompleted BEFORE invoking Run — events fire on a worker thread.
var done = new System.Threading.ManualResetEventSlim(false);
object lastReport = null;
var evt = execT.GetEvent("RunCompleted");
var handler = new System.Action<object, object>((s, e) => { lastReport = e; done.Set(); });
var del = System.Delegate.CreateDelegate(evt.EventHandlerType, handler.Target, handler.Method);
evt.AddEventHandler(executor, del);

try
{
    // 4. Click Run.
    var runCmd = vmT.GetProperty("RunCommand").GetValue(vm);
    runCmd.GetType().GetMethod("Execute", new[] { typeof(object) })
          .Invoke(runCmd, new object[] { null });

    // 5. Wait. Test runs of small folders take seconds; allow generous headroom.
    if (!done.Wait(TimeSpan.FromSeconds(60))) return "timeout";
}
finally { evt.RemoveEventHandler(executor, del); }

// 6. Pull structured report.
var rT = lastReport.GetType();
var results = ((System.Collections.IEnumerable)rT.GetProperty("Results").GetValue(lastReport))
              .Cast<object>().ToList();
return new {
    aborted = rT.GetProperty("AbortedReason").GetValue(lastReport),
    pass = results.Count(r => r.GetType().GetProperty("Status").GetValue(r)?.ToString() == "Pass"),
    fail = results.Count(r => r.GetType().GetProperty("Status").GetValue(r)?.ToString() != "Pass"),
};
```

Observed performance: ~14 ms per Test cycle when the script-compile cache is warm, ~10 s on the first call (compile + executor warm-up).
</driving-the-batch-palette>

<gotchas>

<reflection-bindingflags>
**Private members need `BindingFlags.NonPublic`.** `BatchViewModel.Refresh()` is private — `GetMethod("Refresh")` with default flags returns null. Use the public `RefreshCommand.Execute(null)` instead, or pass the NonPublic flag. Half a session was lost to NRE-from-null-MethodInfo on this.
</reflection-bindingflags>

<wpf-visual-tree-de-realization>
The visual tree only contains the *currently active* tab's WPF content. Walking `HwndSource.RootVisual` to find `BatchViewModel` works while the BATCH tab is selected, then **stops working** when the user (or anything else) switches to REPL. Always reach the VM through `McpPlugin._batchRpc._uiState`, not through `VisualTreeHelper`. Same applies if the palette is auto-hidden / collapsed.
</wpf-visual-tree-de-realization>

<paletteset-tabs-opaque-to-uia>
Autodesk's `PaletteSet` renders its tab strip as owner-drawn Win32 controls that **don't appear in the UI Automation tree**. PowerShell UIA can read the AutoCAD MDI frame and the palette's hosted content (Pane + Button + Edit), but it cannot see or click the "REPL / BATCH" tab headers. If you need to programmatically switch tabs, call `PaletteSet.Activate(tabIndex)` or its WPF moral equivalent via reflection — NOT UIA.
</paletteset-tabs-opaque-to-uia>

<exception-ambiguity-in-repl>
`Autodesk.AutoCAD.Runtime.Exception` is in the REPL's default imports, so unqualified `Exception` is ambiguous against `System.Exception` at compile time. Use `System.Exception` explicitly in `catch` clauses inside REPL submissions.
</exception-ambiguity-in-repl>

<alc-mismatch-when-bridging-to-isolated-types>
The plugin runs in a DevReload `IsolatedPluginContext` ALC, but the REPL globals (`Doc`, `Db`, etc.) live in `Acd.Mcp.Api` which is in the **Default** ALC. When you cast a result from a plugin-internal call back through the REPL, watch for `InvalidCastException: [A]T cannot be cast to [B]T`. Workarounds:
- Treat cross-ALC values as `object` and access them via reflection (member names, not typed properties).
- For collections, iterate as `IEnumerable` — don't unbox to `IList<TypedThing>`.
- For events, define the handler as `Action<object, object>` and let `Delegate.CreateDelegate` adapt — never declare the handler with the typed `EventArgs`.
</alc-mismatch-when-bridging-to-isolated-types>

<wait-for-completion-via-events>
`RunCommand.Execute(null)` returns immediately — the actual run is `async` on a background thread. To wait, subscribe to `BatchExecutor.RunCompleted` BEFORE invoking the command. The event delivers a `BatchRunReport` carrying `Files`, `Results`, `AbortedReason`, etc. The agent must `RemoveEventHandler` in a `finally` so successive cycles don't accumulate handlers.

Polling `BatchViewModel.IsRunning` is fragile — the property flips on the dispatcher thread and there's no `IsRunningChanged` event surface for the agent.
</wait-for-completion-via-events>

</gotchas>

<powershell-uia-fallback>
For surfaces NOT owned by the plugin — Autodesk's File-Open dialog, license popups, fatal-error message boxes, modal "Drawing recovery" prompts — use PowerShell with the built-in `UIAutomationClient` assembly. No NuGet, no MCP-server install.

```powershell
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Find AutoCAD's main window. acad.exe is a single-process WIN32 app
# (NOT a UWP shim like Notepad on Win11).
$acad = Get-Process | Where-Object {
    $_.ProcessName -match '^(acad|accore)$' -and $_.MainWindowHandle -ne 0
} | Select-Object -First 1

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$acad.Id)
$mainWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# Find a button by exact Name and invoke it. Prefer AutomationId where the
# control exposes one; AutoCAD ribbons tend to expose Name but not AutomationId.
$btnCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "OK")))
$btn = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
```

**Use `Process.MainWindowTitle` only as a last resort** — Win11 UWP apps (Notepad, Calculator) run under a stub PID; the visible window belongs to a different PID. AutoCAD itself is fine; its PID matches its window.
</powershell-uia-fallback>

<bridge-side-iteration>
The `<reload-the-plugin-procedure>` below covers reloading the **plugin** inside AutoCAD (DevReload's collectible ALC, `Acd.Mcp.dll`). It does NOT cover the **bridge** — `Acd.Mcp.Bridge.exe` is a separate process that Claude Code's MCP transport spawns, talks to over stdio, and resolves from its plugin cache (`~/.claude/plugins/cache/acd-mcp/.../bin/`). Bridge-side iteration has its own gotchas:

<gotcha id="cache-vs-bin">
**The running bridge runs from the cache, not from `bin/` at repo root.** `.mcp.json`'s `${CLAUDE_PLUGIN_ROOT}` resolves to the cache; the cache is a snapshot taken at `/plugin install` time. A fresh `dotnet publish` to the repo's `bin/` does NOT update the running bridge. To activate a rebuilt bridge:

```powershell
pwsh scripts/Refresh-PluginCache.ps1 -Publish   # publish + copy bin/ -> cache/
# then in Claude Code:
/reload-plugins
```

`Refresh-PluginCache.ps1` handles the copy across all cache version subdirs. The `-Publish` flag chains the `dotnet publish` step so a single command refreshes the iteration loop. See `<v3-h2>` in `CRASH_TEST_V3_JOURNAL.md` for the original finding.
</gotcha>

<gotcha id="killing-bridge-disconnects-session">
**Killing `Acd.Mcp.Bridge.exe` from outside permanently disconnects the in-session Claude Code MCP server.** The MCP harness does NOT auto-respawn the bridge on a tool-call attempt after the child process dies; the four `mcp__plugin_acd-mcp_acd-mcp__*` tools are silently removed from the available-tool list and stay gone until the user runs `/reload-plugins`. An agentic loop cannot self-trigger that command.

**Symptom:** `ToolSearch` returns "no longer available (their MCP server disconnected)" for the acd-mcp tools after the bridge process exits, and subsequent calls fail with `No such tool available`.

**Workarounds, in order of preference:**

1. **Don't kill the bridge.** Refresh the cache (`Refresh-PluginCache.ps1`) and call `/reload-plugins` instead — the harness will gracefully tear down + respawn.

2. **For an AFK / agentic loop, drive the plugin via the direct pipe.** The plugin's named pipe (`\\.\pipe\acd-mcp-<PID>`) is independent of the bridge process — Civil 3D can stay running with the pipe open across bridge restarts. `scripts/Invoke-AcdMcpPipe.ps1` is a one-call PowerShell helper that speaks the wire format directly:

   ```powershell
   $pid = Get-Process acad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -ExpandProperty Id -First 1
   pwsh scripts/Invoke-AcdMcpPipe.ps1 -AcadPid $pid -Method 'execute' `
        -Params @{ code = 'return Doc.Name;' }
   ```

   Same surface as the MCP tools (`execute`, `batch.proposeScript`, `batch.runTest`, `batch.getSelection`, `repl.proposeScript`, etc.) but without the MCP-server middleman. Note that the discriminated-error shape from the BatchPropose / BatchRunTest / BatchGetSelection tools (`<v3-g4>`) is added by the BRIDGE — calling the pipe directly returns the raw plugin response (`result` on success, `error` envelope on failure).

3. **Future structural fix (not in this repo):** Claude Code's MCP transport could auto-respawn a child process on observed disconnect. See `<v3-h1>` in the v3 crash-test journal — that's where the bug report against Anthropic lives if you want to chase it.
</gotcha>
</bridge-side-iteration>

<reload-the-plugin-procedure>
DevReload hot-swaps the plugin's isolated ALC. The procedure for an iteration loop:

1. **Edit the source file.**
2. **Build.** From the repo root:
   ```
   dotnet build src\Acd.Mcp\Acd.Mcp.csproj -c Debug
   ```
   This refreshes `bin\Debug\Acd.Mcp.dll` (and `.Batch.dll`, `.Api.dll`).
3. **Close the ACD-MCP palette** before reloading. The palette holds a static `_palette` reference plus a Win32 `Window.Handle`; reloading without closing was reported to leak/wedge. Two ways to close from inside the REPL just before reload:
   - Run the AutoCAD command `ACDMCP_PALETTE` to toggle it shut, OR
   - Call `McpPlugin._palette.Close()` reflectively.
4. **Trigger DevReload's reload via the per-plugin commands.** DevReload registers `<commandPrefix>UNLOAD` and `<commandPrefix>LOAD` for every plugin in `%APPDATA%\DevReload\plugins.json`. For ACD-MCP (`commandPrefix: ACDMCP`), that's `ACDMCPUNLOAD` then `ACDMCPLOAD`. The unload tears down the collectible ALC; the load streams the fresh bytes back in. **Caveat from this session:** even after `ACDMCPUNLOAD`, AutoCAD held a `FileShare.Read` lock on `Acd.Mcp.dll` long enough to fail an immediate `dotnet build`. Workarounds: `mv Acd.Mcp.dll Acd.Mcp.dll.old; dotnet build` (let the new build write a fresh file), or kill AutoCAD entirely if the loop is short.
5. **Re-run `ACDMCP_START`** to wake the pipe listener. First call after a fresh ALC takes a few seconds (Roslyn warm-up, DTO graph rebuild).
6. **Re-run `ACDMCP_PALETTE`** to bring the palette back. Only needed if the user wants visual confirmation; AFK testing can skip this and drive `_batchRpc._uiState` directly once `_batchExecutor` is non-null.
    
**Verifying the reload landed:** after `ACDMCP_START`, run `Doc.Name` via REPL — if it succeeds, the new pipe is up. To confirm the new code is the one running, check `Acd.Mcp.McpPlugin.Version` (bump the version string before reloading so old vs new is visually obvious).
</reload-the-plugin-procedure>

<measurement-harness>
Pattern for "did the agent's button-push actually work" — repeat N cycles, count successes:

```csharp
int wireSuccess = 0;
var times = new List<long>();
for (int i = 0; i < 10; i++)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    // ... full cycle (set folder + mask + Refresh + Run + wait for RunCompleted) ...
    sw.Stop();
    times.Add(sw.ElapsedMilliseconds);
    if (/* report.Files.Count == expected */) wireSuccess++;
}
return new { wireSuccessRate = wireSuccess / 10.0, avgMs = times.Average() };
```

Separate **wire success** (did the click reach the executor and produce a completion event?) from **business success** (did the files actually Pass?). The first is a measure of the automation harness; the second depends on the script body and the runtime. Confusing the two is how you waste 30 minutes "fixing" a 100%-working harness because the script itself has a bug.
</measurement-harness>

<when-to-reach-for-an-mcp-server>
This recipe replaces an MCP-server install for THIS plugin in THIS session. Reach for a real MCP server (FlaUI-MCP, Windows-MCP, or Anthropic computer-use) when:

- You need to click in an app whose source you don't own.
- You need to handle native modal dialogs (file pickers, license, fatal errors).
- You're driving a fresh AutoCAD launch and need to interact before the plugin is loaded.

For everything inside ACD-MCP's own UI, reflection from the REPL is faster, more deterministic, and survives across builds without retraining.
</when-to-reach-for-an-mcp-server>

<autonomous-bootstrap>
The reflection harness above assumes the plugin is **already loaded and the pipe is up**. When the agent has to bring up a fresh Civil 3D from a cold machine — no user at the keyboard, no existing AutoCAD process — there is a four-step bootstrap chain. This section captures everything that worked in the v18 G6 verification (2026-05-13). Every step has a non-obvious gotcha; together they get from "no Civil 3D" to "agent has a live `autocad_execute_csharp` pipe" without a human in the loop.

<step-1-launch-with-automation>
The standard Start-Menu shortcut for Civil 3D 2025 Metric expands to:
```
acad.exe /ld "C:\Program Files\Autodesk\AutoCAD 2025\AecBase.dbx"
         /p "<<C3D_Metric>>"
         /product C3D
         /language en-US
```
Adding `/Automation` runs Civil 3D as a **hidden, out-of-process COM server**: the MDI frame is created but `IsWindowVisible == false`. DevReload Manager and Toolspace still pop visible (their `Show()` is unconditional), which is harmless. The CPU/license footprint is the same as a normal launch; the only externally observable difference is `MainWindowHandle == 0`.

```powershell
$args = '/ld "C:\Program Files\Autodesk\AutoCAD 2025\AecBase.dbx" /p "<<C3D_Metric>>" /product C3D /language en-US /Automation'
Start-Process "C:\Program Files\Autodesk\AutoCAD 2025\acad.exe" -ArgumentList $args
```
</step-1-launch-with-automation>

<step-2-devreload-auto-load>
ACD-MCP's `loadOnStartup: false` in `%APPDATA%\DevReload\plugins.json` is the production default — the user clicks "Load" in DevReload Manager when they want it. For unattended agent use, flip the flag:

```jsonc
{
  "name": "Acd.Mcp",
  "dllPath": "X:\\GitHub\\shtirlitsDva\\ACD-MCP\\src\\Acd.Mcp\\bin\\Debug\\Acd.Mcp.dll",
  "loadOnStartup": true,   // ← only change
  ...
}
```

The agent must restore `false` when the session ends — otherwise the user's next normal launch starts with the plugin already loaded, which is a behaviour change they didn't ask for. Wrap the flip in a try/finally around the verification.
</step-2-devreload-auto-load>

<step-3-listener-auto-start>
Even after `Initialize()` runs cleanly, the named pipe is NOT open — `ACDMCP_START` is a separate `[CommandMethod]` the user normally types. There's no way to type a command into a hidden `/Automation` Civil 3D (no visible CLI control, UIA can't reach it). Three options, in order of how clean they are:

1. **`Application.Idle` hook in `Initialize()` (DEBUG-only).** Subscribe once, fire `Start()`, unsubscribe:
   ```csharp
   #if DEBUG
       Application.Idle += AutoStartOnceOnIdle;
   #endif
   private static void AutoStartOnceOnIdle(object? s, EventArgs e) {
       Application.Idle -= AutoStartOnceOnIdle;
       SafeBoundary.Run("auto-start", () => Start());
   }
   ```
   `Idle` fires once AutoCAD's command loop is ready — the right barrier for "now it's safe to open the pipe". This is what worked in the v18 verification. Production builds (`#if !DEBUG`) keep the user-controlled behaviour.

2. **COM `SendCommand` via ROT.** Theoretically the journal-documented path. In practice, Civil 3D 2025 launched with `/Automation` against an unsaved `Drawing1.dwg` does NOT register in the ROT — verified empirically by enumerating monikers, no `AutoCAD.*` entries. The ROT moniker uses the drawing's full path; an unsaved drawing has no path, so no moniker. Workaround would be to pass a saved `.dwg` on the command line, but that's friction.

   **CORRECTION (2026-05-14, verified against pid 51832 Civil 3D 2025 on the DevReload-impl machine):** the "no `AutoCAD.*` entries in ROT" finding above was **a moniker-name search-string bug, not an empirical truth about the ROT**. A normal (non-`/Automation`) Civil 3D launch registers in the ROT immediately, even with no drawing open — but under its **CLSID** `!{363E5B47-885D-44C3-89EB-A2AB2129B57E}`, NOT under a `AutoCAD.Application`-style display name. Code that searches the ROT for the ProgID string `"AutoCAD.Application"` will find nothing; code that searches for the CLSID-in-braces substring finds the entry on the first try.
   
   Reach via:
   ```csharp
   [DllImport("ole32.dll")]  static extern int CLSIDFromProgID(string progId, out Guid clsid);
   [DllImport("oleaut32.dll")] static extern int GetActiveObject(ref Guid rclsid, IntPtr res, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
   CLSIDFromProgID("AutoCAD.Application", out var clsid);
   GetActiveObject(ref clsid, IntPtr.Zero, out var app);
   ```
   
   Returns a fully functional COM `AcadApplication` with `IsQuiescent`, `HWND`, `Name`, `Version`, `GetAcadState()` etc. all reachable. `ActiveDocument` still throws when no document is loaded — that part of the original write-up is correct — but `Documents.Add()` / `Documents.Open()` work because they're on the (always-available) `Documents` collection.
   
   Whether the `/Automation` flag changes this behaviour was NOT re-verified in the correction; the original empirical observation may still hold for `/Automation` specifically. For normal agentic-dev launches (visible AutoCAD, no `/Automation`), use COM via the CLSID path. See `H:\GitHub\shtirlitsDva\DevReload-impl\docs\shared-understanding\agent-autocad-control.md` (`<rot-moniker-format>`) for the full evidence dump and the `AcadComClient` implementation that uses it.

3. **Win32 `SendMessage` to the AutoCAD CLI HWND.** Doesn't work: the CLI is a WPF `AutoCompleteEdit_1` control with no native HWND that accepts `WM_CHAR`.

Option 1 is what to use. Tagged TEMPORARY in v18's code so it's obvious during reviews; revert before merging to main.
</step-3-listener-auto-start>

<step-4-pipe-readiness-detection>
After step 3 fires, the pipe exists at `\\.\pipe\acd-mcp-<PID>`. The intuitive readiness check fails:

```powershell
Test-Path "\\.\pipe\acd-mcp-$pid"   # returns False even when the pipe is open
```

`Test-Path` doesn't actually open the named-pipe handle; it returns false for valid-but-unconnected pipes. The reliable check enumerates the pipe filesystem directly:

```powershell
[System.IO.Directory]::GetFiles("\\.\\pipe\\") | Where-Object { $_ -match "acd-mcp-$pid" }
```

Wasted 90 s of monitor time in the v18 session on `Test-Path` before switching.
</step-4-pipe-readiness-detection>

<repl-alc-typeof-trap>
Once the pipe is up, the REPL can be driven via `autocad_execute_csharp`. **But:** the script submission lives in Roslyn's non-collectible in-memory ALC, while the plugin's own types (`Acd.Mcp.McpPlugin`, `Acd.Mcp.Batch.BatchExecutor`, ...) live in DevReload's collectible isolated ALC. The CLR forbids non-collectible → collectible references, so this fails:

```csharp
typeof(Acd.Mcp.McpPlugin).GetField(...)
// → FileNotFoundException: Could not load file or assembly 'Acd.Mcp, Version=...'
```

The workaround: reach plugin-internal types via `Assembly.GetType("FullName")` against an `AppDomain.CurrentDomain.GetAssemblies()` entry. The assembly object IS visible across ALCs; only static type references in the script's IL are forbidden.

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Acd.Mcp");
var pluginT = asm.GetType("Acd.Mcp.McpPlugin");          // ← reflection, not typeof
var bf = BindingFlags.NonPublic | BindingFlags.Static;
var version = pluginT.GetField("Version", bf).GetValue(null);
```

This is exactly the same rule G6 fought at the structural level for batch script bodies; the REPL hits it any time you `typeof()` a type the script's compile-side reference list does NOT promote into the default ALC. Treat it as "anything in `Acd.Mcp.dll` or `Acd.Mcp.Batch.dll` is reflection-only from the REPL".
</repl-alc-typeof-trap>

</autonomous-bootstrap>

<qa-standardization>
What this session proved: **a Civil 3D BATCH/REPL regression test can run end-to-end with no human at the keyboard.** The v18 verification went from "no Civil 3D running" to "5/5 Pass against five real DWGs" in under three minutes, with a single failure mode (license conflict if the user is already in Civil 3D). That's a CI-shaped problem.

The path from "I did it by hand in an agentic loop" to "every PR auto-runs" is mostly removing manual judgement. The pieces:

<what-the-harness-needs>
A QA harness for ACD-MCP should:
1. Detect / kill prior agent-launched Civil 3D processes (NOT the user's).
2. Launch Civil 3D with the documented `/Automation` arg set.
3. Flip `loadOnStartup` true in DevReload's `plugins.json` (and restore on tear-down).
4. Wait for `Initialize: v<NN>` AND `EnsureDtoGraph: Registered <N>` (with `N > 0`) in `%LOCALAPPDATA%\Acd.Mcp\log.txt` — NOT just the first; both gate the test.
5. Wait for the named pipe via `Directory.GetFiles("\\\\.\\pipe\\")` (not `Test-Path`).
6. Drive REPL + BATCH via the reflection harness in `<the-static-field-handle>` / `<driving-the-batch-palette>`.
7. Compare per-file `BatchRunReport.Results` against expected step outcomes for a fixed set of fixture DWGs (e.g. `tests/Acd.Mcp.IntegrationTests/fixtures/*.dwg`).
8. Tear down: kill Civil 3D, restore `loadOnStartup`, restore any palette state mutations.
</what-the-harness-needs>

<key-obstacles>
- **License singleton.** Civil 3D refuses two instances per Windows user session. A CI runner needs a dedicated session per concurrent test — easy on a Windows runner farm, awkward on a developer laptop running the suite locally.
- **Startup latency.** Civil 3D 2025 cold-start to "plugin ready" is ~60–90 s. Tests cluster well — once the pipe is up, a hundred BATCH runs cost ~14 ms each (see `<measurement-harness>`). Test runs should batch many assertions per launch, not launch per test.
- **DevReload coupling.** The agent harness assumes DevReload is installed and configured. A production-shape harness should not need DevReload — it should NETLOAD `Acd.Mcp.dll` directly. That's a separate piece of work: an `AcdMcp.IntegrationHost` exe that uses `accoreconsole.exe` or COM to NETLOAD without DevReload. Worth doing once; would also serve as the user's "special test app" sketched at the start of this session.
- **Bin-folder config.** `SharedAssemblies.Config.json` lives in `bin/` (gitignored), but the streamed-assembly list is now load-bearing for G6. Either a tracked template + post-build copy (see G8 in `CRASH_TEST_V2_JOURNAL.md`), or the integration host writes the file itself before launch.
- **Reflection-API stability.** Half the harness pokes private fields by name (`_batchRpc`, `_uiState`, `_batchExecutor`). A rename refactor silently breaks every test. Two ways to harden: (a) annotate the harness-reachable fields with a `[HarnessSurface]` attribute and add a smoke test that asserts they exist, or (b) expose the same verbs through MCP tools so the harness can call typed MCP RPCs instead of reflecting. Option (b) is the right shape long-term — see the unfinished `autocad_batch_run_test` / `batch_run_live` action item.
</key-obstacles>

<recommended-shape>
A minimum-viable QA harness, in three layers:

1. **`AcdMcp.IntegrationHost`** (new exe, ~200 LOC). Handles steps 1–5 of `<what-the-harness-needs>`: process management, log-tail wait, pipe-readiness detection, COM bind (or pipe client) once the pipe is up. Knows nothing about test cases.

2. **`AcdMcp.IntegrationTests`** (new xUnit project). One `IClassFixture` constructs the host, drives the BATCH palette + REPL, asserts on `BatchRunReport`. Tests look like ordinary `[Fact]` methods. Fixtures (.dwg files) live next to the test code, gitignored or LFS-tracked depending on size.

3. **CI wiring.** Self-hosted Windows runner with Civil 3D 2025 installed and a non-interactive license entitlement. GitHub Actions job: `dotnet test AcdMcp.IntegrationTests` with `[Trait("Category", "RequiresCivil3D")]` so PRs that change only non-AutoCAD code can skip it.

**Cost estimate from this session's data:**
- One full launch + verification cycle = 90–120 s (90 s cold-start + ~30 s for a dozen BATCH runs).
- Marginal cost of additional assertions inside one launch = ~15 ms each.
- 100 assertions across one launch ≈ 92 s wall-clock. Cheaper than a typical e2e browser test.

**What's NOT in scope for the first cut:** Live-mode runs (those touch real files, need disposable fixtures), `ACDMCP_PALETTE` interactive flows, anything that depends on the user clicking. The reflection harness handles every code path that matters for testing batch scripts and the REPL.

Once this exists, the `CRASH_TEST_*.md` journal pattern this repo has been using becomes redundant: the journal exists to record manual end-to-end verifications agents do by hand. With an automated harness, the journal collapses to "I added these test cases; they pass."
</recommended-shape>

</qa-standardization>

</computer-use-from-claude-code>
