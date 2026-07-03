using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rvt.Mcp
{
    // Return-value serialization for the Revit REPL. Hand-rolled minimal
    // projections (Element / ElementId / XYZ) instead of porting the full
    // ACD-MCP DTO registry — same `$unsupported` sentinel contract so the
    // agent-side pattern matching stays identical. Extending this into the
    // csx-based DTO system is a follow-up once the surface proves itself.
    public static class RvtJson
    {
        public static JsonSerializerOptions BuildOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // Agent-facing JSON over a JSON-RPC byte stream, not HTML — relax
                // the default HTML-safe encoder so '<' '>' '&' backtick and
                // non-ASCII emit literally instead of as \uXXXX. Mirrors
                // Acd.Mcp.Serialization.AcadDtoOptions.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new RevitValueConverterFactory() },
            };
        }
    }

    // Catches every Autodesk.Revit.* type: known shapes get a useful
    // projection, the rest become {"$unsupported": "<short type name>"} —
    // never an exception, never an accidental object-graph walk into the
    // Revit API (which is full of cycles and disposed-handle traps).
    public sealed class RevitValueConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.Namespace?.StartsWith("Autodesk.Revit.", StringComparison.Ordinal) == true
            || typeToConvert.Namespace == "Autodesk.Revit";

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(RevitValueConverter<>).MakeGenericType(typeToConvert))!;
    }

    internal sealed class RevitValueConverter<T> : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Revit API values are write-only on the REPL surface.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }

            switch (value)
            {
                // ElementId: the long value is what agents feed back into scripts.
                case Autodesk.Revit.DB.ElementId id:
                    writer.WriteNumberValue(id.Value);
                    return;

                case Autodesk.Revit.DB.XYZ p:
                    writer.WriteStartObject();
                    writer.WriteNumber("x", p.X);
                    writer.WriteNumber("y", p.Y);
                    writer.WriteNumber("z", p.Z);
                    writer.WriteEndObject();
                    return;

                case Autodesk.Revit.DB.Element el:
                    WriteElement(writer, el);
                    return;

                default:
                    writer.WriteStartObject();
                    writer.WriteString("$unsupported", value.GetType().Name);
                    writer.WriteEndObject();
                    return;
            }
        }

        private static void WriteElement(Utf8JsonWriter writer, Autodesk.Revit.DB.Element el)
        {
            writer.WriteStartObject();
            try
            {
                writer.WriteNumber("id", el.Id.Value);
                writer.WriteString("name", el.Name);
                writer.WriteString("category", el.Category?.Name);
                writer.WriteString("type", el.GetType().Name);
            }
            catch (Exception ex)
            {
                // Disposed handle / invalid element — keep the response alive.
                writer.WriteString("$serialization_error", ex.Message);
            }
            writer.WriteEndObject();
        }
    }
}
