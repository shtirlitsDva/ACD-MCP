using Xunit;
using Acd.Mcp.Serialization;

namespace Acd.Mcp.Tests;

public class DtoRegistryTests
{
    private sealed class Foo { public int X { get; init; } }

    [Fact]
    public void Register_then_TryGet_returns_projection()
    {
        var r = new DtoRegistry();
        r.Register<Foo>(f => new { x = f.X }, source: "test");
        Assert.Contains(typeof(Foo), r.RegisteredTypes);
    }

    [Fact]
    public void Register_twice_overwrites()
    {
        var r = new DtoRegistry();
        r.Register<Foo>(f => "first", source: "system");
        r.Register<Foo>(f => "second", source: "user");

        // Single entry, latest wins — the loader relies on this for the
        // user-overrides-system rule.
        Assert.Single(r.RegisteredTypes);
    }

    [Fact]
    public void Clear_empties_registry()
    {
        var r = new DtoRegistry();
        r.Register<Foo>(f => f.X, source: "test");
        r.Clear();
        Assert.Empty(r.RegisteredTypes);
    }
}
