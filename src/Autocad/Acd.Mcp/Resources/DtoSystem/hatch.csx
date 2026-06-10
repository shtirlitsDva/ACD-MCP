// @dto: Autodesk.AutoCAD.DatabaseServices.Hatch

// Loops/island data is rich and rarely needed in JSON form. v1 sticks to
// pattern + scalar bookkeeping; users can override in dto-user/ if they
// need loop geometry.
Acd.RegisterDto<Hatch>(h => new
{
    pattern_name = h.PatternName,
    pattern_type = h.PatternType,
    pattern_scale = h.PatternScale,
    pattern_angle = h.PatternAngle,
    area = h.Area,
    layer = h.Layer,
    color_index = h.Color.ColorIndex,
});
