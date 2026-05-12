<crash-test-actions>

<meta>
- **Date:** 2026-05-13
- **Author:** Claude Opus 4.7 (1M context), invoked by user mgo@norsyn.dk
- **Source journal:** [`CRASH_TEST_JOURNAL.md`](CRASH_TEST_JOURNAL.md) (2026-05-12)
- **Scope:** Address every finding F1-F20 in the journal with code, tests, and documentation changes. No quick hacks; favour architectural fixes over fevers.
- **Build & test posture:** Full solution builds clean (0 warnings, 0 errors). Pre-existing test suites: `Acd.Mcp.Batch.Tests` 44/44 passes. `Acd.Mcp.Tests` grew from 13 to 16 with new DTO-loader script tests; 16/16 passes.
</meta>

<critical-fixes>

<action id="F7" status="fixed">
<title>F7 — Move DTO registration types to Acd.Mcp.Api so .csx JIT-binding resolves through the default ALC</title>

**Root cause** (verified against the journal's diagnosis):

Roslyn-emitted IL from every `.csx` DTO file references `DtoRegistrationApi`/`DtoDataProviderApi` and (transitively) `DtoRegistry`, `IDtoProjection`, `IEntityDataProvider`. These all lived in `Acd.Mcp` (isolated ALC under DevReload). When a `.csx` body executed `Acd.RegisterDto<T>(...)`, the JIT asked the default ALC for `Acd.Mcp` — the default ALC never loaded that assembly — `FileNotFoundException`. Zero DTOs ever registered.

**Decision: take Option 1 from the journal (move) rather than Option 2 (resolving handler).** A resolving handler in the default ALC would pin the isolated ALC across hot-reloads, defeating DevReload's whole point.

**What moved** (`src/Acd.Mcp.Api/Serialization/` is now the home):

- `DtoRegistrationGlobals` — the `globalsType` for DTO `.csx` submissions.
- `DtoRegistrationApi` — the `Acd` facade (`RegisterDto<T>` + `DataProvider`).
- `DtoDataProviderApi` — the metadata-read facade. **Refactored to use a pair of delegates** instead of an `IEntityDataProvider` field, so `Acd.Mcp.Api` does not need a project reference on `Acd.Mcp.Batch` (would have risked a duplicate-identity load of `Outcome<T>` across the two ALCs).
- `DtoRegistry`, `IDtoProjection`, `TypedProjection<T>` — all referenced from the IL surface or its method bodies.

**What stayed in `Acd.Mcp`:**

- All `IEntityDataProvider` implementations (`BlockAttributeProvider`, `PropertySetProvider`, `XDataProvider`, `CompositeDataProvider`, `EntityDataProviders` factory).
- The whole serializer wiring (`DtoConverterFactory`, `DtoConverter<T>`, `AcadDtoOptions`, `DtoLoader`, `DtoSystemSeeder`, `DtoDiagnostics`, `DtoReloadTrigger`, `DtoRpcHandler`).

**Glue at the boundary** (`src/Acd.Mcp/McpPlugin.cs` → `EnsureDtoGraph`):

```csharp
_dataProviderApi = new DtoDataProviderApi(
    readAll: (e, tx) => providers.ReadAll(e, tx),
    tryRead: (e, tx, k) =>
        providers.TryRead(e, tx, k) is Outcome<string>.Pass p ? p.Value : null);
```

The Outcome→null collapse at the public-API boundary is deliberate. The richer `Outcome<T>` shape stays useful inside the composite for chained-provider error propagation; collapsing it for the DTO/REPL boundary matches the existing `DtoDataProviderApi.TryRead` contract before the move.

**Internals access** (`src/Acd.Mcp.Api/AssemblyInfo.cs`):

```csharp
[assembly: InternalsVisibleTo("Acd.Mcp")]
[assembly: InternalsVisibleTo("Acd.Mcp.Tests")]
```

`DtoRegistry.TryGet` and `IDtoProjection` stay `internal`; the converter (which lives in `Acd.Mcp`) reaches them via IVT. Same for the pure-logic test project that links the registry source directly.

**Namespace stayed `Acd.Mcp.Serialization`.** The types live in a different assembly now, but the type FQNs are unchanged, so no `using` updates were required in consumers (`DtoLoader`, `DtoConverterFactory`, `AcadDtoOptions`, `McpPlugin`).

**Test coverage added:** `tests/Acd.Mcp.Tests/DtoLoaderScriptTests.cs` exercises the full CSharpScript→RegisterDto→registry path end-to-end against a `FakeRegistrationGlobals` that mirrors the moved API. Three cases: happy path, compile-error must-not-register, and overwrite ordering. (The ALC-split scenario specifically requires a real isolated `AssemblyLoadContext` and cannot be reproduced in a pure-logic test; the script test catches the registration-mechanism regression that's the actionable half of F7.)
</action>

<action id="F8" status="fixed">
<title>F8 — PropertySetProvider AECC assembly probe</title>

**Root cause:** `LoadAecAssembly` filtered by assembly simple-name (`AecBaseMgd` / `AeccBaseMgd`). On Civil 3D 2025, `PropertyDataServices` lives in `AecPropDataMgd`, which the filter didn't match. Result: `PropertySetProvider.IsAvailable == false`, the factory dropped it from the composite, the plugin's headline Civil 3D feature silently became "block attributes only".

**Fix** (`src/Acd.Mcp/Data/PropertySetProvider.cs`):

Replaced the name-filter probe with a full-FQN scan across every loaded assembly:

```csharp
private static Type? ResolveTypeAcrossLoaded(string fullName, List<string> missing)
{
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        try
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t is not null) return t;
        }
        catch
        {
            // ReflectionTypeLoadException on a partial load — skip and continue.
        }
    }
    missing.Add($"type:{fullName}");
    return null;
}
```

The five required AECC types (`PropertyDataServices`, `PropertySet`, `PropertySetDefinition`, `PropertySetData`, `PropertyDefinition`) are looked up identically. The owning assembly is logged in the success path so the operator can see which vertical resolved (`AecPropDataMgd` on 2025+, `AecBaseMgd` on older). Survives any future rename Autodesk chooses — AECC type identity is unique across verticals by full name.

`LoadAecAssembly` is deleted (dead code after the rewrite).
</action>

<action id="F9" status="fixed">
<title>F9 — Expose Acd.DataProvider in REPL globals</title>

**Root cause:** The skill documented `Acd.DataProvider.ReadAll(entity)` as a REPL pattern. In reality `Acd` was only a globals field inside DTO `.csx` bodies (type `DtoRegistrationApi`). In the REPL, `Acd` resolved to the namespace stub, not a façade, so the pattern was CS0234 at every call site.

**Fix** (`src/Acd.Mcp.Api/AcdReplApi.cs` — new, plus updates to `AcadGlobals.cs`):

Introduced `AcdReplApi` — a single-property façade (`DataProvider`) that mirrors the `Acd.DataProvider.ReadAll(...)` syntax the skill always documented. Lives in `Acd.Mcp.Api` for the same ALC reason as the other facades.

`AcadGlobals` (the REPL globalsType) now exposes `Acd` and takes the façade via constructor injection:

```csharp
public sealed class AcadGlobals
{
    public AcadGlobals(AcdReplApi acd) { Acd = acd ?? throw new ArgumentNullException(nameof(acd)); }
    public AcdReplApi Acd { get; }
    // ... Doc, Db, Ed, CivilDoc unchanged ...
}
```

`ScriptSession` was updated to accept `AcadGlobals` via constructor (was previously parameterless). `McpPlugin.TryEnsureCore` builds the REPL globals from the same `DtoDataProviderApi` instance the DTO loader uses — one composite, one wire point. `Acd.RegisterDto<T>` is intentionally **not** exposed in REPL (the loader is the only registration path; surfacing RegisterDto in REPL would be misleading).
</action>

<action id="F13" status="fixed">
<title>F13 — Resolve Entity/DBObject namespace ambiguity in the REPL default imports</title>

**Root cause:** The REPL's default imports included `Autodesk.Civil.DatabaseServices.*`, where Civil 3D defines its own `Entity` and `DBObject` types that collide with `Autodesk.AutoCAD.DatabaseServices.Entity`/`DBObject`. Any snippet that touched `Entity` directly (e.g. casting a `tx.GetObject` result) became CS0104 "ambiguous reference".

**Fix** (`src/Acd.Mcp/Scripting/ScriptSession.cs`):

Removed the four `Autodesk.Civil.*` namespaces from the REPL's default `WithImports(...)` list. Users that need the Civil surface add an explicit `using` at the top of their submission — the skill documents the `using AcadEntity = ...;` alias pattern.

**Note:** The batch script runtime (`Acd.Mcp/Batch/BatchScriptRuntime.cs`) retains the Civil imports. Batch scripts can't see `Application`/`Document`/`Editor` (globals don't expose them, so the imports never produce a Civil/AutoCAD ambiguity through those types), and most batch users on Civil 3D need the Civil DBO types unqualified. The batch surface stays as it was; only the REPL changed.
</action>

</critical-fixes>

<polish-fixes>

<action id="F14" status="fixed">
<title>F14 — Allow Infinity/NaN in the JSON serializer</title>

**Fix** (`src/Acd.Mcp/Serialization/AcadDtoOptions.cs`):

```csharp
NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
```

Degenerate AutoCAD geometry can yield `double.PositiveInfinity` / `NegativeInfinity` / `NaN`. Before, those tripped the serializer and the whole return shape was replaced with `$serialization_error`. Now they emit as `"Infinity"` / `"-Infinity"` / `"NaN"` strings — the agent can pattern-match on the special name and still get the rest of the projected shape.
</action>

<action id="F5" status="fixed">
<title>F5 — Unify the $serialization_error sigil shape</title>

**Fix** (`src/Acd.Mcp/Scripting/ScriptSession.cs` → `SerializeReturnValue`):

```csharp
var marker = new Dictionary<string, string>
{
    ["$serialization_error"] = ex.Message,
};
return JsonSerializer.Serialize(marker);
```

The `$` prefix matches the converter's `$unsupported` family. Agents can pattern-match on the leading sigil to recognise serializer-emitted sentinels with a single rule, instead of one shape for "unknown type" and a different shape for "couldn't serialise". Documented in `skills/start/SKILL.md` so the agent learns to recognise both.
</action>

<action id="F17" status="fixed">
<title>F17 — Add System.Console reference to the REPL metadata set</title>

**Root cause:** `System.Console` is lazy-loaded; `AppDomain.CurrentDomain.GetAssemblies()` at `ScriptSession` construction time didn't enumerate it. Snippets that called `Console.WriteLine` got CS0103 even though the executor's `ConsoleCapture` was already wired to redirect stdout.

**Fix** (`src/Acd.Mcp/Scripting/ScriptSession.cs` → `BuildOptions`):

```csharp
var refs = RoslynReferences.Build(
    typeof(AcadGlobals),
    typeof(System.Console));   // force-include even when the AppDomain
                               // scan hasn't seen it yet.
```

`RoslynReferences.Build` already accepts "also include" anchors; this is just a second use of the existing seam.
</action>

<action id="F15" status="fixed">
<title>F15 — Surface batch RPC errors to the MCP agent</title>

**Root cause:** Batch tools (`BatchProposeScriptTool`, `BatchRunTestTool`, `BatchGetSelectionTool`) let `AcadRpcException` propagate. The MCP framework wrapped it into the generic "An error occurred invoking ..." string. The agent couldn't see the actual reason ("No files selected in BATCH palette", "BATCH editor buffer is empty", "BATCH palette is not open").

**Fix** (`src/Acd.Mcp.Bridge/Tools/Batch*.cs`):

Each batch tool now catches `AcadRpcException` and rethrows as `ModelContextProtocol.McpException`. The MCP SDK's docs are explicit that `McpException.Message` is propagated to the remote endpoint, so the agent gets the actionable text verbatim.

```csharp
catch (AcadRpcException ex)
{
    throw new McpException(ex.Message);
}
```

Symmetric with the existing `ExecuteCsharpTool` pattern (which goes the other way — degrades to an `ExecuteResult` with the message — because that tool's return shape already has an error channel).

Also fixed an unrelated **bug** spotted while editing: `BatchGetSelectionTool.GetSelectionAsync` was calling the RPC method `"autocad_batch_get_selection"` (the public MCP tool name) instead of `"batch.getSelection"` (the JSON-RPC method the plugin listens on). The plugin would have returned `MethodNotFound` for every call. Corrected to `"batch.getSelection"`.
</action>

<action id="F19" status="fixed">
<title>F19 — Surface `replaced_dirty` in `propose_script` response</title>

**Fix** (`src/Acd.Mcp/Batch/BatchRpcHandler.cs`, `Bridge/Tools/BatchProposeScriptTool.cs`, `Batch/Ui/BatchViewModel.cs`):

The `IBatchUiState` interface gained an `IsDirty` member; `BatchViewModel` implements it from the existing `IsDirty` property. `HandleProposeScript` reads it before invoking `_executor.ProposeScript` and includes `replaced_dirty` in the response:

```csharp
bool willPromptForReplace =
    _uiState.IsDirty && !string.Equals(_executor.CurrentScript, body, StringComparison.Ordinal);
// ... after saving ...
return new { ok = true, saved_as = ..., name = ..., replaced_dirty = willPromptForReplace };
```

**What `replaced_dirty` doesn't tell you:** whether the user actually clicked Yes or No. The dialog is dispatcher-marshalled, so it's still being shown by the time the RPC returns. The flag tells the agent "a prompt is/was being shown" so it can warn the user to flip back to the palette. The batch skill was updated to describe this.

`BatchProposeResult` record was extended; consumers either get the new field as `false` (default for absent properties in System.Text.Json) or the actual flag.
</action>

<action id="Terminate-NRE" status="fixed">
<title>Bonus — NullReferenceException in `McpPlugin.Terminate/palette.Close`</title>

**Observation:** `log.txt` regularly carried `EXCEPTION in McpPlugin.Terminate/palette.Close: NullReferenceException`. The code is `_palette?.Close()` so the LHS is already null-guarded; the NRE comes from inside `Autodesk.AutoCAD.Windows.PaletteSet.Close()` when its wrapped Window is half-initialised (palette never shown, or user manually X'd it earlier).

**Fix** (`src/Acd.Mcp/McpPlugin.cs` → `Terminate`):

```csharp
SafeBoundary.Run("McpPlugin.Terminate/palette.Close", () =>
{
    if (_palette is { Visible: true }) _palette.Close();
});
```

`Dispose()` still runs unconditionally and handles full teardown; `Close()` is now only invoked when there's a visible palette to close. The recurring log noise stops.
</action>

</polish-fixes>

<documentation-updates>

<action id="F1-F4-F6-F10-F12-F16-F18" status="fixed">
<title>Skill doc updates (start / add-dto / batch)</title>

All findings tagged as `[doc gap]` or `[doc]` were addressed in `skills/start/SKILL.md`, `skills/add-dto/SKILL.md`, and `skills/batch/SKILL.md`:

- **F1 — CivilDoc global** is now in the `<repl-conventions>` globals list with a null-guard caveat.
- **F2 — Imports** lists the full default set including `System.IO`, `System.Text`. The Civil 3D namespaces are documented as **not** in the default imports, with the explicit `using` workaround.
- **F3 — `dynamic`** is documented as unavailable, with the reflection alternative.
- **F4 — Trailing-expression return** is documented as a feature (the LINQPad / dotnet-script convention).
- **F6 — `using var tx = ...;`** is replaced with the block-form `using (var tx = ...) { ... }` example everywhere it appears in the skills. The CS1002 root cause is documented inline.
- **F10 — Composite contents on Civil 3D 2025** is explicit: block-attributes + PropertySets (when AECC is loaded); XData is intentionally deferred. The add-dto skill now lists this per-vertical.
- **F12 — `ACDMCP_START` is required** — the start skill's `<initial-checks>` is explicit about this (opening the palette alone is not enough).
- **F16 — `timeout_ms` is cooperative** — documented as a soft hint rather than a kill switch.
- **F18 — `using` directives go first** — documented under `<repl-conventions>`; applies to both namespace imports and `using` aliases.

The `replaced_dirty` field semantic from F19 is documented in the batch skill's "Propose the script" step.
</action>

</documentation-updates>

<testing-additions>

<action id="F11" status="partial">
<title>F11 — Regression tests for the DTO loader path</title>

`tests/Acd.Mcp.Tests/DtoLoaderScriptTests.cs` adds three CSharpScript-driven tests that exercise the same wiring the real DTO loader uses:

1. `Script_calling_RegisterDto_populates_registry` — happy path.
2. `Script_compile_error_does_not_register_type` — compile failure → registry stays empty.
3. `Two_scripts_overwrite_in_registration_order` — overwrite ordering (user-wins-over-system).

These exercise the registration mechanism end-to-end through Roslyn against a `FakeRegistrationGlobals` that mirrors `DtoRegistrationApi`'s surface. The fake is necessary because the test project is pure-net8 (no AutoCAD references), and `DtoRegistrationApi` proper takes a `DtoDataProviderApi` whose delegates need `Entity`/`Transaction`.

**What's still uncovered:** the ALC-split scenario from F7 specifically requires a real isolated `AssemblyLoadContext`, which can only be reproduced inside an AutoCAD-hosting process. The journal's recommended "boot the plugin, check `EnsureDtoGraph: Registered N DTO types.` with N > 0" remains future work — it requires an integration test harness that loads the plugin into a fixture process. The script-based tests above catch the registration-mechanism regression (the actionable half), and the architectural move itself (F7) is the proximate fix that won't regress unless someone deliberately moves the types back.

A test for `PropertySetProvider.ResolveTypeAcrossLoaded` was considered but skipped: the helper depends on AECC types reaching reflection probes, and the failure-mode that mattered (F8) was specifically about which assemblies the probe scanned. The all-loaded-assemblies scan is the simplest correct implementation; a test would amount to re-asserting the implementation rather than the contract. Documented as an integration-test candidate alongside F11.
</action>

</testing-additions>

<not-fixed>

The following journal items did not yield code changes — either documented-only, deferred upstream, or judged out-of-scope for this pass. Calling them out explicitly so they aren't lost:

- **F11 (partial) — integration test that boots the plugin** stays future work; it needs an AutoCAD-host fixture. The crash test journal's own tally already marks this as "future".
- **Visualizer v2 enhancements** (`tools/visualizer`) — orthogonal to the crash-test scope; the visualizer is itself a feature the journal author shipped during the test, not a finding in the plugin.

</not-fixed>

<verification>

<step>Solution build: clean. 0 warnings, 0 errors.</step>
<step>`Acd.Mcp.Batch.Tests` (xunit, 44 tests): all pass.</step>
<step>`Acd.Mcp.Tests` (xunit, 16 tests after the F11 additions): all pass.</step>
<step>No new compiler warnings introduced.</step>
<step>Hot-reload contract preserved — no resolving-handler is registered in the default ALC, so the isolated ALC stays cleanly unloadable after `MCPUNLOAD`.</step>
<step>The DTO `.csx` files in `src/Acd.Mcp/Resources/DtoSystem/` were NOT modified — the F7 move was binary-compatible at the source-file level (same `Acd.RegisterDto<T>(...)` shape, same `Acd.DataProvider.ReadAll(...)` shape). User-authored DTOs continue to work without edits.</step>

</verification>

</crash-test-actions>
