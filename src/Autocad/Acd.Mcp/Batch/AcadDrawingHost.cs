using System.IO;
using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Batch.Runtime
{
    // Opens an AutoCAD side-loaded Database for one path, returns it
    // wrapped in an AcadBatchSession. The runner never sees Database or
    // Transaction directly — they ride inside the session.
    //
    // Share-mode choice (FileShare.ReadWrite) — see issue #1 for the full
    // story. The short version: AutoCAD's Database holds the underlying OS
    // file handle past `Dispose()` (handle release is finalizer-driven, not
    // synchronous). The runner opens each file twice in Mode=Live — once
    // for the Test phase and again for the Live phase — and the Test
    // handle is typically still alive when the Live phase tries to SaveAs.
    // A FileShare.Read open says "I share for read, NOT for write," which
    // makes the OS refuse the Live SaveAs writer and surface eFilerError.
    // FileShare.ReadWrite shares for both — the lingering handle no longer
    // blocks our own SaveAs. Detection of foreign writers stays at the
    // probe layer (DefaultFileAccessProbe), which is unaffected by our
    // internal share mode.
    internal sealed class AcadDrawingHost : IDrawingHost<AcadBatchGlobals>
    {
        public IBatchSession Open(string path, FileLease lease)
        {
            // new Database(false, true): build no default drawing,
            // no document association. The standard side-loading shape.
            var db = new Database(buildDefaultDrawing: false, noDocument: true);

            // FileShare.ReadWrite: lets a lingering handle from this
            // batch's earlier Test phase coexist with our own future
            // SaveAs. See the class-level comment for why FileShare.Read
            // (the obvious choice on paper) breaks Mode=Live.
            db.ReadDwgFile(path, FileShare.ReadWrite, allowCPConversion: false, password: "");

            var tx = db.TransactionManager.StartTransaction();
            return new AcadBatchSession(path, db, tx);
        }

        public AcadBatchGlobals BuildGlobals(IBatchSession session, IBatchContext ctx)
        {
            var s = (AcadBatchSession)session;
            return new AcadBatchGlobals
            {
                xDb = s.Database,
                xTx = s.Transaction,
                ctx = ctx,
            };
        }
    }
}
