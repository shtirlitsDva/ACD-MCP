using System.IO;
using Xunit;

namespace Acd.Mcp.Tests
{
    // Guards against drift back to the pre-v0.3.0 tool names. The bridge
    // tool surface settled on `autocad_script_execute` / `autocad_script_propose`
    // when the SCRIPT/REPL refactor shipped; the old names lingered in the
    // deployed bin and skill docs and caused source-vs-binary drift (see
    // docs/review/fragility-review-2026-05-18.md, finding-5).
    //
    // This test scans every Bridge tool *.cs file and fails if a retired
    // tool name reappears. Cheap belt-and-suspenders so a future agent
    // (or a careless merge) cannot reintroduce the drift silently.
    public class ToolNameRegressionTests
    {
        private static readonly string[] RetiredToolNames =
        {
            "autocad_execute_csharp",
            "autocad_repl_propose_script",
        };

        [Fact]
        public void BridgeTools_DoNotReferenceRetiredNames()
        {
            var toolsDir = ResolveToolsDir();
            Assert.True(Directory.Exists(toolsDir),
                $"Expected Bridge Tools dir at '{toolsDir}'.");

            foreach (var file in Directory.EnumerateFiles(toolsDir, "*.cs", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(file);
                foreach (var retired in RetiredToolNames)
                {
                    Assert.False(text.Contains(retired),
                        $"Retired tool name '{retired}' reappeared in {Path.GetFileName(file)}.");
                }
            }
        }

        private static string ResolveToolsDir()
        {
            // Walk up from the test bin until we find the repo root (presence
            // of Acd.Mcp.sln), then append the known relative path. Robust
            // against build-output dir changes and CI working-dir surprises.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Acd.Mcp.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "Acd.Mcp.Bridge", "Tools");
        }
    }
}
