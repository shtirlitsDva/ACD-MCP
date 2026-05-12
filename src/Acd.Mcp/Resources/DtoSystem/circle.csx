// @dto: Autodesk.AutoCAD.DatabaseServices.Circle

Acd.RegisterDto<Circle>(c => new
{
    center = c.Center,
    radius = c.Radius,
    normal = c.Normal,
    layer = c.Layer,
    color_index = c.Color.ColorIndex,
});
