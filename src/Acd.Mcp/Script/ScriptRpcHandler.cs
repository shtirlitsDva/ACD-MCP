using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Batch;

namespace Acd.Mcp.Script
{
    // Pipe RPC surface for SCRIPT-flavor calls (single-drawing operations
    // against the active document). Mirrors BatchRpcHandler's shape so
    // the BATCH and SCRIPT flows are read with the same approach.
    //
    // The methods stay narrow:
    //   script.proposeScript    — agent writes a script + stages it in
    //                             the SCRIPT editor (UI accepts/discards).
    //   script.getEditor        — fetch the editor's current text + mirror
    //                             path (the agent's "read-before-propose"
    //                             input).
    //   script.listSavedScripts — paginated saved-script list (Script flavor).
    //   script.getSavedScript   — fetch one saved script by name.
    //
    // The SCRIPT surface has no folder/mask/file selection or run-test
    // analog — direct execution stays on the existing autocad_script_execute
    // path (see McpPlugin's pipe handler for that route).
    internal sealed class ScriptRpcHandler
    {
        private readonly ScriptEditor _editor;

        public ScriptRpcHandler(ScriptEditor editor)
        {
            if (editor is null) throw new ArgumentNullException(nameof(editor));
            if (editor.Flavor != ScriptFlavor.Script)
                throw new ArgumentException(
                    $"ScriptRpcHandler requires a ScriptEditor with Flavor=Script (got {editor.Flavor}).",
                    nameof(editor));
            _editor = editor;
        }

        public Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            object? result = method switch
            {
                "script.proposeScript"    => HandleProposeScript(parameters),
                "script.getEditor"        => HandleGetEditor(),
                "script.listSavedScripts" => HandleListSavedScripts(parameters),
                "script.getSavedScript"   => HandleGetSavedScript(parameters),
                _ => null,
            };
            return Task.FromResult(result);
        }

        private object HandleProposeScript(JsonElement p)
        {
            var name = GetRequiredString(p, "name");
            var body = GetRequiredString(p, "script_body");
            var summary = GetOptionalString(p, "input_summary");

            // Snapshot the editor's dirty flag BEFORE proposing. If it
            // was dirty AND the new body differs from the user's text,
            // the UI will prompt — tell the agent so it can warn the
            // user proactively. The user's actual Yes/No can't be
            // reported synchronously here because the dialog is
            // dispatcher-marshalled and the RPC call returns first.
            //
            // CurrentText reflects what the user is actually editing
            // (staging model — proposals don't commit until Accept), so
            // this comparison is honest.
            bool willPromptForReplace =
                _editor.IsDirty && !string.Equals(_editor.CurrentText, body, StringComparison.Ordinal);

            var saved = _editor.ProposeFromAgent(name, body, summary);
            return new
            {
                ok = true,
                saved_as = saved.Path,
                name = saved.Name,
                replaced_dirty = willPromptForReplace,
            };
        }

        private object HandleGetEditor() =>
            new { body = _editor.CurrentText, mirror_path = _editor.MirrorPath };

        private object HandleListSavedScripts(JsonElement p)
        {
            int limit = GetOptionalInt(p, "limit") ?? 50;
            int offset = GetOptionalInt(p, "offset") ?? 0;
            var scripts = _editor.Store.List(ScriptFlavor.Script, limit, offset);
            return new
            {
                flavor = "script",
                limit,
                offset,
                total = _editor.Store.Count(ScriptFlavor.Script),
                entries = System.Linq.Enumerable.Select(scripts, s => new
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
            var s = _editor.Store.TryGet(ScriptFlavor.Script, name);
            if (s is null) throw new InvalidOperationException($"No saved script named '{name}'.");
            return new { name = s.Name, flavor = s.Flavor.ToString().ToLowerInvariant(), summary = s.Summary, body = s.Body, path = s.Path };
        }

        // Parameter helpers — duplicated from BatchRpcHandler intentionally
        // (each handler owns its parameter parsing; sharing would tie the
        // two handlers together for no real benefit). Same shape, same
        // error messages.
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
            if (p.ValueKind != JsonValueKind.Object) return null;
            if (!p.TryGetProperty(name, out var e)) return null;
            if (e.ValueKind != JsonValueKind.String) return null;
            return e.GetString();
        }

        private static int? GetOptionalInt(JsonElement p, string name)
        {
            if (p.ValueKind != JsonValueKind.Object) return null;
            if (!p.TryGetProperty(name, out var e)) return null;
            if (e.ValueKind != JsonValueKind.Number) return null;
            return e.TryGetInt32(out var v) ? v : null;
        }
    }
}
