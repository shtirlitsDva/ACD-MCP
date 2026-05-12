// @dto: Autodesk.AutoCAD.DatabaseServices.Polyline3d

// Polyline3d stores each vertex as a separate PolylineVertex3d database
// object. Walking those requires the current transaction. The base DTO
// returns the vertex ids (each ObjectId DTOs to its handle); users who
// need positions inline should override in dto-user/ with a transaction-
// aware projection.
Acd.RegisterDto<Polyline3d>(p3d => new
{
    closed = p3d.Closed,
    length = p3d.Length,
    layer = p3d.Layer,
    color_index = p3d.Color.ColorIndex,
    vertex_ids = p3d.Cast<ObjectId>().ToArray(),
});
