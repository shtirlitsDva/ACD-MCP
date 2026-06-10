// @dto: Autodesk.AutoCAD.DatabaseServices.DBPoint

Acd.RegisterDto<DBPoint>(p => new
{
    position = p.Position,
    layer = p.Layer,
    color_index = p.Color.ColorIndex,
});
