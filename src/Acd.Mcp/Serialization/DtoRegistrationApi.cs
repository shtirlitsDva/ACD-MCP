namespace Acd.Mcp.Serialization
{
    // The surface a DTO .csx file calls into. Each file is compiled with a
    // fresh DtoRegistrationApi instance whose Source tag identifies the file —
    // that tag lands on the registered projection and shows up in diagnostics
    // when two files register the same type.
    //
    // This class is intentionally tiny. Anything beyond registration belongs
    // on the IEntityDataProvider surface (exposed via DataProvider, attached
    // in slice 6).
    public sealed class DtoRegistrationApi
    {
        private readonly DtoRegistry _registry;
        private readonly string _source;

        public DtoRegistrationApi(DtoRegistry registry, string source)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public void RegisterDto<T>(Func<T, object?> projection)
        {
            if (projection is null) throw new ArgumentNullException(nameof(projection));
            _registry.Register(projection, _source);
        }
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
