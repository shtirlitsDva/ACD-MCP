using Autodesk.AutoCAD.DatabaseServices;
using Acd.Mcp.Batch;
using Acd.Mcp.Data;

namespace Acd.Mcp.Serialization
{
    // The DTO-facing data-provider surface. Wraps an IEntityDataProvider so
    // DTO projections never have to thread a Transaction by hand — the wrapper
    // pulls it off the entity's Database.TransactionManager.TopTransaction.
    //
    // Why hide tx from the DTO body: the projection lambda is single-argument
    // (Func<T, object?>). There is no syntactic place for the caller to inject
    // a transaction without changing every DTO's signature. The implicit
    // resolve keeps DTOs terse and lines up with how AutoCAD scripts already
    // think about "the current transaction".
    //
    // If a projection runs outside any transaction, we throw. Returning empty
    // would be a worse failure mode — the agent would silently get a JSON
    // shape with no metadata and not know why.
    public sealed class DtoDataProviderApi
    {
        private readonly IEntityDataProvider _provider;

        public DtoDataProviderApi(IEntityDataProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public IReadOnlyDictionary<string, string> ReadAll(Entity entity)
        {
            var tx = ResolveTx(entity);
            return _provider.ReadAll(entity, tx);
        }

        public string? TryRead(Entity entity, string key)
        {
            var tx = ResolveTx(entity);
            return _provider.TryRead(entity, tx, key) is Outcome<string>.Pass p ? p.Value : null;
        }

        private static Transaction ResolveTx(Entity entity)
        {
            var tx = entity?.Database?.TransactionManager?.TopTransaction;
            if (tx is null)
                throw new InvalidOperationException(
                    "No active transaction. DTO projections that read metadata must run inside " +
                    "a Transaction. Wrap your script in `using var tx = doc.TransactionManager.StartTransaction();`.");
            return tx;
        }
    }
}
