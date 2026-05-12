using System;
using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Batch.Runtime
{
    // AutoCAD-backed session: owns a side-loaded Database and a top-level
    // Transaction. Disposing without CommitAndSave rolls the transaction
    // back via Database.Dispose semantics (any open Transaction is aborted
    // when its Database is disposed).
    //
    // The pattern matches the user's reference loop:
    //
    //     using (Database xDb = new Database(false, true))
    //     using (Transaction xTx = xDb.TransactionManager.StartTransaction())
    //     {
    //         xDb.ReadDwgFile(file, FileShare.Read, allowCPConversion: false, password: "");
    //         ...
    //         if (commit) { xTx.Commit(); xDb.SaveAs(file, xDb.OriginalFileVersion); }
    //     }
    //
    // The spec's runtime template orders ReadDwgFile BEFORE StartTransaction;
    // the reference loop does the opposite. Both work — but the spec's order
    // is what we use because it makes the Database fully populated by the
    // time the first transaction starts.
    //
    // SaveAs preserves the original DWG version via Database.OriginalFileVersion
    // (verified shape: `DwgVersion Database.OriginalFileVersion { get; }`).
    internal sealed class AcadBatchSession : IBatchSession
    {
        private readonly Database _db;
        private readonly Transaction _tx;
        private bool _committed;
        private bool _disposed;

        public string Path { get; }
        public Database Database => _db;
        public Transaction Transaction => _tx;

        public AcadBatchSession(string path, Database db, Transaction tx)
        {
            Path = path;
            _db = db;
            _tx = tx;
        }

        public void CommitAndSave()
        {
            if (_committed) return;
            _tx.Commit();
            // The Database's OriginalFileVersion preserves the file's
            // existing DWG version on save — never silently up-version a
            // file. The spec is explicit: no user toggle, no asking.
            _db.SaveAs(Path, _db.OriginalFileVersion);
            _committed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // If not committed, we intentionally don't call Tx.Commit. The
            // Database's Dispose aborts any pending transaction; we still
            // dispose the Transaction explicitly first to be tidy.
            //
            // Both disposals are wrapped to never throw — a tear-down
            // failure must not mask the body's outcome.
            try { _tx.Dispose(); } catch { }
            try { _db.Dispose(); } catch { }
        }
    }
}
