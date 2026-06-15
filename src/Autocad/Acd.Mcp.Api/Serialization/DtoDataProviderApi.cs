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
    // Transaction resolution: if the caller already has an open transaction
    // (the script is still inside its `using (tx) { ... }`, or BATCH's xTx is
    // live), we reuse it. If NOT — the common case when serializing an entity
    // the script returned, because DTO projection runs *after* the script's
    // own transaction has closed — we open a short-lived OpenCloseTransaction
    // just for the read and dispose it immediately. The returned entity stays
    // readable (closed-but-pinned), and the metadata sub-objects (block
    // attributes, property sets) are re-opened under this fresh transaction.
    // Verified live: a BlockReference returned from a closed `using` block
    // serializes its attributes correctly through this path.
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
            => WithTransaction(entity, tx => _readAll(entity, tx));

        public string? TryRead(Entity entity, string key)
            => WithTransaction(entity, tx => _tryRead(entity, tx, key));

        // Runs body with a usable transaction: the active TopTransaction if one
        // exists, otherwise a short-lived OpenCloseTransaction opened on the
        // entity's own database and committed once the read is done.
        private static T WithTransaction<T>(Entity entity, Func<Transaction, T> body)
        {
            var db = entity?.Database
                ?? throw new InvalidOperationException(
                    "Cannot read entity metadata: the entity is not attached to a Database.");

            var top = db.TransactionManager.TopTransaction;
            if (top is not null)
                return body(top);

            using var tx = db.TransactionManager.StartOpenCloseTransaction();
            var result = body(tx);
            tx.Commit();
            return result;
        }
    }
}
