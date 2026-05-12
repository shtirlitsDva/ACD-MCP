// @dto: Autodesk.AutoCAD.DatabaseServices.Extents2d

Acd.RegisterDto<Extents2d>(e => new
{
    min = e.MinPoint,
    max = e.MaxPoint,
});
