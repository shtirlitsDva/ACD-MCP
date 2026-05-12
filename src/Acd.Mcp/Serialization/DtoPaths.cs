using System.IO;

namespace Acd.Mcp.Serialization
{
    // Canonical filesystem locations for DTO storage. Two tiers:
    //
    //   dto-system  in %LOCALAPPDATA%\Acd.Mcp\dto-system\
    //     Owned by the plugin install. Wiped and repopulated on startup.
    //     The user must not edit these — changes are lost on next install.
    //
    //   dto-user    in %APPDATA%\Acd.Mcp\dto-user\
    //     Owned by the user. The plugin never writes here. A same-typed file
    //     here overrides whatever the system folder ships for that type.
    //
    // The split exists so the installer can refresh the shipped DTO set
    // without ever clobbering a user's customisation.
    public static class DtoPaths
    {
        private const string AppFolder = "Acd.Mcp";
        public const string SystemFolderName = "dto-system";
        public const string UserFolderName = "dto-user";

        public static string SystemFolder { get; } = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            AppFolder, SystemFolderName);

        public static string UserFolder { get; } = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            AppFolder, UserFolderName);

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(SystemFolder);
            Directory.CreateDirectory(UserFolder);
        }
    }
}
