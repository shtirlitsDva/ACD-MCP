// @dto: Autodesk.AutoCAD.Geometry.Point3d

Acd.RegisterDto<Point3d>(p => new
{
    x = p.X,
    y = p.Y,
    z = p.Z,
});
