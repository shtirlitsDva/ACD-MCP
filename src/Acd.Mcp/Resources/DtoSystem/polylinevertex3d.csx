// @dto: Autodesk.AutoCAD.DatabaseServices.PolylineVertex3d

Acd.RegisterDto<PolylineVertex3d>(v => new
{
    position = v.Position,
    layer = v.Layer,
});
