using System.Diagnostics;
using System.Text.Json;
using Acd.Mcp.Api;
using Acd.Mcp.Batch;
using Acd.Mcp.Batch.Runtime;
using Acd.Mcp.Data;
using Acd.Mcp.Pipe;
using Acd.Mcp.Script;
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
        private const string Version = "v29-get-selection";

        // Static so they survive across DevReload's per-call activator (it creates a
        // fresh McpPlugin instance for each non-static [CommandMethod] call).
        private static ScriptSession? _session;
        private static AcadExecutor? _executor;
        private static ExecutionLog? _log;
        private static PipeListener? _listener;
        private static PaletteHost? _paletteHost;
        private static StatusRpcHandler? _statusRpc;
        private static SynchronizationContext? _mainSync;
        private static BatchExecutor? _batchExecutor;
        private static BatchRpcHandler? _batchRpc;

        // Owns every cleanup step (event unsubscribes + IDisposable
        // teardowns). Created in Initialize, drained in Terminate. Keeps
        // Terminate small and co-locates registration with construction so
        // a new component cannot easily forget its unhook.
        private static ResourceManager? _resources;

        // Shared script-store + per-flavor ScriptEditor instances.
        // SavedScriptStore is filesystem-backed and stateless — a single
        // instance routes Batch / Script reads to the correct subfolder
        // via the flavor parameter. Each ScriptEditor owns its
        // EditorBuffer (mirror file) lifetime; no separate field is
        // needed at the plugin level. Both editors are created at
        // TryEnsureCore so the palette and the script.* RPC handler
        // can see the same instance regardless of which is set up first.
        private static SavedScriptStore? _scriptStore;
        private static ScriptEditor? _batchEditor;
        private static ScriptEditor? _scriptEditor;
        private static ScriptRpcHandler? _scriptRpc;

        // Path of the SCRIPT editor's mirror file. BATCH editor uses
        // buffer-batch.csx (its EditorBuffer.DefaultPath) in the same
        // folder; the buffer-<flavor> naming keeps the two mirror files
        // sorted adjacently in Explorer.
        private static string ScriptMirrorPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Acd.Mcp",
            "buffer-script.csx");

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
            _resources = new ResourceManager(SafeBoundary.Run);

            // TaskScheduler.UnobservedTaskException is a process-lifetime
            // event in the default ALC; a lambda subscriber would pin our
            // collectible ALC forever. The named-handler hook + matching
            // unhook (registered with the ResourceManager) lets Terminate
            // release that pin.
            _resources.RegisterEvent("SafeBoundary.ProcessHooks",
                subscribe: SafeBoundary.HookProcessHooks,
                unsubscribe: SafeBoundary.UnhookProcessHooks);

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

                // Auto-start: open the named pipe as soon as the command
                // loop is idle. Was DEBUG-only until fragility-fix v2 —
                // promoted to RELEASE because requiring users to type
                // ACDMCP_START after every AutoCAD launch was a silent
                // tax with no benefit. Users who genuinely want the
                // manual control can opt out via the config file (see
                // ShouldAutoStart for the path + format).
                //
                // Hook removes itself on first fire. Terminate's
                // ResourceManager also -= to cover the "plugin unloaded
                // before Idle ever fired" case (Application.Idle is
                // process-lifetime in the default ALC, so a leftover
                // subscription would pin our collectible ALC).
                if (ShouldAutoStart())
                {
                    _resources!.RegisterEvent("Application.Idle/AutoStart",
                        subscribe:   () => Application.Idle += AutoStartOnceOnIdle,
                        unsubscribe: () => Application.Idle -= AutoStartOnceOnIdle);
                }
            });
        }

        private static void AutoStartOnceOnIdle(object? sender, EventArgs e)
        {
            Application.Idle -= AutoStartOnceOnIdle;
            SafeBoundary.Run("McpPlugin.AutoStartOnceOnIdle", () => Start());
        }

        // Reads %LOCALAPPDATA%\Acd.Mcp\config.json for an `auto_start`
        // bool. Absence = true (auto-start). Failure to parse =
        // true (autostart is the safe default; corrupt config shouldn't
        // accidentally disable the plugin's main entry point).
        //
        // Format is intentionally trivial — one bool field — to keep
        // the surface area small. If more knobs land later, this becomes
        // a typed Options record; YAGNI for now.
        private static bool ShouldAutoStart()
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Acd.Mcp", "config.json");
                if (!System.IO.File.Exists(path)) return true;

                var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("auto_start", out var v) &&
                    v.ValueKind is System.Text.Json.JsonValueKind.False)
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                SafeBoundary.Info("ShouldAutoStart",
                    $"Failed to read config.json ({ex.GetType().Name}: {ex.Message}); defaulting to auto-start.");
                return true;
            }
        }

        public void Terminate()
        {
            // Terminate MUST NOT throw — DevReload's unload path needs to
            // complete for the ALC to actually unload. ResourceManager.Dispose
            // wraps each registered step in SafeBoundary.Run, so a single
            // failure cannot skip the rest. Static fields are nulled so the
            // next DevReload cycle starts from a clean slate.
            // Two-phase teardown: ResourceManager.Dispose runs every
            // registered Dispose/Action in LIFO; nothing inside those
            // steps reaches back to read the McpPlugin static fields,
            // so the second-phase null block is safe to run after.
            // Fields are nulled here (not via RegisterAction) so the
            // teardown order is in one place and reviewers see at a
            // glance every static the next DevReload cycle inherits.
            _resources?.Dispose();
            _resources = null;

            _paletteHost = null;
            _statusRpc = null;
            _listener = null;
            _batchExecutor = null;
            _batchRpc = null;
            _batchEditor = null;
            _scriptEditor = null;
            _scriptRpc = null;
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

            // The batch + repl RPC handlers are wired at ACDMCP_PALETTE time
            // — both throw a clear "palette is not open" error from the
            // pipe dispatcher (ExtraRpcMethodHandler) until then, so agent
            // calls fail loudly with actionable guidance instead of
            // silently succeeding against a non-existent UI.
            if (_listener is null)
            {
                _listener = new PipeListener(_executor!, ExtraRpcMethodHandler);
                // Two steps: Stop() then Dispose(). Each in its own
                // SafeBoundary so a Stop failure can't skip the Dispose.
                // Register Dispose first so LIFO runs Stop → Dispose.
                _resources!.Register("listener.Dispose", _listener);
                _resources!.RegisterAction("listener.Stop", () => _listener?.Stop());
            }
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
            ed.WriteMessage($"\n  Palette:    {DescribePaletteStatus()}");
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

            // After phase-1 of the fragility fix, the palette is no
            // longer where RPC handlers are constructed. They are wired
            // in TryEnsureCore (via the PaletteHost), so this command
            // is now exactly what its name implies: show the palette.
            // The host marshals the create/show to this same main
            // thread; both ACDMCP_PALETTE and agent-driven EnsureVisible
            // arrive at GetOrCreateOnMainThread in one path.
            var palette = _paletteHost!.GetOrCreateOnMainThread();
            palette.Visible = true;
        });

        // Listener-side method dispatch. After phase-1 of the fragility
        // fix all RPC handlers are wired in TryEnsureCore — the palette
        // gating that previously broke every AutoCAD restart is gone.
        // The remaining null-checks defend against the listener firing
        // before TryEnsureCore (currently impossible — ACDMCP_START
        // calls TryEnsureCore before binding the listener — but the
        // assert keeps the contract explicit).
        //
        //   script.* — routed to _scriptRpc (auto-opens palette on
        //              propose; pure editor ops succeed regardless).
        //   batch.*  — routed to _batchRpc  (auto-opens palette on
        //              propose; ops that read user-owned UI state
        //              surface PALETTE_CLOSED when the palette is shut).
        //   dto.*    — routed to _dtoRpc    (always available once the
        //              DTO graph is built; doesn't touch the palette).
        private static Task<object?> ExtraRpcMethodHandler(string method, System.Text.Json.JsonElement parameters, System.Threading.CancellationToken ct)
        {
            if (method.StartsWith("script."))
            {
                if (_scriptRpc is null)
                    throw new InvalidOperationException(
                        "PLUGIN_NOT_INITIALIZED: SCRIPT RPC handler is not wired. " +
                        "TryEnsureCore did not complete — check the AutoCAD log.");
                return _scriptRpc.DispatchAsync(method, parameters, ct);
            }
            if (method.StartsWith("batch."))
            {
                if (_batchRpc is null)
                    throw new InvalidOperationException(
                        "PLUGIN_NOT_INITIALIZED: BATCH RPC handler is not wired. " +
                        "TryEnsureCore did not complete — check the AutoCAD log.");
                return _batchRpc.DispatchAsync(method, parameters, ct);
            }
            if (method.StartsWith("dto."))
            {
                if (_dtoRpc is null)
                    throw new InvalidOperationException(
                        "DTO_NOT_READY: DTO graph is not initialised yet. " +
                        "Run ACDMCP_START inside AutoCAD first.");
                return _dtoRpc.DispatchAsync(method, parameters, ct);
            }
            if (method.StartsWith("acdmcp."))
            {
                // Status surface intentionally degrades gracefully — if
                // TryEnsureCore hasn't been called, return a minimal
                // snapshot rather than throwing, so the agent can still
                // diagnose a half-initialised plugin.
                _statusRpc ??= new StatusRpcHandler(BuildStatusSnapshot);
                return _statusRpc.DispatchAsync(method, parameters, ct);
            }
            return Task.FromResult<object?>(null);
        }

        // Single source of truth for "what works right now." Mirrors
        // exactly what the agent's skill / status resource sees and
        // what ACDMCP_STATUS prints, so the user's diagnostic and the
        // agent's diagnostic stay aligned.
        internal static StatusSnapshot BuildStatusSnapshot()
        {
            bool pipeUp = _listener is { IsRunning: true };
            bool palette = _paletteHost?.IsOpen ?? false;

            CapabilityState ready() => new("ready", null);
            CapabilityState unavailable(string r) => new("unavailable", r);
            CapabilityState degraded(string r) => new("degraded", r);

            return new StatusSnapshot(
                version: Version,
                pid: Process.GetCurrentProcess().Id,
                pipe: pipeUp ? $"acd-mcp-{Process.GetCurrentProcess().Id}" : "<not listening>",
                script_execute:      pipeUp ? ready() : unavailable("PIPE_NOT_LISTENING"),
                script_propose:      pipeUp ? ready() : unavailable("PIPE_NOT_LISTENING"),
                batch_propose:       pipeUp ? ready() : unavailable("PIPE_NOT_LISTENING"),
                batch_run_test:      pipeUp ? (palette ? ready() : degraded("PALETTE_CLOSED")) : unavailable("PIPE_NOT_LISTENING"),
                batch_list_files:    pipeUp ? (palette ? ready() : degraded("PALETTE_CLOSED")) : unavailable("PIPE_NOT_LISTENING"),
                dto:                 _dtoRpc is null ? unavailable("DTO_NOT_READY") : ready());
        }

        // Used by ACDMCP_STATUS to describe palette state without
        // duplicating the host's null-guard logic.
        private static string DescribePaletteStatus()
        {
            if (_paletteHost is null) return "not initialised";
            if (_paletteHost.Palette is null) return "not opened";
            return _paletteHost.IsOpen ? "visible" : "hidden";
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
                    // Registered so Terminate releases the accumulated
                    // ScriptState chain (variable bindings, captured trees)
                    // alongside the other long-lived resources. Roslyn-emitted
                    // submission assemblies still live in the default ALC for
                    // process lifetime — that's a documented Roslyn property,
                    // not our leak.
                    _resources!.Register("session", _session);
                }
                _executor ??= new AcadExecutor(_mainSync, _session, _log);

                // ScriptEditor wiring: one shared store; per-flavor
                // editor (each owns its EditorBuffer). Both editors are
                // owned at the plugin level so the same instances are
                // seen by the palette, the RPC handlers, and Terminate.
                // REPL gets a sibling mirror path so the agent's
                // "read-the-mirror-before-proposing" convention works
                // independently for each flavor.
                //
                // Each ScriptEditor owns its EditorBuffer (the mirror file +
                // debounce timer). Disposing the editor flushes the pending
                // write and tears down the timer. SavedScriptStore is
                // filesystem-backed and stateless — nothing to dispose.
                _scriptStore ??= new SavedScriptStore();
                if (_batchEditor is null)
                {
                    _batchEditor = new ScriptEditor(
                        ScriptFlavor.Batch, _scriptStore, new EditorBuffer());
                    _resources!.Register("batchEditor", _batchEditor);
                }
                if (_scriptEditor is null)
                {
                    _scriptEditor = new ScriptEditor(
                        ScriptFlavor.Script, _scriptStore, new EditorBuffer(ScriptMirrorPath));
                    _resources!.Register("scriptEditor", _scriptEditor);
                }
                if (_batchExecutor is null)
                {
                    _batchExecutor = new BatchExecutor(_batchEditor);
                    _resources!.Register("batchExecutor", _batchExecutor);
                }

                // PaletteHost is the lazy-binding layer for the SCRIPT
                // and BATCH palette. Constructed here (not in
                // ShowPalette) so RPC handlers can be wired immediately
                // — the palette itself is materialised on first need.
                // See docs/review/fragility-review-2026-05-18.md
                // finding-1 for why this matters.
                if (_paletteHost is null)
                {
                    _paletteHost = new PaletteHost(
                        _mainSync,
                        factory: () =>
                        {
                            var p = new ScriptPaletteSet(_executor!, _session!, _log!, _batchExecutor!, _scriptEditor!);
                            // Two steps in LIFO: register Dispose first
                            // so Close runs before Dispose on tear-down.
                            // The Visible guard preserves the original
                            // Terminate behaviour — Autodesk's base
                            // PaletteSet has been observed to NRE on
                            // Close() when its wrapped Window is
                            // half-initialised.
                            _resources!.Register("palette.Dispose", p);
                            _resources!.RegisterAction("palette.Close", () =>
                            {
                                if (p is { Visible: true }) p.Close();
                            });
                            return p;
                        });
                }

                // RPC handlers are wired here — NOT inside ShowPalette —
                // so an agent call that arrives before the palette is
                // ever opened succeeds and triggers EnsureVisible(),
                // instead of failing with the now-removed "palette is
                // not open" error.
                _scriptRpc ??= new ScriptRpcHandler(_scriptEditor!, _paletteHost, _mainSync);
                _batchRpc  ??= new BatchRpcHandler(_batchExecutor!, _paletteHost);
                _statusRpc ??= new StatusRpcHandler(BuildStatusSnapshot);

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
            // Belt-and-suspenders: nulling the static fields in Terminate is
            // sufficient to make the registry instances unreachable, but an
            // explicit Clear() drops every projection delegate (and any
            // captured plugin-ALC closures inside them) right away rather
            // than waiting for GC to find the right ref count.
            _resources!.RegisterAction("dtoRegistry.Clear", () => _dtoRegistry?.Clear());
            _resources!.RegisterAction("dtoDiagnostics.Clear", () => _dtoDiagnostics?.Clear());
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
