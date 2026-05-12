using Acd.Mcp.Scripting;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Acd.Mcp.McpPlugin))]

#if DEBUG
// In DEBUG we expect to be loaded by DevReload. The empty NoAutoCommands type
// "claims" command registration so AutoCAD's ExtensionLoader skips its auto-scan,
// leaving DevReload's CommandRegistrar (which uses Utils.AddCommand) responsible.
// This matters because Utils.AddCommand is removable on ALC unload, while AutoCAD's
// permanent CommandClass.AddCommand is not — keeping the permanent path would yield
// eDuplicateKey on the second hot-reload.
[assembly: CommandClass(typeof(Acd.Mcp.NoAutoCommands))]
#endif

namespace Acd.Mcp
{
#if DEBUG
    public class NoAutoCommands { }
#endif

    public class McpPlugin : IExtensionApplication
    {
        // Bump between rebuilds to verify hot-reload picks up the new assembly.
        private const string Version = "v2-slice2";

        // Static so the session survives across multiple [CommandMethod] invocations.
        // DevReload's CommandRegistrar creates a new McpPlugin instance per non-static
        // command call, so instance-level state would be discarded.
        private static ScriptSession? _session;
        private static ScriptSession Session => _session ??= new ScriptSession();

        public void Initialize()
        {
            Log($"Initialize() {Version}");
        }

        public void Terminate()
        {
            _session = null;
            Log($"Terminate() {Version}");
        }

        [CommandMethod("ACDMCP_PING")]
        public static void Ping()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nACD-MCP pong {Version} @ {System.DateTime.Now:HH:mm:ss}\n");
        }

        // Throwaway harness for Slice 2. Reads C# from the clipboard and runs it
        // through the script session. Replaced by the named-pipe listener in Slice 3.
        [CommandMethod("ACDMCP_EVAL")]
        public static void Eval()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed is null) return;

            string code;
            try
            {
                code = System.Windows.Forms.Clipboard.GetText();
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ACD-MCP] Clipboard read failed: {ex.Message}\n");
                return;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                ed.WriteMessage("\n[ACD-MCP] Clipboard is empty.\n");
                return;
            }

            ed.WriteMessage($"\n[ACD-MCP] Eval ({code.Length} chars from clipboard)...\n");

            // We're on AutoCAD's main thread inside a [CommandMethod] — exactly where
            // AutoCAD APIs need to run. Blocking with GetResult is safe because
            // CSharpScript uses ConfigureAwait(false) internally; continuations land
            // on the threadpool, not back on this thread.
            var result = Session.ExecuteAsync(code).GetAwaiter().GetResult();
            WriteResult(ed, result);
        }

        [CommandMethod("ACDMCP_RESET")]
        public static void ResetSession()
        {
            _session?.Reset();
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\n[ACD-MCP] Script session reset.\n");
        }

        private static void WriteResult(Editor ed, ExecuteResult result)
        {
            if (result.Success)
            {
                ed.WriteMessage($"\n[ACD-MCP] OK ({result.ElapsedMs} ms)");
                if (result.ReturnValueRepr is not null)
                    ed.WriteMessage($"\n=> {result.ReturnValueRepr}");
                ed.WriteMessage("\n");
            }
            else
            {
                ed.WriteMessage($"\n[ACD-MCP] ERROR ({result.ElapsedMs} ms)");
                foreach (var d in result.Diagnostics)
                    ed.WriteMessage($"\n  {d.Severity} ({d.Line ?? 0},{d.Column ?? 0}): {d.Message}");
                if (result.Stderr is not null)
                    ed.WriteMessage($"\n  {result.Stderr}");
                ed.WriteMessage("\n");
            }
        }

        private static void Log(string msg)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n[ACD-MCP] {msg} @ {System.DateTime.Now:HH:mm:ss}\n");
        }
    }
}
