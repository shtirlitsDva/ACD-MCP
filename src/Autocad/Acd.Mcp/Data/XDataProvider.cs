using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Data
{
    // XData (extended-entity-data) support is deferred. The interface is wired
    // in upfront so downstream code — DTOs, the composite, the API surface —
    // does not need a retrofit when we implement it. Until then, every call
    // throws so the omission is loud rather than silent.
    //
    // Once implemented, this wraps the per-RegApp typed-value list on every
    // Entity's XData property.
    public sealed class XDataProvider : IEntityDataProvider
    {
        public Outcome<string> TryRead(Entity entity, Transaction tx, string key) =>
            throw new System.NotSupportedException(
                "XData support is not yet implemented. Track this in the open-source backlog.");

        public System.Collections.Generic.IReadOnlyDictionary<string, string> ReadAll(Entity entity, Transaction tx) =>
            throw new System.NotSupportedException(
                "XData support is not yet implemented. Track this in the open-source backlog.");
    }
}
