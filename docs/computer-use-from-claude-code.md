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
4. **Trigger DevReload's "reload ACD-MCP" action.** Find DevReload's public API or its reload command (TODO: confirm exact command/method during the first iteration of the fix loop, then update this doc).
5. **Re-run `ACDMCP_START`** to wake the pipe listener. First call after a fresh ALC takes a few seconds (Roslyn warm-up, DTO graph rebuild).
6. **Re-run `ACDMCP_PALETTE`** to bring the palette back. Only needed if the user wants visual confirmation; AFK testing can skip this and drive `_batchRpc._uiState` directly once `_batchExecutor` is non-null.

**Important caveat (per @mgo):** `Acd.Mcp.Api.dll` lives in the Default ALC and **does NOT hot-reload**. Any change to types in that assembly requires a full Civil 3D restart. Stick to `Acd.Mcp.dll` / `Acd.Mcp.Batch.dll` for the agentic loop. If a fix forces a change in `Acd.Mcp.Api.dll`, finish the loop in the next session.

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

</computer-use-from-claude-code>
