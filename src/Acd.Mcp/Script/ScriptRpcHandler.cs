using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acd.Mcp.Batch;
using Acd.Mcp.Ui;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Document = Autodesk.AutoCAD.ApplicationServices.Document;

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
    //   script.getSelection     — read the active drawing's pickfirst
    //                             selection (entity handles, types,
    //                             layers, block names) + the drawing
    //                             that owns them.
    //
    // The SCRIPT surface has no folder/mask/file selection or run-test
    // analog — direct execution stays on the existing autocad_script_execute
    // path (see McpPlugin's pipe handler for that route).
    internal sealed class ScriptRpcHandler
    {
        private readonly ScriptEditor _editor;
        private readonly IPaletteHost _paletteHost;
        private readonly SynchronizationContext _mainSync;

        public ScriptRpcHandler(
            ScriptEditor editor,
            IPaletteHost paletteHost,
            SynchronizationContext mainSync)
        {
            if (editor is null) throw new ArgumentNullException(nameof(editor));
            if (editor.Flavor != ScriptFlavor.Script)
                throw new ArgumentException(
                    $"ScriptRpcHandler requires a ScriptEditor with Flavor=Script (got {editor.Flavor}).",
                    nameof(editor));
            _editor = editor;
            _paletteHost = paletteHost;
            _mainSync = mainSync ?? throw new ArgumentNullException(nameof(mainSync));
        }

        public Task<object?> DispatchAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            object? result = method switch
            {
                "script.proposeScript"    => HandleProposeScript(parameters),
                "script.getEditor"        => HandleGetEditor(),
                "script.listSavedScripts" => HandleListSavedScripts(parameters),
                "script.getSavedScript"   => HandleGetSavedScript(parameters),
                "script.getSelection"     => HandleGetSelection(),
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

            // Surface the staged proposal: open the palette if the user
            // hasn't yet. ScriptViewModel checks PendingProposal on
            // construction (Ui/ScriptViewModel.cs:120), so a late-opening
            // palette picks up the staged proposal correctly.
            _paletteHost.EnsureVisible();

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

        // Reads the active drawing's pickfirst selection on the main
        // thread. The dispatcher runs on a threadpool thread (pipe
        // listener), so any Database / Editor read MUST be marshalled
        // via _mainSync.Send. We capture exceptions inside the
        // delegate and rethrow on the caller's thread so the pipe
        // dispatcher's existing error -> JSON-RPC envelope path
        // handles them uniformly.
        //
        // For dynamic blocks (BlockReference.IsDynamicBlock) we read
        // the name from DynamicBlockTableRecord rather than the
        // anonymous BlockTableRecord — that's the name the user sees
        // in the Properties palette and the friendliest signal for
        // the agent.
        private object HandleGetSelection()
        {
            Exception? captured = null;
            object? result = null;
            _mainSync.Send(_ =>
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc is null)
                        throw new InvalidOperationException(
                            "NO_ACTIVE_DOCUMENT: no drawing is currently open in AutoCAD.");

                    using (doc.LockDocument())
                    {
                        var psr = doc.Editor.SelectImplied();
                        string docName = ResolveDocumentName(doc);
                        string? docPath = ResolveDocumentPath(doc);

                        if (psr.Status != PromptStatus.OK || psr.Value is null)
                        {
                            result = new
                            {
                                document_name = docName,
                                document_path = docPath,
                                count = 0,
                                entities = Array.Empty<object>(),
                            };
                            return;
                        }

                        var entities = new List<object>();
                        using (var tx = doc.Database.TransactionManager.StartTransaction())
                        {
                            foreach (var id in psr.Value.GetObjectIds())
                            {
                                if (tx.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;

                                string? blockName = null;
                                if (ent is BlockReference br)
                                {
                                    var btrId = br.IsDynamicBlock
                                        ? br.DynamicBlockTableRecord
                                        : br.BlockTableRecord;
                                    var btr = (BlockTableRecord)tx.GetObject(btrId, OpenMode.ForRead);
                                    blockName = btr.Name;
                                }

                                entities.Add(new
                                {
                                    handle       = id.Handle.ToString(),
                                    object_class = ent.GetType().Name,
                                    layer        = ent.Layer,
                                    block_name   = blockName,
                                });
                            }
                            tx.Commit();
                        }

                        result = new
                        {
                            document_name = docName,
                            document_path = docPath,
                            count = entities.Count,
                            entities,
                        };
                    }
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            }, null);

            if (captured is not null) throw captured;
            return result!;
        }

        // doc.Database.Filename is "" for unsaved drawings; doc.Name
        // is the title-bar label (e.g. "Drawing1.dwg" for unsaved,
        // "Floor1.dwg" or the full path depending on AutoCAD settings
        // for saved). Prefer the path-derived filename when available
        // so the agent always sees a real filename, never a full path
        // it has to strip itself.
        private static string ResolveDocumentName(Document doc)
        {
            var path = doc.Database.Filename;
            return string.IsNullOrEmpty(path) ? doc.Name : Path.GetFileName(path);
        }

        private static string? ResolveDocumentPath(Document doc)
        {
            var path = doc.Database.Filename;
            return string.IsNullOrEmpty(path) ? null : path;
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
