using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Serialization
{
    // The DTO/REPL-facing data-provider surface. Wraps a pair of delegates
    // (read-all / try-read) so DTO projections never have to thread a
    // Transaction by hand — the wrapper pulls it off the entity's
    // Database.TransactionManager.TopTransaction.
    //
    // Why delegates instead of an interface in this assembly: the underlying
    // IEntityDataProvider abstraction lives in Acd.Mcp (plugin/isolated ALC)
    // and exposes Outcome<T> from Acd.Mcp.Batch. Bringing either type across
    // the ALC boundary risks duplicate-identity load failures. Delegates
    // keep the boundary narrow — Acd.Mcp.Api stays self-contained except for
    // the AutoCAD types we already reference, and Acd.Mcp adapts at the
    // wire point.
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
        private readonly Func<Entity, Transaction, IReadOnlyDictionary<string, string>> _readAll;
        private readonly Func<Entity, Transaction, string, string?> _tryRead;

        public DtoDataProviderApi(
            Func<Entity, Transaction, IReadOnlyDictionary<string, string>> readAll,
            Func<Entity, Transaction, string, string?> tryRead)
        {
            _readAll = readAll ?? throw new ArgumentNullException(nameof(readAll));
            _tryRead = tryRead ?? throw new ArgumentNullException(nameof(tryRead));
        }

        public IReadOnlyDictionary<string, string> ReadAll(Entity entity)
        {
            var tx = ResolveTx(entity);
            return _readAll(entity, tx);
        }

        public string? TryRead(Entity entity, string key)
        {
            var tx = ResolveTx(entity);
            return _tryRead(entity, tx, key);
        }

        private static Transaction ResolveTx(Entity entity)
        {
            var tx = entity?.Database?.TransactionManager?.TopTransaction;
            if (tx is null)
                throw new InvalidOperationException(
                    "No active transaction. DTO projections that read metadata must run inside " +
                    "a Transaction. Wrap your script in `using (var tx = Db.TransactionManager.StartTransaction()) { ... }`.");
            return tx;
        }
    }
}
