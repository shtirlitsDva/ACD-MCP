// @dto: Autodesk.AutoCAD.DatabaseServices.AttributeReference

Acd.RegisterDto<AttributeReference>(a => new
{
    tag = a.Tag,
    text = a.TextString,
    position = a.Position,
    height = a.Height,
    rotation = a.Rotation,
    layer = a.Layer,
});
