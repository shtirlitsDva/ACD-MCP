// @dto: Autodesk.AutoCAD.Geometry.Point2d

Acd.RegisterDto<Point2d>(p => new
{
    x = p.X,
    y = p.Y,
});
