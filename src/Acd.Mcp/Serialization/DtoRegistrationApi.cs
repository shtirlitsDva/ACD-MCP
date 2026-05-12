namespace Acd.Mcp.Serialization
{
    // The single `Acd` surface a DTO .csx file sees. Two responsibilities:
    //
    //   * RegisterDto<T>(projection)  — bind a projection lambda for type T.
    //   * DataProvider                — composite metadata reader for use
    //                                   inside projection bodies.
    //
    // Why one type instead of two: the user types `Acd.RegisterDto<...>` and
    // `Acd.DataProvider.ReadAll(...)` in the same file. Surfacing both off
    // one facade matches the literal syntax shape and keeps the globals
    // type trivial. The internal collaborators (DtoRegistry, IEntityDataProvider)
    // are constructor-injected so this class stays a thin façade.
    //
    // Each .csx file gets its own DtoRegistrationApi instance because the
    // Source tag carries the file's name into the registry — useful when
    // diagnosing "which file registered Circle?".
    public sealed class DtoRegistrationApi
    {
        private readonly DtoRegistry _registry;
        private readonly string _source;

        public DtoRegistrationApi(DtoRegistry registry, DtoDataProviderApi dataProvider, string source)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            DataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public void RegisterDto<T>(Func<T, object?> projection)
        {
            if (projection is null) throw new ArgumentNullException(nameof(projection));
            _registry.Register(projection, _source);
        }

        public DtoDataProviderApi DataProvider { get; }
    }

    // CSharpScript globals. The script body sees `Acd` as if it were a local
    // variable; `Acd.RegisterDto<Circle>(c => new { ... })` is the canonical
    // call. Distinct from AcadGlobals (REPL) so the DTO file scope is small
    // and predictable.
    public sealed class DtoRegistrationGlobals
    {
        public DtoRegistrationApi Acd { get; }

        public DtoRegistrationGlobals(DtoRegistrationApi acd) => Acd = acd;
    }
}
