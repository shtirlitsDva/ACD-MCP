using System.Diagnostics;
using System.IO;

namespace Acd.Mcp.Serialization
{
    // Bootstraps %LOCALAPPDATA%\Acd.Mcp\dto-system\ from the embedded resource
    // set shipped inside the plugin DLL. Mirrors the installer contract:
    //
    //   * The system folder is wiped and repopulated on every Seed() call.
    //   * The user folder (dto-user) is left strictly alone.
    //
    // Once a real installer ships (see future-plugin-distribution.md), this
    // seeder becomes the no-op fallback for dev / sideload scenarios.
    //
    // Resource naming: MSBuild names a `Resources\DtoSystem\circle.csx`
    // resource as `Acd.Mcp.Resources.DtoSystem.circle.csx`. We strip the
    // common prefix and write the leaf filename.
    public static class DtoSystemSeeder
    {
        private const string ResourcePrefix = "Acd.Mcp.Resources.DtoSystem.";

        public static void Seed()
        {
            try
            {
                var target = DtoPaths.SystemFolder;
                Directory.CreateDirectory(target);

                // Wipe existing — installer-contract semantics.
                foreach (var path in Directory.EnumerateFiles(target, "*.csx"))
                {
                    try { File.Delete(path); } catch { /* best-effort */ }
                }

                var asm = typeof(DtoSystemSeeder).Assembly;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, System.StringComparison.Ordinal)) continue;

                    var fileName = name.Substring(ResourcePrefix.Length);
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream is null) continue;

                    var outPath = Path.Combine(target, fileName);
                    using var fs = File.Create(outPath);
                    stream.CopyTo(fs);
                }
            }
            catch (System.Exception ex)
            {
                // Seeding failure shouldn't take down the plugin — the user
                // can still author DTOs in dto-user/.
                Trace.WriteLine($"[DtoSystemSeeder] Seed failed: {ex.Message}");
            }
        }
    }
}
