---
name: acd-mcp-add-dto
description: Author or override a DTO that teaches the ACD-MCP serializer how to project an AutoCAD entity type to JSON. Triggers when the MCP returns {"$unsupported":"<type>"}, when a default DTO's shape is insufficient, or when the user asks "add a DTO for X".
---

<purpose>
The ACD-MCP serializer projects each AutoCAD entity to JSON through a DTO file. A DTO is a one-file C# script that calls `Acd.RegisterDto<T>(t => new { ... })` to declare the JSON shape. Without a DTO, the serializer emits the marker `{"$unsupported":"FullTypeName"}` so the agent (you) knows it must author one.

This skill walks you through writing a correct DTO. The rules are non-negotiable — getting them wrong silently produces invalid JSON or overwrites user customisation.
</purpose>

<where-dtos-live>
Two folders. The split exists so a future plugin update can refresh the shipped DTO set without ever clobbering user authoring.

- **System folder** — `%LOCALAPPDATA%\Acd.Mcp\dto-system\`
  Plugin-owned. Wiped and repopulated on every plugin install. **Never edit these.** Your changes will be lost. To override a system DTO, create a same-typed file in the user folder instead.

- **User folder** — `%APPDATA%\Acd.Mcp\dto-user\`
  Your folder. Plugin never touches it. A same-typed DTO here overrides whatever the system folder ships. **All new authoring goes here.**

Resolution order at serialisation time: user folder first, then system folder, then the `$unsupported` marker. So `dto-user/circle.csx` beats `dto-system/circle.csx`.
</where-dtos-live>

<one-type-per-file>
**Filename matches the type, lowercased.** Examples:
- `Autodesk.AutoCAD.DatabaseServices.Circle` → `circle.csx`
- `Autodesk.AutoCAD.DatabaseServices.BlockReference` → `blockreference.csx`
- `Autodesk.AutoCAD.DatabaseServices.PolylineVertex3d` → `polylinevertex3d.csx`

**One `RegisterDto<T>` call per file.** Do not write an `entities.csx` that registers ten types. Per-file granularity is what lets a user override a single type via the user folder. Multiple registrations in one file break that contract.

**Header is mandatory.** First non-blank line of the file:

```
// @dto: Autodesk.AutoCAD.DatabaseServices.Circle
```

The header is for diagnostics — when compilation fails, the loader logs "compile error in <file> targeting <header type>" so the failure is easy to triage. The body's `RegisterDto<T>` is what actually registers; do not rely on the header doing anything functional.
</one-type-per-file>

<verify-do-not-guess>
**GUESSING IS FORBIDDEN.** Before referencing any property of an AutoCAD type, confirm the property exists with the exact spelling. The user is emphatic on this — a DTO that calls a non-existent property fails at script-compile time, silently disables that type's DTO, and the serializer emits `$unsupported` again with no clear cause.

Three acceptable verification paths:

1. **Probe the live type via the MCP.** Run inside `autocad_execute_csharp`:

   ```csharp
   typeof(Circle).GetProperties().Select(p => p.Name).ToList()
   ```

   Returns the actual property list. Authoritative.

2. **Examine an instance.** If you have a representative object:

   ```csharp
   var c = ...; c.GetType().GetProperties().Select(p => p.Name).ToList()
   ```

3. **Read official Autodesk API docs.** Context7 has the AutoCAD .NET API documented. Search "AutoCAD Managed API <ClassName>".

Never write a DTO referencing a property you have not verified. The user has caught this anti-pattern in the past and called it out specifically.
</verify-do-not-guess>

<the-projection>
The projection is a `Func<T, object?>` — it takes the typed value and returns an anonymous object whose shape becomes the JSON. Names in the projection are snake_case (or Pascal, transformed automatically by the serializer). Example:

```csharp
// @dto: Autodesk.AutoCAD.DatabaseServices.Circle

Acd.RegisterDto<Circle>(c => new
{
    center = c.Center,
    radius = c.Radius,
    normal = c.Normal,
    layer = c.Layer,
    color_index = c.Color.ColorIndex,
});
```

**Reduce AutoCAD types to primitives at the leaf** — `c.Color.ColorIndex` (a `short`) rather than `c.Color` (an `Autodesk.AutoCAD.Colors.Color` object that has its own representation concerns). Each AutoCAD type the projection emits has to itself have a DTO; the closer you stay to primitives at the leaves, the fewer DTOs you transitively need.

**Geometry primitives are already covered.** `Point2d`, `Point3d`, `Vector2d`, `Vector3d`, `Extents2d`, `Extents3d`, `ObjectId`, `Handle` — these ship in the system folder. Just include the property; the serializer will project it correctly.
</the-projection>

<entity-metadata>
For entity-attached metadata (block attributes, PropertySets on Civil 3D, eventually XData), use the data provider — never read one mechanism directly:

```csharp
attributes = Acd.DataProvider.ReadAll(br)
```

Why: a user who stores `PartNumber` in block attributes vs PropertySets vs XData should get the same DTO output. Reading one mechanism by hand misses the others. The composite data provider checks every registered mechanism and returns the union.

`Acd.DataProvider.ReadAll(entity)` returns `IReadOnlyDictionary<string, string>`. `Acd.DataProvider.TryRead(entity, key)` returns a single value or null. Both pull the current top transaction from the entity's database; if no transaction is active, they throw — make sure your script is inside a transaction when entity DTO is serialised.
</entity-metadata>

<test-after-writing>
After authoring a DTO, immediately probe it via the MCP. Two checks:

1. **Round-trip a sample value.** Run a script that returns an instance of the type. Verify the JSON shape in the response's `returnValueJson`.

2. **Run the type-listing probe again** to confirm no diagnostics — if the file failed to compile, the registry won't have your type registered and the serializer will still emit `$unsupported`. The `Trace.WriteLine` log (in the plugin's SafeBoundary log file) names the failed file with the compile error.

A DTO that has not been round-trip tested is not done.
</test-after-writing>

<naming>
JSON property names: lowercase, snake_case for multi-word concepts.

- `center`, `radius`, `normal` — good
- `color_index`, `start_angle`, `pattern_name` — good
- `colorIndex`, `Color`, `startAngle` — bad

The serializer applies `JsonNamingPolicy.SnakeCaseLower`, so you may write Pascal names in the projection and they get transformed (`ColorIndex` → `color_index`). Either style works; mix is fine. Consistency across DTOs matters for an agent constructing follow-up queries.
</naming>

<reload-behaviour>
The serializer rescans both folders on every cache miss (rate-limited to 500ms). After you write a new DTO file:

- **No restart required.** Save the file; the next time the serializer encounters that type, it picks up your DTO.
- **No registration step.** The file's mere presence in `dto-user/` (with a valid `Acd.RegisterDto<T>(...)` call) is the registration.

If the rescan does not pick up your file, suspect a compile error — check the plugin's SafeBoundary log for `[DtoLoader] Compile error in user:<filename>`.
</reload-behaviour>

<override-pattern>
To override a system DTO with a richer projection:

1. Copy the system DTO's content from `%LOCALAPPDATA%\Acd.Mcp\dto-system\<file>` to `%APPDATA%\Acd.Mcp\dto-user\<same-file>`.
2. Edit the user copy. The user-folder version wins automatically.
3. **Never edit the system file itself.** It will be overwritten on the next plugin install.

This pattern preserves user authoring across plugin updates and is the entire reason the two-folder split exists.
</override-pattern>

<example-walkthrough>
The MCP returned `{"$unsupported":"Autodesk.AutoCAD.DatabaseServices.RotatedDimension"}`. You need to author a DTO.

**Step 1 — verify properties.**

```csharp
typeof(RotatedDimension).GetProperties()
    .Select(p => $"{p.Name}: {p.PropertyType.Name}")
    .ToList()
```

The agent runs this in `autocad_execute_csharp` and reads the property list: `Measurement`, `Rotation`, `XLine1Point`, `XLine2Point`, `DimLinePoint`, `Layer`, `Color`, …

**Step 2 — write the DTO** at `%APPDATA%\Acd.Mcp\dto-user\rotateddimension.csx`:

```csharp
// @dto: Autodesk.AutoCAD.DatabaseServices.RotatedDimension

Acd.RegisterDto<RotatedDimension>(d => new
{
    measurement = d.Measurement,
    rotation = d.Rotation,
    xline1 = d.XLine1Point,
    xline2 = d.XLine2Point,
    dim_line = d.DimLinePoint,
    layer = d.Layer,
    color_index = d.Color.ColorIndex,
});
```

**Step 3 — test.** Run a script that returns a RotatedDimension instance. The `returnValueJson` should now show the projected shape. Done.
</example-walkthrough>

<anti-patterns>
- **Multiple `RegisterDto` calls in one file.** Breaks per-type override. Always one type per file.
- **Editing `dto-system/`.** Lost on next install. Override via `dto-user/`.
- **Referencing properties without verification.** Silent failure; the file fails to compile and the type stays `$unsupported`.
- **Reading block attributes / PropertySets manually instead of `Acd.DataProvider.ReadAll`.** Misses the cross-mechanism union and breaks for users who store metadata differently than you assumed.
- **Including raw AutoCAD types deep in the projection** (e.g. `c.Color` instead of `c.Color.ColorIndex`). Requires that those types also have DTOs; lean toward primitives at the leaves.
</anti-patterns>
