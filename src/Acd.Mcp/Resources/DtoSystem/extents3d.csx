// @dto: Autodesk.AutoCAD.DatabaseServices.Extents3d

Acd.RegisterDto<Extents3d>(e => new
{
    min = e.MinPoint,
    max = e.MaxPoint,
});
