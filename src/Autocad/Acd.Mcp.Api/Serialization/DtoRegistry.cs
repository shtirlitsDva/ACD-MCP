using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Acd.Mcp.Serialization
{
    // Thread-safe map from runtime System.Type to its registered projection.
    //
    // Registrations carry a `source` tag identifying which folder/file produced
    // them; that lets the loader implement the override rule (user wins over
    // system) without the registry needing to know about folders. The loader
    // simply registers user files AFTER system files within a transaction, and
    // calls `Register` with `overwrite: true` for the second pass.
    //
    // Lives in Acd.Mcp.Api (default ALC) — Roslyn-emitted IL from DTO .csx
    // submissions JIT-loads through the default ALC, so DtoRegistrationApi's
    // field of this type must be resolvable from that ALC. The composite data
    // providers, converter factory, and loader continue to live in Acd.Mcp
    // (isolated ALC) and reach in through InternalsVisibleTo for TryGet.
    public sealed class DtoRegistry
    {
        private readonly ConcurrentDictionary<Type, IDtoProjection> _entries = new();

        public void Register<T>(Func<T, object?> projection, string source)
        {
            if (projection is null) throw new ArgumentNullException(nameof(projection));
            _entries[typeof(T)] = new TypedProjection<T>(projection, source);
        }

        // Used by the loader's reload path: clears every registration so the
        // next scan can repopulate from scratch. Cheaper and more predictable
        // than diffing.
        public void Clear() => _entries.Clear();

        internal bool TryGet(Type runtimeType, out IDtoProjection projection)
        {
            return _entries.TryGetValue(runtimeType, out projection!);
        }

        public IReadOnlyCollection<Type> RegisteredTypes =>
            _entries.Keys.ToList().AsReadOnly();
    }
}
