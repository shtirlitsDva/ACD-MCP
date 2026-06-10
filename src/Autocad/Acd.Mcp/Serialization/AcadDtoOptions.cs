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
        public static JsonSerializerOptions Build(DtoRegistry registry,
            IDtoReloadTrigger? reload = null,
            DtoDiagnostics? diagnostics = null)
        {
            var options = new JsonSerializerOptions
            {
                // Snake_case is the DTO convention spec'd for the agent-facing
                // JSON: `color_index`, not `colorIndex`. The policy lets DTO
                // authors write either Pascal names (transformed) or snake_case
                // names (untouched) — both end up the same in the output.
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
                // AutoCAD geometry can legitimately yield Infinity / NaN
                // (degenerate Distance, divide-by-zero in derived formulas).
                // The default JSON behaviour throws on those values, which
                // bubbles up as $serialization_error and erases the actual
                // shape. Emit them as "Infinity" / "-Infinity" / "NaN"
                // strings instead — readable, and the agent can pattern-
                // match on the special name.
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                // Anonymous objects produced by DTO projections include enums
                // (LineWeight, ColorMethod, etc.). Writing them as names is
                // friendlier to an agent than raw integers.
                Converters = { new JsonStringEnumConverter() },
            };
            options.Converters.Add(new DtoConverterFactory(registry, reload, diagnostics));
            return options;
        }
    }
}
