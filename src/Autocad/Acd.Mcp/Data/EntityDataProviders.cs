namespace Acd.Mcp.Data
{
    // Factory that assembles the composite the rest of the plugin uses.
    //
    // Order:
    //   1. BlockAttributeProvider — narrow, cheap, only fires for BlockReference.
    //   2. PropertySetProvider    — universal but Civil-only. Registered ONLY if
    //                               the AECC managed assemblies are loaded.
    //                               XData is intentionally not in the composite
    //                               yet; it throws on use, and silently routing
    //                               every call through a throwing provider would
    //                               hide the deferral.
    //
    // The "skip when unavailable" rule means a vanilla-AutoCAD user gets a
    // composite of one (BlockAttribute), and that's fine — their workflow
    // doesn't include PropertySets.
    public static class EntityDataProviders
    {
        public static IEntityDataProvider CreateDefault()
        {
            var list = new System.Collections.Generic.List<IEntityDataProvider>
            {
                new BlockAttributeProvider(),
            };

            var ps = new PropertySetProvider();
            if (ps.IsAvailable) list.Add(ps);

            return new CompositeDataProvider(list);
        }
    }
}
