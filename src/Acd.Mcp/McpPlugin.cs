using System.Diagnostics;
using System.Text.Json;
using Acd.Mcp.Api;
using Acd.Mcp.Batch;
using Acd.Mcp.Batch.Runtime;
using Acd.Mcp.Data;
using Acd.Mcp.Pipe;
using Acd.Mcp.Scripting;
using Acd.Mcp.Serialization;
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
        private const string Version = "v17-reverted-keep-g2-g3";

        // Static so they survive across DevReload's per-call activator (it creates a
        // fresh McpPlugin instance for each non-static [CommandMethod] call).
        private static ScriptSession? _session;
        private static AcadExecutor? _executor;
        private static ExecutionLog? _log;
        private static PipeListener? _listener;
        private static ReplPaletteSet? _palette;
        private static SynchronizationContext? _mainSync;
        private static BatchExecutor? _batchExecutor;
        private static BatchRpcHandler? _batchRpc;

        // Shared script-store + per-flavor ScriptEditor instances.
        // SavedScriptStore is filesystem-backed and stateless — a single
        // instance routes Batch / Repl reads to the correct subfolder
        // via the flavor parameter. The ScriptEditor owns its
        // EditorBuffer (mirror file) lifetime; no separate field is
        // needed at the plugin level. Phase 1: only BATCH is wired
        // here; the REPL editor is added in Phase 2.
        private static SavedScriptStore? _scriptStore;
        private static ScriptEditor? _batchScriptEditor;

        // DTO graph. Built once in TryEnsureCore; the same registry feeds both
        // the JsonSerializerOptions (passed to ScriptSession) and the loader
        // (called for ReloadAll / Refresh). _dtoDiagnostics is shared with
        // the DtoConverter so the $unsupported marker can carry compile-
        // error context inline, and with _dtoRpc so the agent can also
        // query the full list via acd-mcp://dto-system/diagnostics.
        //
        // _dataProviderApi is the same instance threaded through both DTO
        // bodies (via DtoRegistrationApi.DataProvider) and the REPL (via
        // AcadGlobals.Acd.DataProvider) — one composite, one place where
        // the read-all / try-read delegates resolve to the live provider
        // chain.
        private static DtoRegistry? _dtoRegistry;
        private static DtoLoader? _dtoLoader;
        private static DtoDiagnostics? _dtoDiagnostics;
        private static DtoRpcHandler? _dtoRpc;
        private static JsonSerializerOptions? _dtoJsonOptions;
        private static DtoDataProviderApi? _dataProviderApi;

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

                // Force-load the AECC PropertyData assembly so PropertySetProvider's
                // type probe (run when EnsureDtoGraph builds the composite) can find
                // the AECC types. AutoCAD lazy-loads AecPropDataMgd; without this
                // nudge, ACD-MCP boots before AECC and PropertySetProvider self-
                // disables ("vanilla AutoCAD"), turning the plugin's headline
                // Civil 3D feature into block-attributes-only. See G2 in the v2
                // crash-test journal.
                //
                // Type.GetType("...PropertyDataServices, AecPropDataMgd") returns
                // null on vanilla AutoCAD — that's the genuine "vanilla" case and
                // PropertySetProvider correctly reports it. On Civil 3D / Map 3D /
                // MEP this call is sufficient to bring the AECC managed wrapper
                // into the AppDomain.
                SafeBoundary.Run("McpPlugin.Initialize/probe-AECC", () =>
                {
                    Type.GetType(
                        "Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices, AecPropDataMgd",
                        throwOnError: false);
                });
            });
        }

        public void Terminate()
        {
            // Terminate MUST NOT throw — DevReload's unload path needs to complete
            // for the ALC to actually unload. Each tear-down step is isolated so
            // one failure cannot skip the next.
            //
            // Close() is only meaningful when the palette is currently visible —
            // and Autodesk's base PaletteSet has been observed to NRE when its
            // wrapped Window is half-initialised (palette never shown, or user
            // manually closed it). The Visible guard avoids the spurious log
            // entry; Dispose() still handles full teardown either way.
            SafeBoundary.Run("McpPlugin.Terminate/palette.Close", () =>
            {
                if (_palette is { Visible: true }) _palette.Close();
            });
            SafeBoundary.Run("McpPlugin.Terminate/palette.Dispose", () => _palette?.Dispose());
            _palette = null;

            SafeBoundary.Run("McpPlugin.Terminate/listener.Stop",    () => _listener?.Stop());
            SafeBoundary.Run("McpPlugin.Terminate/listener.Dispose", () => _listener?.Dispose());
            _listener = null;

            SafeBoundary.Run("McpPlugin.Terminate/batchExecutor.Dispose", () => _batchExecutor?.Dispose());
            _batchExecutor = null;
            _batchRpc = null;

            // ScriptEditor owns the EditorBuffer (BatchExecutor.Dispose
            // intentionally does NOT touch them). Disposing the editor
            // flushes the mirror's pending write and tears down its
            // debounce timer. SavedScriptStore is stateless; nothing
            // to dispose.
            SafeBoundary.Run("McpPlugin.Terminate/batchScriptEditor.Dispose",
                () => _batchScriptEditor?.Dispose());
            _batchScriptEditor = null;
            _scriptStore = null;

            _executor = null;
            _log = null;
            _session = null;
            _mainSync = null;
            _dtoLoader = null;
            _dtoRegistry = null;
            _dtoDiagnostics = null;
            _dtoRpc = null;
            _dtoJsonOptions = null;
            _dataProviderApi = null;
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

            // The batch RPC handler needs a IBatchUiState provider; until the
            // palette is opened we fall back to an empty stub so agent calls
            // that depend on UI state fail loudly with a clear message.
            _listener ??= new PipeListener(_executor!, ExtraRpcMethodHandler);
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

            _palette ??= new ReplPaletteSet(_executor!, _session!, _log!, _batchExecutor!);
            // The BATCH tab's view-model implements IBatchUiState; once the
            // palette exists, route the batch RPC handler at it so the agent
            // can query the user's current folder + mask + file selection.
            _batchRpc = new BatchRpcHandler(_batchExecutor!, _palette.BatchViewModel);
            _palette.Visible = true;
        });

        // Listener-side method dispatch. Two prefixes are handled here:
        //   batch.* — routed to _batchRpc (requires the palette to be open).
        //   dto.*   — routed to _dtoRpc   (always available once the DTO
        //             graph is built; doesn't need the palette).
        private static Task<object?> ExtraRpcMethodHandler(string method, System.Text.Json.JsonElement parameters, System.Threading.CancellationToken ct)
        {
            if (method.StartsWith("batch."))
            {
                if (_batchRpc is null)
                    throw new InvalidOperationException(
                        "BATCH palette is not open. Run ACDMCP_PALETTE inside AutoCAD first.");
                return _batchRpc.DispatchAsync(method, parameters, ct);
            }
            if (method.StartsWith("dto."))
            {
                if (_dtoRpc is null)
                    throw new InvalidOperationException(
                        "DTO graph is not initialised yet. Run ACDMCP_START inside AutoCAD first.");
                return _dtoRpc.DispatchAsync(method, parameters, ct);
            }
            return Task.FromResult<object?>(null);
        }

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
                EnsureDtoGraph();
                _log ??= new ExecutionLog();
                if (_session is null)
                {
                    // REPL globals get the same data-provider façade DTO
                    // bodies use, so `Acd.DataProvider.ReadAll(entity)`
                    // resolves the same way in both contexts. See
                    // AcdReplApi for the why.
                    var replGlobals = new AcadGlobals(new AcdReplApi(_dataProviderApi!));
                    _session = new ScriptSession(replGlobals, _dtoJsonOptions);
                }
                _executor ??= new AcadExecutor(_mainSync, _session, _log);

                // ScriptEditor wiring: one shared store; per-flavor
                // editor (which owns its EditorBuffer). The BATCH editor
                // is owned at the plugin level so the same instance is
                // seen by the palette, the RPC handlers, and Terminate.
                _scriptStore ??= new SavedScriptStore();
                _batchScriptEditor ??= new ScriptEditor(
                    ScriptFlavor.Batch, _scriptStore, new EditorBuffer());
                _batchExecutor ??= new BatchExecutor(_batchScriptEditor);
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

        // Build the DTO machinery once per process lifetime. Idempotent; safe to
        // call from every TryEnsureCore. Seeds %LOCALAPPDATA% from the embedded
        // resource set, loads both folders into the registry, hands the
        // resulting JsonSerializerOptions to ScriptSession.
        private static void EnsureDtoGraph()
        {
            if (_dtoJsonOptions is not null) return;

            DtoPaths.EnsureFolders();
            DtoSystemSeeder.Seed();

            _dtoRegistry = new DtoRegistry();
            _dtoDiagnostics = new DtoDiagnostics();
            var providers = EntityDataProviders.CreateDefault();
            // Adapt the Outcome-based internal provider to the delegate pair
            // DtoDataProviderApi takes. The collapse of Outcome → null at
            // this boundary is intentional: the public API surface keeps
            // Acd.Mcp.Api free of any Acd.Mcp.Batch type, so the assembly
            // doesn't need a cross-ALC reference. The richer Outcome
            // remains useful inside the composite for chained-provider
            // error propagation; collapsing it for the DTO/REPL boundary
            // matches the existing contract.
            _dataProviderApi = new DtoDataProviderApi(
                readAll: (e, tx) => providers.ReadAll(e, tx),
                tryRead: (e, tx, k) =>
                    providers.TryRead(e, tx, k) is Outcome<string>.Pass p ? p.Value : null);
            _dtoLoader = new DtoLoader(_dtoRegistry, _dataProviderApi, _dtoDiagnostics);

            var reload = new DtoReloadTrigger(_dtoLoader);
            _dtoJsonOptions = AcadDtoOptions.Build(_dtoRegistry, reload, _dtoDiagnostics);
            _dtoRpc = new DtoRpcHandler(_dtoDiagnostics);

            _dtoLoader.ReloadAll();

            SafeBoundary.Info("EnsureDtoGraph",
                $"Registered {_dtoRegistry.RegisteredTypes.Count} DTO types.");
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
