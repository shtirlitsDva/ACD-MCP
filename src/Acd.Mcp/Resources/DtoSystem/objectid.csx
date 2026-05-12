// @dto: Autodesk.AutoCAD.DatabaseServices.ObjectId

Acd.RegisterDto<ObjectId>(id =>
{
    string handle = "";
    if (!id.IsNull)
    {
        try { handle = id.Handle.Value.ToString("X"); }
        catch { /* erased / detached id — leave handle blank */ }
    }
    return new
    {
        handle = handle,
        is_valid = id.IsValid,
        is_erased = id.IsErased,
    };
});
