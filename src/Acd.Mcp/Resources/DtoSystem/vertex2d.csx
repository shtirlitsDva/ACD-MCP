// @dto: Autodesk.AutoCAD.DatabaseServices.Vertex2d

// Vertex of an old-style Polyline2d. The lightweight Polyline above stores
// vertex data inline; this type only appears for legacy POLYLINE entities.
Acd.RegisterDto<Vertex2d>(v => new
{
    position = v.Position,
    bulge = v.Bulge,
    layer = v.Layer,
});
