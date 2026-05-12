using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Batch;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Batch.Runtime
{
    // Pipe RPC surface for batch-related calls. Lives next to the executor
    // so the dispatch table is in one place; the listener forwards any
    // method that starts with "batch." to this handler.
    //
    // The methods deliberately stay narrow:
    //   batch.proposeScript     — agent writes a script + pushes it to editor.
    //   batch.runTest           — agent kicks off a test-mode run.
    //                              (No counterpart for Live — see spec.)
    //   batch.listRuns          — paginated history list.
    //   batch.getRun            — fetch one run by id.
    //   batch.getLastRun        — fetch the most recent run.
    //   batch.listSavedScripts  — paginated saved-script list.
    //   batch.getSavedScript    — fetch one saved script by name.
    //   batch.getEditor         — fetch the editor's current text.
    //   batch.getSelection      — fetch the UI's current folder+mask+files.
    //
    // The Bridge fronts a subset of these as MCP tools and the rest as
    // MCP resources. The plugin-side handler doesn't care how the Bridge
    // presents them.
    internal sealed class BatchRpcHandler
    {
        private readonly BatchExecutor _executor;
        private readonly IBatchUiState _uiState;

        public BatchRpcHandler(BatchExecutor executor, IBatchUiState uiState)
        {
            _executor = executor;
            _uiState = uiState;
        }

        public async Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            return method switch
            {
                "batch.proposeScript" => HandleProposeScript(parameters),
                "batch.runTest"       => await HandleRunTestAsync(parameters, ct).ConfigureAwait(false),
                "batch.listRuns"      => HandleListRuns(parameters),
                "batch.getRun"        => HandleGetRun(parameters),
                "batch.getLastRun"    => HandleGetLastRun(),
                "batch.listSavedScripts" => HandleListSavedScripts(parameters),
                "batch.getSavedScript"   => HandleGetSavedScript(parameters),
                "batch.getEditor"     => HandleGetEditor(),
                "batch.getSelection"  => HandleGetSelection(),
                _ => null, // signals "method not handled by this handler"
            };
        }

        private object HandleProposeScript(JsonElement p)
        {
            var name = GetRequiredString(p, "name");
            var body = GetRequiredString(p, "script_body");
            var summary = GetOptionalString(p, "input_summary");
            var saved = _executor.ProposeScript(name, body, summary);
            return new { ok = true, saved_as = saved.Path, name = saved.Name };
        }

        private Task<object> HandleRunTestAsync(JsonElement p, CancellationToken ct)
        {
            // name is OPTIONAL. With a name, we load that saved script into
            // the editor first (legacy / explicit path). Without a name, we
            // run whatever the live editor buffer currently holds — that's
            // the common case right after batch.proposeScript and matches
            // what the user sees in the palette.
            var name = GetOptionalString(p, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var saved = _executor.Scripts.TryGet(ScriptFlavor.Batch, name!)
                    ?? throw new InvalidOperationException(
                        $"No saved batch script named '{name}'. Call batch.proposeScript first, or omit `name` to run the editor buffer.");

                // Push back into the editor as the live-shared slot. We do
                // NOT honour an "unsaved edits" dialog here — if the user
                // has dirty edits, the UI's prompt is the authoritative
                // arbiter; the pipe path simply pushes. The UI subscribes
                // to the ScriptProposed event and decides.
                _executor.ProposeScript(saved.Name, saved.Body, saved.Summary);
            }
            else if (string.IsNullOrWhiteSpace(_executor.CurrentScript))
            {
                throw new InvalidOperationException(
                    "BATCH editor buffer is empty. Either pass a saved-script `name`, or call batch.proposeScript first.");
            }

            var files = _uiState.CurrentSelection;
            if (files.Count == 0)
                throw new InvalidOperationException(
                    "No files are currently selected in the BATCH palette. Set a folder + mask first.");

            // StartTestRun returns the real run_id immediately (generated
            // before the worker task even starts). The agent polls
            // acd-mcp://batch-runs/<run_id> for THAT specific run, so a
            // concurrent UI Live run can't be mistaken for the agent's test.
            var runId = _executor.StartTestRun(files);

            return Task.FromResult<object>(new
            {
                run_id = runId,
                pending = true,
                results_resource = $"acd-mcp://batch-runs/{runId}",
                note = "Run started. Poll the results_resource URI until the report is complete.",
            });
        }

        private object HandleListRuns(JsonElement p)
        {
            int limit = GetOptionalInt(p, "limit") ?? BatchRunHistory.DefaultLimit;
            int offset = GetOptionalInt(p, "offset") ?? 0;
            var summaries = _executor.History.ListRecent(limit, offset);
            return new
            {
                limit,
                offset,
                total = _executor.History.Count(),
                entries = summaries,
            };
        }

        private object HandleGetRun(JsonElement p)
        {
            var id = GetRequiredString(p, "run_id");
            var report = _executor.History.Load(id);
            if (report is null)
                throw new InvalidOperationException($"No batch run with id '{id}'.");
            return report;
        }

        private object HandleGetLastRun()
        {
            var summary = _executor.History.LoadLastSummary();
            if (summary is null) return new { exists = false };
            var report = _executor.History.Load(summary.RunId);
            if (report is null) return new { exists = false };
            return report;
        }

        private object HandleListSavedScripts(JsonElement p)
        {
            int limit = GetOptionalInt(p, "limit") ?? 50;
            int offset = GetOptionalInt(p, "offset") ?? 0;
            var flavor = ScriptFlavor.Batch;
            if (TryGetString(p, "flavor", out var fs) &&
                Enum.TryParse<ScriptFlavor>(fs, ignoreCase: true, out var f))
                flavor = f;
            var scripts = _executor.Scripts.List(flavor, limit, offset);
            return new
            {
                flavor = flavor.ToString().ToLowerInvariant(),
                limit,
                offset,
                total = _executor.Scripts.Count(flavor),
                entries = scripts.Select(s => new
                {
                    name = s.Name,
                    flavor = s.Flavor.ToString().ToLowerInvariant(),
                    summary = s.Summary,
                    path = s.Path,
                }),
            };
        }

        private object HandleGetSavedScript(JsonElement p)
        {
            var name = GetRequiredString(p, "name");
            var flavor = ScriptFlavor.Batch;
            if (TryGetString(p, "flavor", out var fs) &&
                Enum.TryParse<ScriptFlavor>(fs, ignoreCase: true, out var f))
                flavor = f;
            var s = _executor.Scripts.TryGet(flavor, name);
            if (s is null) throw new InvalidOperationException($"No saved {flavor} script named '{name}'.");
            return new { name = s.Name, flavor = s.Flavor.ToString().ToLowerInvariant(), summary = s.Summary, body = s.Body, path = s.Path };
        }

        private object HandleGetEditor() =>
            new { body = _executor.CurrentScript, mirror_path = _executor.Editor.MirrorPath };

        private object HandleGetSelection()
        {
            var sel = _uiState.CurrentSelection;
            return new
            {
                folder = _uiState.CurrentFolder,
                mask = _uiState.CurrentMask,
                recurse = _uiState.Recurse,
                files = sel,
                count = sel.Count,
            };
        }

        private static string GetRequiredString(JsonElement p, string name)
        {
            if (p.ValueKind != JsonValueKind.Object ||
                !p.TryGetProperty(name, out var e) ||
                e.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"Missing required parameter '{name}' (string).");
            return e.GetString()!;
        }

        private static string? GetOptionalString(JsonElement p, string name)
        {
            return TryGetString(p, name, out var s) ? s : null;
        }

        private static bool TryGetString(JsonElement p, string name, out string value)
        {
            value = "";
            if (p.ValueKind != JsonValueKind.Object) return false;
            if (!p.TryGetProperty(name, out var e)) return false;
            if (e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString() ?? "";
            return true;
        }

        private static int? GetOptionalInt(JsonElement p, string name)
        {
            if (p.ValueKind != JsonValueKind.Object) return null;
            if (!p.TryGetProperty(name, out var e)) return null;
            if (e.ValueKind != JsonValueKind.Number) return null;
            return e.TryGetInt32(out var v) ? v : null;
        }
    }

    // The UI owns folder / mask / file list. The pipe queries via this
    // narrow read-only surface; the WPF view-model implements it.
    public interface IBatchUiState
    {
        string CurrentFolder { get; }
        string CurrentMask { get; }
        bool Recurse { get; }
        IReadOnlyList<string> CurrentSelection { get; }
    }
}
