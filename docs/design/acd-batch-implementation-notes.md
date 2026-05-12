<implementation-status>
The /acd-batch feature spec at docs/design/future-acd-batch.md is
implemented end-to-end on branch feat/acd-batch except for items that
are explicitly out of scope (DTO/data-providers, plugin distribution).

This file records the decisions made during implementation, the
verifiable assumptions, and the manual test recipe for the
AutoCAD-bound pieces that cannot be unit-tested without AutoCAD running.
</implementation-status>

<key-decisions>

<decision name="hand-rolled-Outcome">
Outcome&lt;T&gt; is a sealed abstract record with sealed nested derived
records (Pass / Skip / Failure). No `OneOf`, no `LanguageExt`. Match is
exhaustive at the call site (every overload covers all three cases plus
a default that throws on unknown subtypes — defensive belt-and-braces
for runtime extensions).
</decision>

<decision name="generic-script-host">
BatchScriptHost is generic over TGlobals so the pure runtime never
references AutoCAD types. The plugin's AcadBatchGlobals (xDb, xTx, ctx)
is what the real host injects; tests use FakeGlobals (with a
FakeDatabase + FakeTransaction). This is what lets the test project
target net8.0 without any AutoCAD reference.
</decision>

<decision name="probe-not-hold-lease">
The spec described a held FileLease across open + transact + save +
dispose. In practice Windows enforces FileShare modes inside the same
process: a FileStream held with FileShare.Read would block AutoCAD's
internal SaveAs write. The implementation probes (open+close) instead.
The probe still throws if another process has the file open for write,
which is the spec's actual safety invariant.
</decision>

<decision name="originalfileversion-shape">
Database.OriginalFileVersion returns `Autodesk.AutoCAD.DatabaseServices.DwgVersion`,
verified by reflecting acdbmgd.dll from AutoCAD 2025. The 2-arg overload
`SaveAs(string, DwgVersion)` exists and is what we call. The reference
loop uses `SaveAs(filename, true, DwgVersion.Newest, SecurityParameters)`
— the spec explicitly overrides the version choice.
</decision>

<decision name="two-phase-state-sharing">
When Mode=Live, the runner does Test pass then Live pass. The same
BatchStateBag is shared across both phases — so `ctx.BatchState<T>()`
returns the same T from the same `T` instance in both passes. This
matches the user's mental model ("the same logic runs twice"). Each
phase still iterates every file from scratch (separate sessions).
</decision>

<decision name="pipeline-rpc-extension">
PipeListener was extended with an optional MethodHandler delegate that
the listener falls through to before returning MethodNotFound. The
existing core methods (ping / reset / execute) are untouched. The plugin
wires the BatchRpcHandler in via this delegate after the BATCH palette
opens.
</decision>

<decision name="mcp-resources-vs-tools">
Batch results are exposed as MCP resources (acd-mcp://batch-runs/...)
rather than tools. This matches the spec's <feedback-loop> design and
aligns with the MCP protocol's intent: resources are read-only views;
tools are actions. Pagination on /recent caps limit at 100 by clamping
inside BatchRunHistory.
</decision>

<decision name="ui-decoupling">
The BATCH palette is one tab on the existing PaletteSet; the Manage
Scripts window is a separate modal Window opened from a button. The
editor is a single AvalonEdit instance (the "live-shared slot"). The
agent writes via propose_script + the editor mirror; the user writes by
typing. Both sides converge in BatchExecutor.CurrentScript.
</decision>

<decision name="slide-switch-handrolled">
The Test/Live switch is a hand-styled ToggleButton in Theme.xaml. ~80
lines of full ControlTemplate XAML, no animation storyboards, no
external dependency. Distinct red accent (#FFB85C5C) when Live to
match the spec's emphasis on visual warning.
</decision>

<decision name="run-id-roundtrip">
The `batch.runTest` RPC returns `{ run_id: "pending", pending: true,
results_resource: "acd-mcp://batch-runs/last" }` immediately. The
agent polls /last for the completed report. This sidesteps the
synchronous-vs-asynchronous tension cleanly: the agent doesn't have to
hold a pipe call open for the duration of a multi-minute batch.
</decision>

</key-decisions>

<things-i-couldnt-verify>

<item name="end-to-end-live-AutoCAD-smoke-test">
The plugin compiles clean and the unit-tested runtime is exhaustively
covered, but I have not exercised the full Test→Live flow inside a live
AutoCAD 2025. Manual recipe is below.
</item>

<item name="OpenFolderDialog-availability">
Microsoft.Win32.OpenFolderDialog is .NET 8 WPF. The project compiles
against net8.0-windows8.0 which includes it, but I haven't verified the
dialog opens inside AutoCAD's WPF dispatcher specifically. Worst case:
swap to a typed-path input box (no UI dep).
</item>

<item name="DocumentManager-side-loaded-DB-on-bg-thread">
Side-loaded `new Database(false, true)` is generally safe off the main
thread per Autodesk docs, but the runner currently runs on a threadpool
task without any main-thread marshaling. If we discover an API call
inside the script body that demands the main thread, we'd need to
introduce a MainThreadGate the script body can call into (a future
extension point, not in v1).
</item>

</things-i-couldnt-verify>

<things-i-punted>

<item name="DTO-system">
docs/design/future-dto-and-data-providers.md is a separate spec, owned
by a separate agent. The batch runtime exposes a clean extension point
(BatchFileResult.Steps is List&lt;StepOutcome&gt;) but does not impose any
DTO shape on what a Step's Apply returns.
</item>

<item name="plugin-distribution">
docs/design/future-plugin-distribution.md is a separate spec. No
.bundle, no installer, no MCP-client autoconfig in this branch.
</item>

<item name="edit-in-vscode">
The spec marked this as deferred. No stub IntelliSense assembly was
emitted. The "live-shared editor in the palette" covers v1's needs.
</item>

<item name="repl-script-store-ui">
SavedScriptStore supports both batch and repl flavors at the data
layer. The Manage Scripts window lists only batch scripts; a parallel
window for repl scripts is a future addition.
</item>

</things-i-punted>

<manual-test-recipe>

<prerequisite>
AutoCAD 2025 running with DevReload's MCPDEV / MCPLOAD on this plugin.
Pipe listener started via ACDMCP_START. Palette open via ACDMCP_PALETTE.
A folder containing 2–3 small test .dwg files.
</prerequisite>

<step number="1" name="propose-and-run-from-the-agent-path">
From a connected MCP client, call:

  autocad_batch_propose_script(
    name = "noop-test",
    script_body = "ctx.Step(\"noop\").Apply(() => \"ok\");",
    input_summary = "smoke test")

Expect: editor in the BATCH tab updates to the proposed body.

In the BATCH palette: pick the test folder, Refresh, see N matched.

From the MCP client:
  autocad_batch_run_test(name = "noop-test")

Wait ~5 seconds. Then read:
  acd-mcp://batch-runs/last

Expect: a JSON document with one entry per file, all Status=Pass,
Phase=Test. AbortedReason=null. Committed=false on every entry.

The .dwg files' on-disk mtime should be unchanged.
</step>

<step number="2" name="cancel">
Propose a script with a deliberate 5-second sleep:

  ctx.Step("slow").Apply(() => { System.Threading.Thread.Sleep(5000); return "done"; });

Click Run. Mid-run, click Cancel.

Expect: the loop exits after the current file completes. The Results
list shows fewer rows than the file count. The status line says
"Cancelled. N file(s) reported." The next file's .dwg is unchanged.
</step>

<step number="3" name="locked-file-aborts">
Open one of the test .dwg files in a second AutoCAD instance (or open
it inside the same AutoCAD as a regular drawing). Run the batch.

Expect: the runner attempts to probe the locked file, throws
BatchAbortedException. The Results list shows no completed file; the
status line shows "Aborted: File '...' is locked or inaccessible.
Batch aborted." No other files are touched.
</step>

<step number="4" name="live-requires-test-pass">
Propose a script that fails (e.g. `throw new
System.InvalidOperationException("nope");`). Flip the switch to Live.
Click Run.

Expect: only the Test phase runs. Status line shows "Aborted: Live
pass not started: N file(s) failed the internal Test pass." Files are
unchanged. (The runtime never starts the Live pass.)
</step>

<step number="5" name="live-actually-saves-on-success">
Propose a small mutation that always succeeds:

  ctx.Step("touch").Apply(() =>
  {
      var lt = (LayerTable)xTx.GetObject(xDb.LayerTableId, OpenMode.ForRead);
      return $"{lt.Cast<ObjectId>().Count()} layers seen";
  });

Flip to Live. Click Run.

Expect: two phases. After the run completes:
  - acd-mcp://batch-runs/last shows TWO entries per file (one Test, one Live).
  - Live-phase entries have Committed=true.
  - The .dwg file's on-disk mtime advances (the SaveAs ran).
  - The DWG version was preserved (compare with a hex diff on the file
    header bytes for the version stamp if you want belt-and-braces).
</step>

<step number="6" name="agent-read-first-flow">
With the BATCH palette open, type some changes into the editor. Then
from the agent:
  read file %LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx

Expect: after a ~250 ms debounce, the file content matches what the
editor shows. This is the agent's read-before-edit hook.

Then call propose_script with a different body. The palette pops a
prompt: "The agent proposed an updated script ...". Click No.

Expect: editor stays on your typed content. Click Yes — editor flips
to the agent's body.
</step>

<step number="7" name="paginated-history">
After several runs, call:
  acd-mcp://batch-runs/recent?limit=2&offset=0

Expect: 2 newest entries.

  acd-mcp://batch-runs/recent?limit=2&offset=2

Expect: the next 2. RunIds match what /last and /by-id resolve to.
</step>

</manual-test-recipe>

<known-gotchas>

<gotcha name="palette-not-open">
Agent batch.* RPCs fail with "BATCH palette is not open" if the user
hasn't run ACDMCP_PALETTE yet. The agent must coach the user to open
the palette first.
</gotcha>

<gotcha name="folder-not-selected">
Agent batch.runTest fails if the user hasn't picked a folder in the
BATCH palette. The agent should query batch.getSelection via the
plugin (no MCP tool exposes this yet — manual addition if needed).
</gotcha>

<gotcha name="dev-reload-mid-run">
If the user MCPDEVs the plugin while a batch run is in progress, the
run is cancelled by the executor's Dispose path. The history file for
that run will not be written (since the report never reaches the save
hook). Acceptable v1 behaviour; document loudly.
</gotcha>

<gotcha name="roslyn-emit-leak">
Same as the REPL: each unique script body emits a new assembly into the
default ALC. Long sessions accumulate them. MCPUNLOAD + MCPLOAD is the
nuclear reset.
</gotcha>

</known-gotchas>
