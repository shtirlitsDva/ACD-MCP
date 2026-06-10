using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.UI;

namespace Rvt.Mcp.Loader
{
    // The .addin-registered entry point. Deliberately dependency-free: its
    // only job is to load Rvt.Mcp.dll (Roslyn and all) into a private
    // AssemblyLoadContext so our Microsoft.CodeAnalysis 4.12 can never
    // collide with whatever Roslyn other add-ins preloaded into the default
    // context (pyRevit ships 4.11 on this machine — the journal logged the
    // version conflict and the engine died on first use).
    //
    // RevitAPI/RevitAPIUI intentionally fall through to the default context,
    // so the inner RvtMcpApp implements the SAME IExternalApplication —
    // the cast below is type-identical, no reflection forwarding needed.
    public sealed class LoaderApp : IExternalApplication
    {
        private IExternalApplication? _inner;

        public Result OnStartup(UIControlledApplication application)
        {
            string dir = Path.GetDirectoryName(typeof(LoaderApp).Assembly.Location)!;
            string enginePath = Path.Combine(dir, "Rvt.Mcp.dll");

            var context = new RvtMcpLoadContext(enginePath);
            Assembly engine = context.LoadFromAssemblyPath(enginePath);

            Type appType = engine.GetType("Rvt.Mcp.RvtMcpApp")
                ?? throw new InvalidOperationException("Rvt.Mcp.RvtMcpApp not found in engine assembly.");
            _inner = (IExternalApplication)Activator.CreateInstance(appType)!;
            return _inner.OnStartup(application);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return _inner?.OnShutdown(application) ?? Result.Succeeded;
        }
    }

    // Non-collectible isolation context: everything next to Rvt.Mcp.dll
    // (Roslyn, System.* uplifts) resolves locally; anything we don't carry
    // (RevitAPI*, framework) returns null and falls through to the default
    // context. Same fall-through pattern as DevReload's IsolatedPluginContext,
    // minus collectibility — the REPL session lives for the Revit session.
    internal sealed class RvtMcpLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _dir;

        public RvtMcpLoadContext(string enginePath) : base("Rvt.Mcp")
        {
            _resolver = new AssemblyDependencyResolver(enginePath);
            _dir = Path.GetDirectoryName(enginePath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? "";

            // Revit API assemblies must keep default-context identity.
            if (name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase))
                return null;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path == null)
            {
                string sideBySide = Path.Combine(_dir, name + ".dll");
                if (File.Exists(sideBySide)) path = sideBySide;
            }

            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
