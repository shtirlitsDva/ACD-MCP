using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Acd.Mcp.Batch
{
    // Two flavors only — batch (side-loaded database) and script (single-
    // drawing palette free-form). See <flavors> in the spec.
    public enum ScriptFlavor
    {
        Batch,
        Script,
    }

    // A persisted script on disk. Header lines (// @flavor:, // @name:,
    // // @summary:) are parsed and stripped from the body when read; written
    // back verbatim when saved (the body is whatever the caller hands us).
    public sealed record SavedScript(
        string Name,
        ScriptFlavor Flavor,
        string Summary,
        string Body,
        string Path);

    // Filesystem-backed catalogue:
    //   %APPDATA%\Acd.Mcp\scripts\batch\
    //   %APPDATA%\Acd.Mcp\scripts\script\
    //
    // Pure I/O; no AutoCAD, no UI. Both the agent (via the MCP tool) and
    // the user (via the Manage Scripts window) call into this same store.
    //
    // Legacy folder %APPDATA%\Acd.Mcp\scripts\repl\ is migrated to
    // scripts\script\ once at plugin Initialize (see McpPlugin.MigrateLegacyPaths).
    public sealed class SavedScriptStore
    {
        public string Root { get; }

        public SavedScriptStore(string? rootOverride = null)
        {
            Root = rootOverride ?? DefaultRoot();
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "batch"));
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "script"));
        }

        public string FolderFor(ScriptFlavor flavor) =>
            System.IO.Path.Combine(Root, flavor == ScriptFlavor.Batch ? "batch" : "script");

        // Pagination on listing. Default 50, max 200 — usually well below
        // the threshold a single response would overflow, but we still cap.
        public IReadOnlyList<SavedScript> List(ScriptFlavor flavor, int limit = 50, int offset = 0)
        {
            if (limit < 1) limit = 1;
            if (limit > 200) limit = 200;
            if (offset < 0) offset = 0;

            var folder = FolderFor(flavor);
            if (!Directory.Exists(folder)) return Array.Empty<SavedScript>();

            var files = Directory.GetFiles(folder, "*.csx")
                .OrderBy(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .Skip(offset)
                .Take(limit)
                .ToArray();

            var list = new List<SavedScript>(files.Length);
            foreach (var f in files)
            {
                try
                {
                    list.Add(Read(f, flavor));
                }
                catch
                {
                    // A broken file MUST NOT stop us listing the others.
                }
            }
            return list;
        }

        public int Count(ScriptFlavor flavor)
        {
            var folder = FolderFor(flavor);
            return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.csx").Length : 0;
        }

        public SavedScript? TryGet(ScriptFlavor flavor, string name)
        {
            var path = PathFor(flavor, name);
            return File.Exists(path) ? Read(path, flavor) : null;
        }

        public SavedScript Save(ScriptFlavor flavor, string name, string body, string? summary = null)
        {
            var safeName = Sanitise(name);
            var path = PathFor(flavor, safeName);
            // Reconstruct the header so the file is self-describing on disk.
            var fullBody = WithHeader(flavor, safeName, summary, body);
            File.WriteAllText(path, fullBody);
            return Read(path, flavor);
        }

        public bool Delete(ScriptFlavor flavor, string name)
        {
            var path = PathFor(flavor, Sanitise(name));
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public bool Rename(ScriptFlavor flavor, string oldName, string newName)
        {
            var oldPath = PathFor(flavor, Sanitise(oldName));
            var newPath = PathFor(flavor, Sanitise(newName));
            if (!File.Exists(oldPath)) return false;
            if (File.Exists(newPath)) throw new IOException($"A script named '{newName}' already exists.");
            File.Move(oldPath, newPath);
            return true;
        }

        public string PathFor(ScriptFlavor flavor, string name) =>
            System.IO.Path.Combine(FolderFor(flavor), Sanitise(name) + ".csx");

        // Header parsing. The script body is whatever the user typed; the
        // first lines may be // @flavor: / // @name: / // @summary:.
        // Missing fields fall back as documented in the spec.
        public static SavedScript Read(string path, ScriptFlavor folderFlavor)
        {
            var text = File.ReadAllText(path);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var flavor = folderFlavor;
            string summary = "";

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("//")) break;
                var m = HeaderLine.Match(trimmed);
                if (!m.Success) break;
                var key = m.Groups[1].Value.ToLowerInvariant();
                var val = m.Groups[2].Value.Trim();
                switch (key)
                {
                    case "flavor":
                        // Legacy header value `repl` maps to ScriptFlavor.Script
                        // so files saved before the rename keep parsing cleanly.
                        if (val.Equals("repl", StringComparison.OrdinalIgnoreCase))
                            flavor = ScriptFlavor.Script;
                        else if (Enum.TryParse<ScriptFlavor>(val, ignoreCase: true, out var f))
                            flavor = f;
                        break;
                    case "name": name = val; break;
                    case "summary": summary = val; break;
                }
            }

            return new SavedScript(name, flavor, summary, text, path);
        }

        private static readonly Regex HeaderLine =
            new(@"^//\s*@(\w+)\s*:\s*(.*)$", RegexOptions.Compiled);

        private static string WithHeader(ScriptFlavor flavor, string name, string? summary, string body)
        {
            // If the body already starts with header lines we don't strip
            // them — caller is responsible for the body. We always prepend
            // a header block; on the next read the parser will use the
            // first occurrence of each field. This is the simplest correct
            // shape and matches the spec's "missing field defaults to ..."
            // semantics.
            var nl = Environment.NewLine;
            var header =
                $"// @flavor: {(flavor == ScriptFlavor.Batch ? "batch" : "script")}{nl}" +
                $"// @name: {name}{nl}";
            if (!string.IsNullOrEmpty(summary))
                header += $"// @summary: {summary}{nl}";

            // If the body already begins with our exact header (overwriting
            // its own file), don't duplicate it.
            if (body.StartsWith(header)) return body;
            return header + body;
        }

        private static string Sanitise(string raw)
        {
            // Strip filesystem-unsafe characters but keep telegram-style
            // names readable. No path separators, no NUL, no reserved
            // Windows chars. Length capped at 80 to keep paths sane.
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (Array.IndexOf(invalid, ch) >= 0) continue;
                if (ch == ' ' || ch == '/' || ch == '\\') sb.Append('-');
                else sb.Append(ch);
            }
            var clean = sb.ToString().Trim('-');
            if (clean.Length == 0) clean = "unnamed";
            if (clean.Length > 80) clean = clean.Substring(0, 80);
            return clean;
        }

        private static string DefaultRoot() => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Acd.Mcp",
            "scripts");
    }
}
