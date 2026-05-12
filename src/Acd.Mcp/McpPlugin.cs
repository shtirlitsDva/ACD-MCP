using System.Diagnostics;
using Acd.Mcp.Pipe;
using Acd.Mcp.Scripting;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using SynchronizationContext = System.Threading.SynchronizationContext;

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
        private const string Version = "v3-slice3";

        // Static so they survive across DevReload's per-call activator (it creates a
        // fresh McpPlugin instance for each non-static [CommandMethod] call).
        private static ScriptSession? _session;
        private static AcadExecutor? _executor;
        private static PipeListener? _listener;
        private static SynchronizationContext? _mainSync;

        public void Initialize()
        {
            // Initialize() runs on AutoCAD's main thread, which has a
            // WindowsFormsSynchronizationContext installed. Capture it now so the
            // pipe listener (on a threadpool thread) can later Post() snippet
            // execution back onto this thread.
            _mainSync = SynchronizationContext.Current
                ?? throw new InvalidOperationException(
                    "No SynchronizationContext on main thread — cannot marshal pipe requests back.");

            Log($"Initialize() {Version}");
        }

        public void Terminate()
        {
            try { _listener?.Stop(); } catch { }
            _listener?.Dispose();
            _listener = null;
            _executor = null;
            _session = null;
            _mainSync = null;
            Log($"Terminate() {Version}");
        }

        [CommandMethod("ACDMCP_PING")]
        public static void Ping()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\nACD-MCP pong {Version} @ {DateTime.Now:HH:mm:ss}\n");
        }

        [CommandMethod("ACDMCP_START")]
        public static void Start()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed is null) return;

            if (_mainSync is null)
            {
                ed.WriteMessage("\n[ACD-MCP] Cannot start: SynchronizationContext was not captured at Initialize().\n");
                return;
            }

            _session ??= new ScriptSession();
            _executor ??= new AcadExecutor(_mainSync, _session);

            if (_listener is { IsRunning: true })
            {
                ed.WriteMessage($"\n[ACD-MCP] Already running on pipe '{_listener.PipeName}'.\n");
                return;
            }

            _listener ??= new PipeListener(_executor);
            _listener.Start();
            ed.WriteMessage($"\n[ACD-MCP] Listening on named pipe '{_listener.PipeName}'.\n");
        }

        [CommandMethod("ACDMCP_STOP")]
        public static void Stop()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (_listener is null || !_listener.IsRunning)
            {
                ed?.WriteMessage("\n[ACD-MCP] Listener is not running.\n");
                return;
            }
            _listener.Stop();
            ed?.WriteMessage("\n[ACD-MCP] Listener stopped.\n");
        }

        [CommandMethod("ACDMCP_STATUS")]
        public static void Status()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed is null) return;
            ed.WriteMessage($"\n[ACD-MCP] {Version}");
            ed.WriteMessage($"\n  PID:        {Process.GetCurrentProcess().Id}");
            ed.WriteMessage($"\n  Listener:   {(_listener is { IsRunning: true } ? $"running on '{_listener.PipeName}'" : "stopped")}");
            ed.WriteMessage($"\n  Session:    {(_session is null ? "uninitialized" : "ready")}");
            ed.WriteMessage("\n");
        }

        [CommandMethod("ACDMCP_RESET")]
        public static void ResetSession()
        {
            _session?.Reset();
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\n[ACD-MCP] Script session reset.\n");
        }

        private static void Log(string msg)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage($"\n[ACD-MCP] {msg} @ {DateTime.Now:HH:mm:ss}\n");
        }
    }
}
