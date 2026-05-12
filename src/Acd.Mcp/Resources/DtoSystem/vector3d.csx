// @dto: Autodesk.AutoCAD.Geometry.Vector3d

Acd.RegisterDto<Vector3d>(v => new
{
    x = v.X,
    y = v.Y,
    z = v.Z,
});
