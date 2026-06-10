using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Data
{
    // Chains a list of providers. TryRead returns on the first Success;
    // ReadAll merges every provider's contribution, with earlier providers
    // winning on key collision.
    //
    // Order matters. The factory registers them deliberately —
    // BlockAttribute first (cheapest, narrowest), PropertySet second
    // (universal, Civil-only), XData last (deferred). The user composing
    // a custom DTO doesn't have to think about which mechanism holds a key:
    // they call ReadAll once and get the union.
    public sealed class CompositeDataProvider : IEntityDataProvider
    {
        private readonly IReadOnlyList<IEntityDataProvider> _providers;

        public CompositeDataProvider(IReadOnlyList<IEntityDataProvider> providers)
        {
            _providers = providers;
        }

        public Outcome<string> TryRead(Entity entity, Transaction tx, string key)
        {
            string? lastError = null;
            foreach (var provider in _providers)
            {
                var r = provider.TryRead(entity, tx, key);
                if (r is Outcome<string>.Pass pass) return pass;
                if (r is Outcome<string>.Failure f) lastError = f.Message;
            }
            return Outcome<string>.Fail(lastError ?? $"Key '{key}' not found in any provider.");
        }

        public IReadOnlyDictionary<string, string> ReadAll(Entity entity, Transaction tx)
        {
            var merged = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _providers)
            {
                foreach (var (k, v) in provider.ReadAll(entity, tx))
                {
                    if (!merged.ContainsKey(k)) merged[k] = v;
                }
            }
            return merged;
        }
    }
}
