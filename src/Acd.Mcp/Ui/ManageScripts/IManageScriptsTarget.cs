using Acd.Mcp.Batch;

namespace Acd.Mcp.Ui.ManageScripts
{
    // Narrow callback surface a view-model exposes to the shared
    // Manage Scripts window. The window doesn't need to know about
    // BATCH or SCRIPT specifics — it just needs to read the current
    // editor text (for Save-As) and tell the host to load a script.
    //
    // Implemented by BatchViewModel and ScriptViewModel. The window
    // holds both an IManageScriptsTarget and the corresponding
    // ScriptEditor (for the store + flavor); together they cover
    // the list / load / save / rename / delete actions without
    // tying the window to either VM type.
    //
    // LoadSavedScript returns true if the load was applied; false if
    // the VM refused (e.g. user clicked No on the unsaved-changes
    // prompt). The window keeps itself open on false so the user can
    // pick another script without retrying the whole flow.
    internal interface IManageScriptsTarget
    {
        string CurrentScriptText { get; }
        bool LoadSavedScript(SavedScript saved);
    }
}
