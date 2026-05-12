// @dto: Autodesk.AutoCAD.Geometry.Vector2d

Acd.RegisterDto<Vector2d>(v => new
{
    x = v.X,
    y = v.Y,
});
