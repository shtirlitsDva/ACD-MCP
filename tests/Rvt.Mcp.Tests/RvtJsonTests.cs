using System.Text.Json;
using Rvt.Mcp;
using Xunit;

// Fake Revit-namespaced type: the converter factory matches on the
// Autodesk.Revit.* namespace prefix, so anything here exercises the
// $unsupported fallback without a running Revit.
namespace Autodesk.Revit.DB.Fakes
{
    public class SomeRevitishThing
    {
        public string Hidden { get; set; } = "should never serialize";
    }
}

namespace Rvt.Mcp.Tests
{
    public class RvtJsonTests
    {
        private static readonly JsonSerializerOptions Options = RvtJson.BuildOptions();

        [Fact]
        public void ElementId_SerializesAsNumber()
        {
            var id = new Autodesk.Revit.DB.ElementId(123456L);
            string json = JsonSerializer.Serialize(id, Options);
            Assert.Equal("123456", json);
        }

        [Fact]
        public void Xyz_SerializesAsCoordinates()
        {
            var p = new Autodesk.Revit.DB.XYZ(1.5, 2.5, -3);
            string json = JsonSerializer.Serialize(p, Options);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1.5, doc.RootElement.GetProperty("x").GetDouble());
            Assert.Equal(2.5, doc.RootElement.GetProperty("y").GetDouble());
            Assert.Equal(-3, doc.RootElement.GetProperty("z").GetDouble());
        }

        [Fact]
        public void UnknownRevitNamespacedType_BecomesUnsupportedSentinel()
        {
            var value = new Autodesk.Revit.DB.Fakes.SomeRevitishThing();
            string json = JsonSerializer.Serialize(value, Options);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("SomeRevitishThing",
                doc.RootElement.GetProperty("$unsupported").GetString());
            Assert.False(doc.RootElement.TryGetProperty("hidden", out _));
        }

        [Fact]
        public void NonRevitTypes_SerializeNormally()
        {
            string json = JsonSerializer.Serialize(
                new { Name = "wall", Count = 3 }, Options);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("wall", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        }

        [Fact]
        public void ListsOfElementIds_SerializeAsNumberArray()
        {
            var ids = new List<Autodesk.Revit.DB.ElementId>
            {
                new(1L), new(2L), new(3L),
            };
            string json = JsonSerializer.Serialize(ids, Options);
            Assert.Equal("[1,2,3]", json);
        }
    }
}
