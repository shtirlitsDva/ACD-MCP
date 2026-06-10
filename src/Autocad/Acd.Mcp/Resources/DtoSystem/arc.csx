// @dto: Autodesk.AutoCAD.DatabaseServices.Arc

Acd.RegisterDto<Arc>(a => new
{
    center = a.Center,
    radius = a.Radius,
    start_angle = a.StartAngle,
    end_angle = a.EndAngle,
    normal = a.Normal,
    length = a.Length,
    layer = a.Layer,
    color_index = a.Color.ColorIndex,
});
