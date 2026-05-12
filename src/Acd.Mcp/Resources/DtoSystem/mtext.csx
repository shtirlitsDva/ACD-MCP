// @dto: Autodesk.AutoCAD.DatabaseServices.MText

Acd.RegisterDto<MText>(m => new
{
    contents = m.Contents,
    location = m.Location,
    height = m.TextHeight,
    rotation = m.Rotation,
    width = m.Width,
    layer = m.Layer,
    color_index = m.Color.ColorIndex,
});
