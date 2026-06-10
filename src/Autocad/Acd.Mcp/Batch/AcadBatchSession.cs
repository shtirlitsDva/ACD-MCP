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
        private readonly ResourceManager _resources;
        private bool _committed;

        public string Path { get; }
        public Database Database => _db;
        public Transaction Transaction => _tx;

        public AcadBatchSession(string path, Database db, Transaction tx)
        {
            Path = path;
            _db = db;
            _tx = tx;

            // ResourceManager disposes LIFO. Register Database FIRST so
            // Transaction is disposed before Database — the Transaction is
            // an inner resource of the Database and AutoCAD expects that
            // order. Errors are isolated via SafeBoundary.Run, so a failing
            // Transaction.Dispose does not block Database.Dispose, and
            // neither failure is silently swallowed — both land in
            // %LOCALAPPDATA%\Acd.Mcp\log.txt for the agent to see.
            _resources = new ResourceManager(SafeBoundary.Run);
            _resources.Register("AcadBatchSession.Database", _db);
            _resources.Register("AcadBatchSession.Transaction", _tx);
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

        // ResourceManager is itself idempotent and runs every step under
        // SafeBoundary.Run; no need for a second `_disposed` guard or
        // a try/catch wrapper here.
        public void Dispose() => _resources.Dispose();
    }
}
