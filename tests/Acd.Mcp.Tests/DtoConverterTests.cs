using System.Text.Json;
using Xunit;
using Acd.Mcp.Serialization;
using Autodesk.AutoCAD.Tests.Fakes;

namespace Autodesk.AutoCAD.Tests.Fakes
{
    // A fake AutoCAD-namespaced type lets us exercise the converter's
    // "AutoCAD type" gate without referencing real AutoCAD assemblies.
    public sealed class FakeCircle
    {
        public double Radius { get; init; }
        public string Layer { get; init; } = "0";
    }
}

namespace Acd.Mcp.Tests
{
    public class DtoConverterTests
    {
        [Fact]
        public void Registered_type_is_projected_via_lambda()
        {
            var registry = new DtoRegistry();
            registry.Register<FakeCircle>(
                c => new { radius = c.Radius, layer = c.Layer },
                source: "test");

            var options = AcadDtoOptions.Build(registry);
            var value = new FakeCircle { Radius = 5.0, Layer = "wall" };

            var json = JsonSerializer.Serialize(value, options);
            Assert.Contains("\"radius\":5", json);
            Assert.Contains("\"layer\":\"wall\"", json);
        }

        [Fact]
        public void Unregistered_AutoCAD_type_emits_unsupported_marker()
        {
            var registry = new DtoRegistry();
            var options = AcadDtoOptions.Build(registry);
            var value = new FakeCircle { Radius = 1.0 };

            var json = JsonSerializer.Serialize(value, options);
            Assert.Contains("\"$unsupported\":\"Autodesk.AutoCAD.Tests.Fakes.FakeCircle\"", json);
        }

        [Fact]
        public void Non_AutoCAD_type_passes_through_default_serialization()
        {
            var registry = new DtoRegistry();
            var options = AcadDtoOptions.Build(registry);
            var value = new { foo = 1, bar = "x" };

            var json = JsonSerializer.Serialize(value, options);
            Assert.Equal("{\"foo\":1,\"bar\":\"x\"}", json);
        }

        [Fact]
        public void Reload_trigger_is_called_on_miss_and_can_repair()
        {
            var registry = new DtoRegistry();
            var trigger = new TestReloadTrigger(() =>
                registry.Register<FakeCircle>(
                    c => new { radius = c.Radius },
                    source: "test-reload"));
            var options = AcadDtoOptions.Build(registry, trigger);

            var value = new FakeCircle { Radius = 9.0 };
            var json = JsonSerializer.Serialize(value, options);

            Assert.True(trigger.Fired);
            Assert.Contains("\"radius\":9", json);
            Assert.DoesNotContain("$unsupported", json);
        }

        [Fact]
        public void Snake_case_naming_policy_is_applied_to_projections()
        {
            var registry = new DtoRegistry();
            registry.Register<FakeCircle>(
                c => new { ColorIndex = 7, StartAngle = 0.5 },
                source: "test");
            var options = AcadDtoOptions.Build(registry);

            var json = JsonSerializer.Serialize(
                new FakeCircle(), options);

            Assert.Contains("\"color_index\":7", json);
            Assert.Contains("\"start_angle\":0.5", json);
        }

        private sealed class TestReloadTrigger : IDtoReloadTrigger
        {
            private readonly System.Action _onMiss;
            public bool Fired { get; private set; }

            public TestReloadTrigger(System.Action onMiss) => _onMiss = onMiss;

            public void NotifyMiss(System.Type runtimeType)
            {
                Fired = true;
                _onMiss();
            }
        }
    }
}
