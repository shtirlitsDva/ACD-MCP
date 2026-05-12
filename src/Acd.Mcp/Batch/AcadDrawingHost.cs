using System.IO;
using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Batch.Runtime
{
    // Opens an AutoCAD side-loaded Database for one path, returns it
    // wrapped in an AcadBatchSession. The runner never sees Database or
    // Transaction directly — they ride inside the session.
    //
    // The 4-arg ReadDwgFile overload that takes FileShare is the one we
    // want: it lets us pass FileShare.Read explicitly. Both Test and Live
    // use FileShare.Read per the spec. The lease is a probe (held only
    // momentarily); ReadDwgFile then opens the underlying file on its own
    // terms with FileShare.Read.
    internal sealed class AcadDrawingHost : IDrawingHost<AcadBatchGlobals>
    {
        public IBatchSession Open(string path, FileLease lease)
        {
            // new Database(false, true): build no default drawing,
            // no document association. The standard side-loading shape.
            var db = new Database(buildDefaultDrawing: false, noDocument: true);

            // FileShare.Read: hard requirement from the spec for both Test
            // and Live. Any concurrent writer makes this throw (which is
            // what we want — see the file-locked-aborts contract).
            db.ReadDwgFile(path, FileShare.Read, allowCPConversion: false, password: "");

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
