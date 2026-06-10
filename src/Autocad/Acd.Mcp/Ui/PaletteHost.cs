using System;
using System.Threading;
using Acd.Mcp.Batch.Runtime;

namespace Acd.Mcp.Ui
{
    // Single point through which everything in the plugin reaches the
    // palette. Replaces the previous pattern where ACDMCP_PALETTE was
    // the only entry point that could construct the palette, and the
    // RPC handlers had to fail if the user hadn't typed that command yet.
    //
    // Lifetime:
    //   - Constructed in TryEnsureCore, so it's wired as soon as the
    //     plugin's core is ready (well before the listener starts).
    //   - The actual ScriptPaletteSet is created on first EnsureVisible
    //     or first ACDMCP_PALETTE — whichever wins.
    //   - The factory + sync context are captured at construction; the
    //     host never reaches back into McpPlugin statics, so it survives
    //     unit substitution.
    //
    // Thread safety: EnsureVisible marshals to the main thread via the
    // captured SynchronizationContext. CurrentBatchUiState and IsOpen
    // are simple reads — the palette field is assigned only once on the
    // main thread, so a torn read can't happen.
    internal sealed class PaletteHost : IPaletteHost
    {
        private readonly SynchronizationContext _mainSync;
        private readonly Func<ScriptPaletteSet> _factory;
        private ScriptPaletteSet? _palette;

        public PaletteHost(SynchronizationContext mainSync, Func<ScriptPaletteSet> factory)
        {
            _mainSync = mainSync;
            _factory = factory;
        }

        public bool IsOpen => _palette is { Visible: true };

        public IBatchUiState? CurrentBatchUiState => _palette?.BatchViewModel;

        public ScriptPaletteSet? Palette => _palette;

        // Called from the ACDMCP_PALETTE command. The command path is
        // already on the main thread, so we can construct + show directly.
        // The host records the reference so RPC handlers see the new
        // palette on their next dispatch.
        public ScriptPaletteSet GetOrCreateOnMainThread()
        {
            _palette ??= _factory();
            return _palette;
        }

        // Called from RPC handler threads when the user hasn't opened
        // the palette themselves. Posts a "create + show" task to the
        // main thread and returns immediately — the agent's call shouldn't
        // block on UI work. If the palette already exists and is visible,
        // this is a no-op fast path.
        public void EnsureVisible(CancellationToken ct = default)
        {
            if (IsOpen) return;

            // Post (fire-and-forget) is intentional: agent calls return
            // their staged proposal immediately; the user sees the palette
            // come up "shortly after" the call completes. Send (blocking)
            // would risk deadlock if the agent's call were itself
            // dispatched on the main thread for any reason.
            _mainSync.Post(_ =>
            {
                if (ct.IsCancellationRequested) return;
                SafeBoundary.Run("PaletteHost.EnsureVisible", () =>
                {
                    var p = GetOrCreateOnMainThread();
                    p.Visible = true;
                });
            }, null);
        }
    }
}
