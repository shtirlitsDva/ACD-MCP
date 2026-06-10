using System.Threading;
using Acd.Mcp.Batch.Runtime;

namespace Acd.Mcp.Ui
{
    // Lazy-binding handle to the SCRIPT/BATCH palette. RPC handlers take
    // this instead of the concrete palette so they can dispatch BEFORE
    // the user has ever opened the palette — the host materialises the
    // window on demand (EnsureVisible) and surfaces the live VM through
    // CurrentBatchUiState once it exists.
    //
    // Why this exists: prior to fragility-fix v2 the RPC handlers were
    // constructed inside ACDMCP_PALETTE, which meant every AutoCAD restart
    // broke `script.*` / `batch.*` calls until the user manually opened
    // the palette. See docs/review/fragility-review-2026-05-18.md
    // (finding-1).
    public interface IPaletteHost
    {
        // True iff the palette has been constructed AND is currently
        // visible to the user. Read-only — RPC handlers consult this to
        // decide whether a "palette closed" structured payload is
        // appropriate (vs. forcing the palette open via EnsureVisible).
        bool IsOpen { get; }

        // The live batch view-model, or null if the palette has never
        // been opened. RPC handlers that read user-owned UI state
        // (selection, on-failure policy) check this before dispatching
        // and return a structured "palette closed" payload when null.
        IBatchUiState? CurrentBatchUiState { get; }

        // Ensure the palette is constructed and visible. Idempotent and
        // safe to call from any thread — the host marshals to the
        // AutoCAD main thread internally. Used by propose-style RPC
        // calls so the user sees the staged proposal immediately even
        // if they never opened the palette themselves.
        void EnsureVisible(CancellationToken ct = default);
    }
}
