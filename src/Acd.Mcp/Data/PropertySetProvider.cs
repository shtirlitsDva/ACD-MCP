using System.Collections;
using System.Reflection;
using Acd.Mcp.Batch;
using Autodesk.AutoCAD.DatabaseServices;

namespace Acd.Mcp.Data
{
    // Wraps the AECC PropertyData APIs (Civil 3D / Map 3D / MEP) without a
    // build-time reference to AeccBaseMgd. The plugin must work on vanilla
    // AutoCAD where those assemblies do not load, so we resolve the surface
    // reflectively at construction:
    //
    //   PropertyDataServices.GetPropertySets(Entity) → ObjectIdCollection
    //   PropertySet { PropertySetDefinition, PropertyNameToId, GetPropertySetDataAt }
    //   PropertySetDefinition.Definitions → PropertyDefinitionCollection (IEnumerable)
    //   PropertyDefinition.Name
    //   PropertySetData.GetData()
    //
    // If any member is missing the provider becomes a no-op — every TryRead
    // returns Failure, every ReadAll returns empty. The factory ([[entity-data-providers]])
    // checks `IsAvailable` and skips registration when the AECC stack is
    // absent, so the composite never even sees this provider on vanilla.
    public sealed class PropertySetProvider : IEntityDataProvider
    {
        private readonly bool _available;
        private readonly MethodInfo? _getPropertySets;
        private readonly PropertyInfo? _propertySetDefinitionProp;
        private readonly MethodInfo? _propertyNameToId;
        private readonly MethodInfo? _getPropertySetDataAt;
        private readonly PropertyInfo? _definitionsProp;
        private readonly PropertyInfo? _propertyDefinitionNameProp;
        private readonly MethodInfo? _propertySetDataGetData;

        public bool IsAvailable => _available;

        public PropertySetProvider()
        {
            try
            {
                var aec = LoadAecAssembly();
                if (aec is null)
                {
                    SafeBoundary.Info("PropertySetProvider",
                        "AECC managed assembly not loaded (AecBaseMgd / AeccBaseMgd). PropertySets disabled — vanilla AutoCAD.");
                    return;
                }

                var missing = new List<string>();
                var pds = ResolveType(aec, "Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices", missing);
                var ps = ResolveType(aec, "Autodesk.Aec.PropertyData.DatabaseServices.PropertySet", missing);
                var psDef = ResolveType(aec, "Autodesk.Aec.PropertyData.DatabaseServices.PropertySetDefinition", missing);
                var psData = ResolveType(aec, "Autodesk.Aec.PropertyData.DatabaseServices.PropertySetData", missing);
                var pDef = ResolveType(aec, "Autodesk.Aec.PropertyData.DatabaseServices.PropertyDefinition", missing);

                if (pds is null || ps is null || psDef is null || psData is null || pDef is null)
                {
                    SafeBoundary.Info("PropertySetProvider",
                        $"AECC types missing in {aec.GetName().Name}: {string.Join(", ", missing)}. PropertySets disabled.");
                    return;
                }

                _getPropertySets = ResolveMethod(pds, "GetPropertySets", new[] { typeof(Entity) }, missing);
                _propertySetDefinitionProp = ResolveProperty(ps, "PropertySetDefinition", missing);
                _propertyNameToId = ResolveMethod(ps, "PropertyNameToId", new[] { typeof(string) }, missing);
                _getPropertySetDataAt = ResolveMethod(ps, "GetPropertySetDataAt", new[] { typeof(int) }, missing);
                _definitionsProp = ResolveProperty(psDef, "Definitions", missing);
                _propertyDefinitionNameProp = ResolveProperty(pDef, "Name", missing);
                _propertySetDataGetData = ResolveMethod(psData, "GetData", Type.EmptyTypes, missing);

                _available =
                    _getPropertySets is not null &&
                    _propertySetDefinitionProp is not null &&
                    _propertyNameToId is not null &&
                    _getPropertySetDataAt is not null &&
                    _definitionsProp is not null &&
                    _propertyDefinitionNameProp is not null &&
                    _propertySetDataGetData is not null;

                if (!_available)
                {
                    SafeBoundary.Info("PropertySetProvider",
                        $"AECC member resolution incomplete: {string.Join(", ", missing)}. PropertySets disabled.");
                }
                else
                {
                    SafeBoundary.Info("PropertySetProvider", $"AECC PropertySets available via {aec.GetName().Name}.");
                }
            }
            catch (Exception ex)
            {
                SafeBoundary.Info("PropertySetProvider",
                    $"Reflection probe threw: {ex.GetType().Name}: {ex.Message}. PropertySets disabled.");
                _available = false;
            }
        }

        private static Type? ResolveType(Assembly asm, string fullName, List<string> missing)
        {
            var t = asm.GetType(fullName);
            if (t is null) missing.Add($"type:{fullName}");
            return t;
        }

        private static MethodInfo? ResolveMethod(Type t, string name, Type[] sig, List<string> missing)
        {
            var m = t.GetMethod(name, sig);
            if (m is null) missing.Add($"method:{t.Name}.{name}");
            return m;
        }

        private static PropertyInfo? ResolveProperty(Type t, string name, List<string> missing)
        {
            var p = t.GetProperty(name);
            if (p is null) missing.Add($"prop:{t.Name}.{name}");
            return p;
        }

        public Outcome<string> TryRead(Entity entity, Transaction tx, string key)
        {
            if (!_available) return Outcome<string>.Fail("PropertySets unavailable on this AutoCAD vertical.");

            try
            {
                var psIds = (ObjectIdCollection?)_getPropertySets!.Invoke(null, new object[] { entity });
                if (psIds is null || psIds.Count == 0)
                    return Outcome<string>.Fail("Entity has no property sets.");

                foreach (ObjectId id in psIds)
                {
                    var psObj = tx.GetObject(id, OpenMode.ForRead);
                    int propId;
                    try { propId = (int)_propertyNameToId!.Invoke(psObj, new object[] { key })!; }
                    catch { continue; }
                    if (propId < 0) continue;

                    var dataObj = _getPropertySetDataAt!.Invoke(psObj, new object[] { propId });
                    if (dataObj is null) continue;

                    var value = _propertySetDataGetData!.Invoke(dataObj, null);
                    return Outcome<string>.Ok(value?.ToString() ?? string.Empty);
                }
                return Outcome<string>.Fail($"Property '{key}' not found in any property set.");
            }
            catch (Exception ex)
            {
                return Outcome<string>.Fail($"PropertySet read failed: {ex.Message}", ex);
            }
        }

        public IReadOnlyDictionary<string, string> ReadAll(Entity entity, Transaction tx)
        {
            if (!_available) return Empty;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psIds = (ObjectIdCollection?)_getPropertySets!.Invoke(null, new object[] { entity });
                if (psIds is null || psIds.Count == 0) return Empty;

                foreach (ObjectId id in psIds)
                {
                    var psObj = tx.GetObject(id, OpenMode.ForRead);
                    var psDefId = (ObjectId)_propertySetDefinitionProp!.GetValue(psObj)!;
                    var psDefObj = tx.GetObject(psDefId, OpenMode.ForRead);
                    var defs = _definitionsProp!.GetValue(psDefObj) as IEnumerable;
                    if (defs is null) continue;

                    foreach (var def in defs)
                    {
                        if (_propertyDefinitionNameProp!.GetValue(def) is not string name) continue;

                        int propId;
                        try { propId = (int)_propertyNameToId!.Invoke(psObj, new object[] { name })!; }
                        catch { continue; }

                        var dataObj = _getPropertySetDataAt!.Invoke(psObj, new object[] { propId });
                        if (dataObj is null) continue;

                        var value = _propertySetDataGetData!.Invoke(dataObj, null);
                        map[name] = value?.ToString() ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Partial dictionary is more useful than nothing.
            }
            return map;
        }

        private static Assembly? LoadAecAssembly()
        {
            // The AECC managed assemblies follow the naming pattern AecBaseMgd /
            // AeccBaseMgd / AeccDbMgd depending on the AutoCAD vertical. We
            // require AecBaseMgd (PropertyData lives there) — accept any
            // already-loaded name match.
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a =>
                {
                    var name = a.GetName().Name;
                    return name is not null &&
                        (name.Equals("AecBaseMgd", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("AeccBaseMgd", StringComparison.OrdinalIgnoreCase));
                });
        }

        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>();
    }
}
