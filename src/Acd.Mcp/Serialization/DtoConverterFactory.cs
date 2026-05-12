using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acd.Mcp.Serialization
{
    // System.Text.Json hook. Claims any AutoCAD-namespaced type plus any type
    // already in the registry. Everything else falls through to STJ's defaults
    // (primitives, collections, anonymous objects produced by DTO projections,
    // user records).
    //
    // The actual write logic is in DtoConverter<T>: it dispatches on the
    // VALUE's runtime type, not the declared static type, so polymorphic
    // serialisation works automatically — an `object` carrying a Circle, or a
    // `List<Entity>` of mixed concrete types, all get the right projection.
    public sealed class DtoConverterFactory : JsonConverterFactory
    {
        private readonly DtoRegistry _registry;
        private readonly IDtoReloadTrigger _reload;

        public DtoConverterFactory(DtoRegistry registry, IDtoReloadTrigger? reload = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _reload = reload ?? NoopReloadTrigger.Instance;
        }

        public override bool CanConvert(Type typeToConvert)
        {
            if (typeToConvert == typeof(object)) return false; // STJ does runtime dispatch itself.
            if (_registry.TryGet(typeToConvert, out _)) return true;
            return IsAcadType(typeToConvert);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(DtoConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)Activator.CreateInstance(converterType, _registry, _reload)!;
        }

        internal static bool IsAcadType(Type t)
        {
            var ns = t.Namespace;
            if (ns is null) return false;
            return ns.StartsWith("Autodesk.AutoCAD", StringComparison.Ordinal)
                || ns.StartsWith("Autodesk.Aec", StringComparison.Ordinal);
        }
    }

    internal sealed class DtoConverter<T> : JsonConverter<T>
    {
        private readonly DtoRegistry _registry;
        private readonly IDtoReloadTrigger _reload;

        public DtoConverter(DtoRegistry registry, IDtoReloadTrigger reload)
        {
            _registry = registry;
            _reload = reload;
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException(
                "DTO converters are write-only. Reading AutoCAD entities from JSON is not a supported scenario.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }

            var runtimeType = value.GetType();

            if (_registry.TryGet(runtimeType, out var projection))
            {
                WriteProjected(writer, value, projection, options);
                return;
            }

            // Cache miss. Let the reload trigger try to populate, then retry once.
            _reload.NotifyMiss(runtimeType);
            if (_registry.TryGet(runtimeType, out projection))
            {
                WriteProjected(writer, value, projection, options);
                return;
            }

            // Still unresolved — emit the marker that signals the agent it
            // needs to author a DTO (or ask the user to).
            writer.WriteStartObject();
            writer.WriteString("$unsupported", runtimeType.FullName ?? runtimeType.Name);
            writer.WriteEndObject();
        }

        private static void WriteProjected(
            Utf8JsonWriter writer, T value, IDtoProjection projection, JsonSerializerOptions options)
        {
            var projected = projection.Project(value!);
            JsonSerializer.Serialize(writer, projected, projected?.GetType() ?? typeof(object), options);
        }
    }
}
