using Xunit;
using Acd.Mcp.Serialization;

namespace Acd.Mcp.Tests;

public class DtoHeaderTests
{
    [Fact]
    public void Parses_canonical_header()
    {
        var src = "// @dto: Autodesk.AutoCAD.DatabaseServices.Circle\n\nAcd.RegisterDto<Circle>(c => c);";
        Assert.Equal("Autodesk.AutoCAD.DatabaseServices.Circle", DtoHeader.TryParse(src));
    }

    [Fact]
    public void Parses_when_whitespace_varies()
    {
        var src = "  //   @dto :   Autodesk.AutoCAD.Geometry.Point3d   \n";
        Assert.Equal("Autodesk.AutoCAD.Geometry.Point3d", DtoHeader.TryParse(src));
    }

    [Fact]
    public void Returns_null_when_no_header()
    {
        var src = "// just a comment\nAcd.RegisterDto<Foo>(f => f);";
        Assert.Null(DtoHeader.TryParse(src));
    }

    [Fact]
    public void Returns_null_for_empty()
    {
        Assert.Null(DtoHeader.TryParse(string.Empty));
    }

    [Fact]
    public void Accepts_nested_type_plus()
    {
        var src = "// @dto: Some.Namespace.Outer+Inner\n";
        Assert.Equal("Some.Namespace.Outer+Inner", DtoHeader.TryParse(src));
    }
}
