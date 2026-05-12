using System.Diagnostics;
using Acd.Mcp.Pipe;
using Acd.Mcp.Scripting;
using Acd.Mcp.Ui;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;
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
        private const string Version = "v8-globals-fix";

        // Static so they survive across DevReload's per-call activator (it creates a
        // fresh McpPlugin instance for each non-static [CommandMethod] call).
        private static ScriptSession? _session;
        private static AcadExecutor? _executor;
        private static ExecutionLog? _log;
        private static PipeListener? _listener;
        private static ReplPaletteSet? _palette;
        private static SynchronizationContext? _mainSync;

        public void Initialize()
        {
            // Initialize MUST NOT throw — DevReload (and AutoCAD's autoload path)
            // handle plugin-init failure poorly. We capture everything and report.
            SafeBoundary.EnsureInitialized();
            SafeBoundary.Run("McpPlugin.Initialize", () =>
            {
                // Initialize() runs on AutoCAD's main thread, which has a
                // WindowsFormsSynchronizationContext installed. Capture it now so the
                // pipe listener (on a threadpool thread) can later Post() snippet
                // execution back onto this thread. If for some reason it's null,
                // we log and leave _mainSync null — EnsureCore() will then report
                // "not initialized" the first time someone tries to use it.
                _mainSync = SynchronizationContext.Current;
                if (_mainSync is null)
                    SafeBoundary.Info("Initialize", "WARNING: SynchronizationContext.Current is null.");

                SafeBoundary.Info("Initialize", $"{Version} (PID {Process.GetCurrentProcess().Id}, log: {SafeBoundary.LogFile})");
                EditorMessage($"[ACD-MCP] Initialize() {Version} @ {DateTime.Now:HH:mm:ss}");
            });
        }

        public void Terminate()
        {
            // Terminate MUST NOT throw — DevReload's unload path needs to complete
            // for the ALC to actually unload. Each tear-down step is isolated so
            // one failure cannot skip the next.
            SafeBoundary.Run("McpPlugin.Terminate/palette.Close",  () => _palette?.Close());
            SafeBoundary.Run("McpPlugin.Terminate/palette.Dispose", () => _palette?.Dispose());
            _palette = null;

            SafeBoundary.Run("McpPlugin.Terminate/listener.Stop",    () => _listener?.Stop());
            SafeBoundary.Run("McpPlugin.Terminate/listener.Dispose", () => _listener?.Dispose());
            _listener = null;

            _executor = null;
            _log = null;
            _session = null;
            _mainSync = null;
            SafeBoundary.Info("Terminate", $"{Version}");
            SafeBoundary.Run("McpPlugin.Terminate/echo", () => EditorMessage($"[ACD-MCP] Terminate() {Version}"));
        }

        [CommandMethod("ACDMCP_PING")]
        public static void Ping() => SafeBoundary.Run("ACDMCP_PING", () =>
        {
            EditorMessage($"ACD-MCP pong {Version} @ {DateTime.Now:HH:mm:ss}");
        });

        [CommandMethod("ACDMCP_START")]
        public static void Start() => SafeBoundary.Run("ACDMCP_START", () =>
        {
            if (!TryEnsureCore(out var reason))
            {
                EditorMessage($"[ACD-MCP] {reason}");
                return;
            }

            if (_listener is { IsRunning: true })
            {
                EditorMessage($"[ACD-MCP] Already running on pipe '{_listener.PipeName}'.");
                return;
            }

            _listener ??= new PipeListener(_executor!);
            _listener.Start();
            EditorMessage($"[ACD-MCP] Listening on named pipe '{_listener.PipeName}'.");
        });

        [CommandMethod("ACDMCP_STOP")]
        public static void Stop() => SafeBoundary.Run("ACDMCP_STOP", () =>
        {
            if (_listener is null || !_listener.IsRunning)
            {
                EditorMessage("[ACD-MCP] Listener is not running.");
                return;
            }
            _listener.Stop();
            EditorMessage("[ACD-MCP] Listener stopped.");
        });

        [CommandMethod("ACDMCP_STATUS")]
        public static void Status() => SafeBoundary.Run("ACDMCP_STATUS", () =>
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed is null) return;
            ed.WriteMessage($"\n[ACD-MCP] {Version}");
            ed.WriteMessage($"\n  PID:        {Process.GetCurrentProcess().Id}");
            ed.WriteMessage($"\n  Listener:   {(_listener is { IsRunning: true } ? $"running on '{_listener.PipeName}'" : "stopped")}");
            ed.WriteMessage($"\n  Session:    {(_session is null ? "uninitialized" : "ready")}");
            ed.WriteMessage($"\n  Palette:    {(_palette is null ? "not opened" : (_palette.Visible ? "visible" : "hidden"))}");
            ed.WriteMessage($"\n  Log file:   {SafeBoundary.LogFile ?? "<unset>"}");
            ed.WriteMessage("\n");
        });

        [CommandMethod("ACDMCP_RESET")]
        public static void ResetSession() => SafeBoundary.Run("ACDMCP_RESET", () =>
        {
            _executor?.Reset();
            EditorMessage("[ACD-MCP] Script session reset.");
        });

        [CommandMethod("ACDMCP_PALETTE")]
        public static void ShowPalette() => SafeBoundary.Run("ACDMCP_PALETTE", () =>
        {
            if (!TryEnsureCore(out var reason))
            {
                EditorMessage($"[ACD-MCP] {reason}");
                return;
            }

            _palette ??= new ReplPaletteSet(_executor!, _session!, _log!);
            _palette.Visible = true;
        });

        // Lazy-init the in-process core. Returns false with a human-readable
        // reason instead of throwing, so commands can surface clean messages.
        private static bool TryEnsureCore(out string reason)
        {
            if (_mainSync is null)
            {
                reason = "Plugin not initialized: SynchronizationContext was not captured at Initialize().";
                return false;
            }

            try
            {
                _log ??= new ExecutionLog();
                _session ??= new ScriptSession();
                _executor ??= new AcadExecutor(_mainSync, _session, _log);
                reason = "";
                return true;
            }
            catch (Exception ex)
            {
                SafeBoundary.Report(ex, "TryEnsureCore");
                reason = $"Core initialization failed: {ex.Message}. See log: {SafeBoundary.LogFile}";
                return false;
            }
        }

        private static void EditorMessage(string msg)
        {
            try
            {
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage("\n" + msg + "\n");
            }
            catch { /* writing to a closing doc shouldn't propagate */ }
        }
    }
}
