using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rvt.Mcp.Tests
{
    // RevitAPI.dll is managed but pulls native Revit DLLs on load. Point the
    // loader at the install dir BEFORE any test method referencing Revit
    // types is JITted. Without this every typed-converter test dies with
    // "module not found" even though RevitAPI.dll itself was copied local.
    internal static class TestNativeSetup
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        [ModuleInitializer]
        internal static void Init()
        {
            string revitDir = Environment.GetEnvironmentVariable("RVT_MCP_TEST_REVIT_DIR")
                ?? @"C:\Program Files\Autodesk\Revit 2025";
            if (Directory.Exists(revitDir))
            {
                SetDllDirectoryW(revitDir);
                Environment.SetEnvironmentVariable(
                    "PATH",
                    revitDir + ";" + Environment.GetEnvironmentVariable("PATH"));
            }
        }
    }
}
