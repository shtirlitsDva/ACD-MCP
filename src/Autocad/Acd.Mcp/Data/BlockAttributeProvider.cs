using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Data
{
    // BlockReference attributes — visible labels users author on a block when
    // they insert it. The collection lives on the BlockReference itself and is
    // unique to that mechanism: nothing else in AutoCAD uses
    // AttributeCollection. For any non-BlockReference entity we return empty
    // / Failure — the composite will move on to the next provider.
    public sealed class BlockAttributeProvider : IEntityDataProvider
    {
        public Outcome<string> TryRead(Entity entity, Transaction tx, string key)
        {
            if (entity is not BlockReference br)
                return Outcome<string>.Fail($"{entity.GetType().Name} has no block attributes.");

            foreach (ObjectId id in br.AttributeCollection)
            {
                if (tx.GetObject(id, OpenMode.ForRead) is AttributeReference attRef
                    && string.Equals(attRef.Tag, key, System.StringComparison.OrdinalIgnoreCase))
                {
                    return Outcome<string>.Ok(attRef.TextString ?? string.Empty);
                }
            }
            return Outcome<string>.Fail($"Attribute '{key}' not found on block reference.");
        }

        public IReadOnlyDictionary<string, string> ReadAll(Entity entity, Transaction tx)
        {
            if (entity is not BlockReference br)
                return Empty;

            var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in br.AttributeCollection)
            {
                if (tx.GetObject(id, OpenMode.ForRead) is AttributeReference attRef && attRef.Tag is { } tag)
                {
                    map[tag] = attRef.TextString ?? string.Empty;
                }
            }
            return map;
        }

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>();
    }
}
