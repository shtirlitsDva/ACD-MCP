using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Batch.Runtime
{
    // The shape a batch script body sees. Mirrors the spec exactly:
    //
    //     ctx.Step(name).Require(...).Apply(...);
    //
    //     ... where xDb / xTx / ctx are the three globals the body can name.
    //
    // Names are intentionally lowercase: matches the spec text verbatim
    // (`xDb`, `xTx`, `ctx`) so the script body in the docs is the script
    // body the runtime sees.
    //
    // This type does NOT live in Acd.Mcp.Batch because the runtime stays
    // AutoCAD-free. It lives here, in the plugin assembly, alongside the
    // host that produces it.
    //
    // Compile-time enforcement: the batch globals deliberately do NOT
    // expose Application / Document / Editor. A batch script that tries
    // to touch `Application` fails to compile with a clear diagnostic.
    public sealed class AcadBatchGlobals
    {
        public Database xDb { get; set; } = default!;
        public Transaction xTx { get; set; } = default!;
        public IBatchContext ctx { get; set; } = default!;
    }
}
