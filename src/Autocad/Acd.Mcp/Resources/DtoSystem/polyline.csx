// @dto: Autodesk.AutoCAD.DatabaseServices.Polyline

// "Lightweight" polyline (LWPolyline). Vertex data is inline — no
// transaction needed to read points and bulges.
Acd.RegisterDto<Polyline>(pl =>
{
    var vertices = new List<object>(pl.NumberOfVertices);
    for (int i = 0; i < pl.NumberOfVertices; i++)
    {
        vertices.Add(new
        {
            position = pl.GetPoint2dAt(i),
            bulge = pl.GetBulgeAt(i),
            start_width = pl.GetStartWidthAt(i),
            end_width = pl.GetEndWidthAt(i),
        });
    }
    return new
    {
        closed = pl.Closed,
        length = pl.Length,
        area = pl.Area,
        layer = pl.Layer,
        color_index = pl.Color.ColorIndex,
        vertices = vertices,
    };
});
