---
name: acd-agentic-dev
description: Loop for agents DOING DEVELOPMENT on the ACD-MCP plugin itself — debugging, fixing, and live-verifying changes inside Civil 3D. Distinct from /acd-mcp:start (which is for agents USING the MCP). Auto-loads when the user mentions debugging ACD-MCP, fixing a plugin bug, adding a feature to BatchRunner/AcadBatchSession/etc, or running the test suite against a real AutoCAD.
when_to_use: User asks to fix a bug in ACD-MCP, add a feature to the plugin, refactor an internal class, run regression tests against Civil 3D, or any task that touches `src/Acd.Mcp*` and needs to be verified end-to-end. Sibling /acd-mcp:start is for plugin-USE workflows; this skill is for plugin-DEV workflows. Both can be active simultaneously.
---

<the-loop>
The agentic-dev loop. Each step is a checkpoint — never skip diagnosis to rush to a fix.

1. **Read the issue / brief.** Cite line numbers. Confirm the cited code is still where it's claimed to be. Codebases drift between issue-filing and issue-fixing.
2. **Verify the proximate-cause hypothesis before treating it.** Issues often state a hypothesis ("dispose throws and `catch{}` swallows the leak") that the live code refutes. Run the minimal repro and INSPECT — don't assume the issue author was right.
3. **Instrument first; treat second.** Any required SafeBoundary/logging improvement that's needed regardless of the eventual fix — do it FIRST. It gives you free diagnostic data for step 4.
4. **Run a minimal live repro via autocad_script_execute.** Stay close to the actual API call site. Capture the exact exception type + message; capture whether `Dispose()` itself throws. The script returns structured JSON; trust the data, not the hypothesis.
5. **Pick the fix direction from evidence.** If the issue offers multiple options, treat the "prefer X" guidance as advisory — push back when the evidence points elsewhere (rule 9).
6. **Write tests at the right layer.** Unit tests pin behaviour. OS-level interactions with AutoCAD's Database are NOT unit-testable; for those, commit a runnable `.csx` repro to `tests/repro/` and reference it from the issue resolution.
7. **Live-verify against a fresh build.** The plugin DLL is locked while AutoCAD runs. Close, rebuild, redeploy, relaunch.
8. **Inspect log.txt for new EXCEPTION entries since plugin init.** The SafeBoundary path is silent on success; a passing live test PLUS a clean log is the green signal.
</the-loop>

<autocad-development-gotchas>
Lessons that bite repeatedly.

1. **`Database.Dispose()` is NOT a synchronous handle release.** The underlying OS file handle survives `Dispose()` (finalizer-driven). Any pattern that opens-disposes-then-immediately-reopens-for-write will race the OS share rules. Use `FileShare.ReadWrite` on side-loaded reads when SaveAs is in the future. See issue #1 + `tests/repro/Test-EfilerErrorRepro.csx`.
2. **`FileShare.Read` means "I'm read, others can read but NOT write."** Setting it on your own open BLOCKS a future writer if a lingering handle exists. The intuition "Read is the safest share mode" is wrong for batch flows that read-then-write the same path.
3. **Auto-start is `#if DEBUG`.** Release builds will not auto-open the pipe — the user must type `ACDMCP_START`. When live-testing Release, plan how to send the command. Three workable channels:
   - `/b <script.scr>` startup script with `ACDMCP_START\r\n` (most reliable; requires relaunch)
   - `SendKeys` via WScript.Shell after `AppActivate` (loses to Windows foreground-stealing rules often enough to be unreliable)
   - COM `SendCommand` if you have an attach path (DevReload's Acad.Process bridge does this)
4. **`%LOCALAPPDATA%\Acd.Mcp\log.txt` is the source of truth, not the AutoCAD editor.** Read the tail filtered to "since Initialize for pid=N" to get only the current session's events. Zero EXCEPTION entries during a clean run is the success signal; the dispose path is intentionally silent on success.
5. **`script_execute` script bodies compile under Roslyn with all `Autodesk.AutoCAD.*` namespaces imported.** `Exception` is ambiguous between `System.Exception` and `Autodesk.AutoCAD.Runtime.Exception`; always qualify as `System.Exception`. Same defensive habit for `Path`, `Application`, `Database` if you import additional namespaces.
6. **Production tests can reach `internal sealed` types via reflection** — `AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Acd.Mcp")` then `type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)`. Use this when the BATCH palette UI gate prevents exercising Live mode through the normal entry point.
7. **The BATCH palette refuses Live mode unless every Test file passed** — that's a UI gate, not a runner gate. `BatchRunner.RunAsync(mode: BatchMode.Live)` runs Live directly; bypassing the gate is fine for diagnostic test runs.
</autocad-development-gotchas>

<test-layers>
Three test layers, ordered by speed + fidelity.

| Layer | Speed | Fidelity | When to use |
|---|---|---|---|
| `tests/Acd.Mcp.Tests/` (no AutoCAD) | < 1 s | Logic only | Pure-.NET behaviour: serialization, ResourceManager, registry, parsers. Pattern: link-include source files from `src/Acd.Mcp/` via `<Compile Include="..." Link="Sut\..."/>` to avoid the AutoCAD-referencing csproj. |
| `tests/Acd.Mcp.Batch.Tests/` (no AutoCAD) | < 1 s | Runner contract | BatchRunner two-phase semantics, fake `IDrawingHost` / `IFileAccessProbe` / `IBatchSession`. Roslyn `BatchScriptHost` needs `Microsoft.CodeAnalysis.CSharp.Scripting` as a package reference. |
| `tests/repro/*.csx` (requires Civil 3D) | seconds | OS + AutoCAD | Race conditions, share-mode semantics, anything the OS or AutoCAD's Database owns. Runnable via `autocad_script_execute` or paste-into-SCRIPT-palette. No CI integration yet — gated on a self-hosted Civil 3D-licensed Windows runner (same blocker as `scripts/Test-DtoSmoke.ps1`). |

When a fix is OS-level (file handles, Database internals, message dispatch), the `tests/repro/*.csx` is the regression artifact. Do NOT manufacture a symbolic unit test that doesn't actually pin the fix — write the runnable script + reference it from the issue resolution.
</test-layers>

<minimal-repro-template>
The shape of a typical investigation:

```csharp
// autocad_script_execute body
using System.IO;

var src = @"<path to representative test asset>";
var tmp = Path.Combine(Path.GetTempPath(), $"repro-{System.Guid.NewGuid():N}.dwg");
File.Copy(src, tmp, overwrite: true);
File.SetAttributes(tmp, File.GetAttributes(tmp) & ~FileAttributes.ReadOnly);

string disposeErr = null;
string saveErr = null;
bool saved = false;

// Phase 1: mimic the failure precondition
{
    var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
    db.ReadDwgFile(tmp, FileShare.Read, allowCPConversion: false, password: "");
    try { db.Dispose(); }
    catch (System.Exception ex) { disposeErr = ex.GetType().Name + ": " + ex.Message; }
}

// Phase 2: trigger the suspected failure
{
    var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
    db.ReadDwgFile(tmp, FileShare.Read, allowCPConversion: false, password: "");
    try { db.SaveAs(tmp, db.OriginalFileVersion); saved = true; }
    catch (System.Exception ex) { saveErr = ex.GetType().Name + ": " + ex.Message; }
    finally { db.Dispose(); }
}

try { File.Delete(tmp); } catch {}

return new {
    dispose_error = disposeErr,  // null = dispose clean
    saved,                       // false = bug reproduces
    save_error = saveErr         // exact exception type + message
};
```

The pattern: a SAFE COPY in `%TEMP%`, structured return shape, EXPLICIT exception capture (no `catch {}`). Bug repros that swallow exceptions waste a round-trip.
</minimal-repro-template>

<dev-environment-recipes>
Concrete commands for common dev tasks.

<reload-the-plugin>
The plugin DLL is locked while AutoCAD is running. Reload sequence:

```powershell
# 1. Close AutoCAD (preserve unsaved drawings if you care)
Stop-Process -Id <pid> -Force

# 2. Rebuild Release (matches what Install-Bundle.ps1 deployed)
dotnet build src/Acd.Mcp/Acd.Mcp.csproj -c Release -p:Platform=x64

# 3. Copy the freshly-built DLLs into the deployed bundle
$src = 'src\Acd.Mcp\bin\Release'
$dst = "$env:APPDATA\Autodesk\ApplicationPlugins\ACD-MCP.bundle\Contents"
Get-ChildItem $src -File |
    Where-Object { $_.Extension -in '.dll','.pdb' } |
    Copy-Item -Destination $dst -Force

# 4. Relaunch with a /b script that runs ACDMCP_START so the pipe comes up
$scr = "$env:TEMP\acdmcp-start.scr"
"ACDMCP_START`r`n" | Set-Content -LiteralPath $scr -Encoding ASCII -NoNewline
Start-Process 'C:\Program Files\Autodesk\AutoCAD 2025\acad.exe' `
    -ArgumentList @('/product','C3D','/nologo','/b',$scr)
```

For DevReload-style hot-reload of just the plugin DLL (no AutoCAD restart), see the sibling DevReload repo's `Acd.Rpc.Bridge` + `acad_send_command` tools.
</reload-the-plugin>

<wait-for-the-pipe>
```powershell
$pipeName = "acd-mcp-<pid>"
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') | ForEach-Object { Split-Path -Leaf $_ }
    if ($pipes -contains $pipeName) { break }
    Start-Sleep -Seconds 1
}
```

**Do NOT use `Test-Path \\.\pipe\<name>`** — Windows pipe Test-Path is unreliable; use the enumerated namespace.
</wait-for-the-pipe>

<inspect-log-since-current-init>
```powershell
$pid_ = <civil3d pid>
$content = Get-Content "$env:LOCALAPPDATA\Acd.Mcp\log.txt"
$start = ($content | Select-String "Initialize.*PID $pid_" | Select-Object -Last 1).LineNumber
$tail = $content[($start - 1)..($content.Count - 1)]
$exceptions = $tail | Select-String 'EXCEPTION'
"Tail: $($tail.Count) lines, EXCEPTION entries: $($exceptions.Count)"
$tail | ForEach-Object { Write-Host $_ }
```

`EXCEPTION` count > 0 means the SafeBoundary path captured something — investigate before declaring a fix verified.
</inspect-log-since-current-init>
</dev-environment-recipes>

<file-locations>
| Purpose | Path |
|---|---|
| AutoCAD-bound plugin (referenced AutoCAD assemblies) | `src/Acd.Mcp/` |
| Pure-.NET batch runner (no AutoCAD refs) | `src/Acd.Mcp.Batch/` |
| MCP stdio bridge | `src/Acd.Mcp.Bridge/` |
| Pure-.NET tests (link-include source) | `tests/Acd.Mcp.Tests/` |
| Pure-.NET batch tests (fake host/probe) | `tests/Acd.Mcp.Batch.Tests/` |
| Live `.csx` regression repros (need Civil 3D) | `tests/repro/` |
| Build + assemble + zip release | `scripts/Build-Release.ps1` |
| Install bundle to AutoCAD ApplicationPlugins | `install-hooks/Install-Bundle.ps1 -BundleSource Deploy\acd-mcp-plugin\autocad-bundle\ACD-MCP.bundle` |
| Plugin runtime log | `%LOCALAPPDATA%\Acd.Mcp\log.txt` |
| Deployed AutoCAD bundle | `%APPDATA%\Autodesk\ApplicationPlugins\ACD-MCP.bundle\Contents\` |
| Crash-test drawings (untracked, repo-root) | `crashtest-v2-dwgs\` |
</file-locations>

<engineering-rules-anchored>
The user's global engineering rules (see `~/.claude/CLAUDE.md` `<engineering-rules-strict>`) apply with extra force here:

- **Rule 4 (no rushing to fix bugs):** Verify the proximate-cause hypothesis with a live repro BEFORE editing code. Issue authors are not infallible.
- **Rule 1 (no overengineering):** A one-line OS-level fix earns a one-line OS-level fix + a runnable repro. Resist symbolic unit tests that don't actually pin the fix.
- **Rule 9 (push back on subpar requests):** When an issue's "prefer X" guidance conflicts with the evidence, name the conflict and propose the better path.
- **Rule 11 (mark dead code):** Phantom `.sln` entries (project listed but folder doesn't exist) qualify — fix or delete, never silently tolerate.
</engineering-rules-anchored>
