// @dto: Autodesk.AutoCAD.DatabaseServices.Line

Acd.RegisterDto<Line>(l => new
{
    start = l.StartPoint,
    end = l.EndPoint,
    length = l.Length,
    layer = l.Layer,
    color_index = l.Color.ColorIndex,
});
