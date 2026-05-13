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
    // Lives in Acd.Mcp.Api (the default-ALC assembly) because the script's
    // emitted IL must reference this type from a NON-collectible ALC. See
    // G6 / G8 in the v2 crash-test journal for the full reasoning. Cannot
    // live in the zero-dep Contracts assembly because the AutoCAD types
    // Database and Transaction are not BCL.
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
