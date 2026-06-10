// @dto: Autodesk.AutoCAD.DatabaseServices.Handle

Acd.RegisterDto<Handle>(h => new
{
    // Hex representation matches what users see in HANDLE-based AutoCAD commands.
    value = h.Value.ToString("X"),
});
