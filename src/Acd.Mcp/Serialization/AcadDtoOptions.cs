using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acd.Mcp.Serialization
{
    // Convenience builder for the JsonSerializerOptions that the REPL response
    // path (and, later, the batch result path) uses to project AutoCAD entities
    // through registered DTOs.
    //
    // The plugin constructs one instance at startup and reuses it. The same
    // registry instance is passed to the DTO loader and to this options
    // builder so both sides see the same converter table.
    public static class AcadDtoOptions
    {
        public static JsonSerializerOptions Build(DtoRegistry registry, IDtoReloadTrigger? reload = null)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
                // Anonymous objects produced by DTO projections include enums
                // (Color, LinetypeObjectId etc.). Writing them as names is
                // friendlier to an agent than raw integers.
                Converters = { new JsonStringEnumConverter() },
            };
            options.Converters.Add(new DtoConverterFactory(registry, reload));
            return options;
        }
    }
}
