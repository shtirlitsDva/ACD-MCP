using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Data
{
    // The single abstraction that hides where an entity's metadata is stored —
    // Block Attributes, AECC PropertySets, or XData. A DTO that needs to expose
    // "PartNumber" or "Zone" never asks "which mechanism?", it just calls
    // ReadAll/TryRead on the composite provider and gets whichever is present.
    //
    // The transaction comes from the caller. The DTO projection layer wraps
    // these calls so DTO code can stay terse (it pulls the current top
    // transaction from the entity's database itself).
    //
    // Values are returned as strings; richer typing (numeric, boolean) is a
    // future extension once we have a concrete need that suffers from the
    // coercion. Today the consumer is JSON serialization, where string is the
    // safest cross-storage shape.
    public interface IEntityDataProvider
    {
        Outcome<string> TryRead(Entity entity, Transaction tx, string key);
        IReadOnlyDictionary<string, string> ReadAll(Entity entity, Transaction tx);
    }
}
