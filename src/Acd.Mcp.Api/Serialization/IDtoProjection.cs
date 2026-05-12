namespace Acd.Mcp.Serialization
{
    // Non-generic façade over a typed projection lambda. The DTO loader builds
    // one of these per `Acd.RegisterDto<T>(t => new { ... })` call and stores
    // it in the registry keyed by typeof(T). The serializer pulls it out at
    // write time and invokes Project(value), then serialises the returned
    // shape via System.Text.Json.
    //
    // Keeping the interface non-generic lets the registry use a single
    // dictionary keyed by System.Type.
    //
    // Lives in Acd.Mcp.Api (default ALC) so any IL emitted from a DTO .csx
    // submission can reference it without reaching into the plugin's
    // isolated ALC. See AcadGlobals for the same constraint applied to the
    // REPL globalsType.
    internal interface IDtoProjection
    {
        object? Project(object source);
    }

    internal sealed class TypedProjection<T> : IDtoProjection
    {
        private readonly System.Func<T, object?> _projection;
        private readonly string _source;

        public TypedProjection(System.Func<T, object?> projection, string source)
        {
            _projection = projection;
            _source = source;
        }

        public object? Project(object source) => _projection((T)source);

        public string Source => _source;
    }
}
