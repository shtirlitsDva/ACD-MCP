using System;

namespace Acd.Mcp.Batch
{
    // One drawing's lifecycle, abstracted away from AutoCAD specifics.
    //
    // The runner:
    //   1. Asks IDrawingHost.Open(path) for a session.
    //   2. Hands the session's exposed Database/Transaction handles to the
    //      compiled script delegate (the host knows the concrete types).
    //   3. Inspects the body's outcome + ctx.HasFailures.
    //   4. If green AND mode == Live: Commit, then SaveAs.
    //   5. Always: Dispose (which rolls back if not committed).
    //
    // The session abstraction keeps the runner free of AutoCAD references —
    // the host returns the session, the compiled delegate operates on it.
    //
    // The TDatabase / TTransaction generics let `Acd.Mcp.Batch` stay free of
    // any AutoCAD type identity while still letting concrete hosts expose
    // strongly-typed handles to the compiled script. Tests use mock types in
    // place of the real Database/Transaction.
    public interface IBatchSession : IDisposable
    {
        // The path this session was opened against.
        string Path { get; }

        // Commits and saves the underlying drawing back to disk. Called by
        // the runner only when:
        //   - Mode == Live, AND
        //   - The body's Outcome is Pass, AND
        //   - ctx.HasFailures == false
        // The host is responsible for preserving the file's original DWG
        // version on save.
        void CommitAndSave();
    }

    // Factory that opens a drawing into a session, parameterised on the
    // script globals type TGlobals. Implementations:
    //   - AcadDrawingHost (in Acd.Mcp): real side-loaded Database +
    //                                    Transaction; TGlobals is
    //                                    AcadBatchGlobals exposing
    //                                    xDb / xTx / ctx.
    //   - FakeDrawingHost (in tests):   in-memory state; TGlobals is a test
    //                                    globals type exposing mock handles.
    //
    // Open is the abstraction boundary. After Open returns, the session owns
    // every drawing-state lifetime; the runner only sees IBatchSession.
    //
    // BuildGlobals produces the typed globals object the compiled script
    // will see. Same instance for the script's lifetime (one file). The
    // host wires xDb/xTx/ctx into TGlobals; the runtime never touches them.
    public interface IDrawingHost<out TGlobals> where TGlobals : class
    {
        // Open(path, lease) — the lease is owned by the caller (the runner)
        // and lives for as long as the session does. The host MAY also hold
        // its own internal resources tied to the path.
        IBatchSession Open(string path, FileLease lease);

        TGlobals BuildGlobals(IBatchSession session, IBatchContext ctx);
    }
}
