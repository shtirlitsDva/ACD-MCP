// @dto: Autodesk.AutoCAD.DatabaseServices.BlockReference

// BlockReference includes user-authored metadata through the data provider —
// transparently merges block attributes and (when on Civil) property sets.
Acd.RegisterDto<BlockReference>(br => new
{
    name = br.Name,
    position = br.Position,
    rotation = br.Rotation,
    layer = br.Layer,
    color_index = br.Color.ColorIndex,
    attributes = Acd.DataProvider.ReadAll(br),
});
