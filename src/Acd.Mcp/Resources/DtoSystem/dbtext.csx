// @dto: Autodesk.AutoCAD.DatabaseServices.DBText

Acd.RegisterDto<DBText>(t => new
{
    text = t.TextString,
    position = t.Position,
    height = t.Height,
    rotation = t.Rotation,
    layer = t.Layer,
    color_index = t.Color.ColorIndex,
});
