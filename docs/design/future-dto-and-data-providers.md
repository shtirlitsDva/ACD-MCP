<!--
DTO + entity-data-provider spec — extracted from the /acd-batch design pass
because it is a different concern with a different audience.

NOT for the batch-implementation agent. This is the serialization layer used
by the existing REPL `autocad_execute_csharp` tool. Independent of batch.
-->

<status>idea / spec — not implemented, not scheduled, not assigned</status>

<motivation>
`autocad_execute_csharp` returns garbage by default when an AutoCAD entity is
the script's last expression — hundreds of properties, recursive graphs,
binary blobs. DTOs solve this: a curated projection per entity type, written
as a Roslyn-compiled `JsonConverter<T>`.

The same mechanism wraps two related concerns:

1. **Type-specific projection** — Circle → { center, radius, layer, color }.
2. **Cross-storage metadata access** — read a "PartNumber" or "Zone" attribute
   regardless of whether the user stored it in Block Attributes, AECC
   PropertySets, or XData. This needs an abstraction layer beneath the DTO.
</motivation>

<scope>
This document describes:

* The DTO file format and registration mechanism.
* The two-tier system/user DTO folder layout.
* The `IEntityDataProvider` abstraction over Block Attributes, PropertySets,
  and XData.
* The `acd-mcp-add-dto` skill that teaches an agent how to extend the set.

Does NOT describe:

* The batch feature (see `future-acd-batch.md`).
* Plugin distribution / install (see `future-plugin-distribution.md`).
</scope>

<dto-file-format>
One DTO per file. Filename matches the type (lowercase). Header comment
declares the target type:

  // @dto: Autodesk.AutoCAD.DatabaseServices.Circle

The body uses a registration helper that the runtime injects into the
script's globals:

  Acad.RegisterDto<Circle>(c => new
  {
      center = c.Center,
      radius = c.Radius,
      layer  = c.Layer,
      color  = c.Color.ColorIndex,
  });

The registration adds a `JsonConverter<Circle>` to a shared
`JsonSerializerOptions` instance. The REPL's response serializer uses that
instance whenever it writes an `ExecuteResult.ReturnValue`.

One file per type, no exceptions. The implementation must NOT support
"register multiple DTOs in one file" — per-file granularity is what makes
the system+user override pattern work cleanly.
</dto-file-format>

<two-tier-folders>
  %LOCALAPPDATA%\Acd.Mcp\dto-system\        ← installer manages this folder
                                              completely. Wiped + repopulated
                                              on every plugin install.

  %APPDATA%\Acd.Mcp\dto-user\               ← user / agent writes here.
                                              Installer NEVER touches.
                                              Overrides any same-typed DTO
                                              in dto-system.

Resolution order at serializer-time:
  1. Look in `dto-user/`. If a converter for type T exists, use it.
  2. Otherwise fall back to `dto-system/`.
  3. Otherwise emit the `{ "$unsupported": "TypeName" }` marker (see below).

Rationale: the installer overwrites `dto-system/` on update so shipped DTOs
can improve over time. User-authored DTOs (or agent-written ones) live in
`dto-user/` and are sacred — never overwritten, always preferred.
</two-tier-folders>

<implicit-reload>
**No reload button.** At serialization time, if the runtime is asked to
serialise an entity whose runtime type has no registered converter, the
serializer:

1. Rescans both folders (`dto-user/` first, `dto-system/` second).
2. Recompiles any `.csx` files that changed since last scan.
3. Retries serialisation against the freshly-registered converters.
4. If still no match, emits `{ "$unsupported": "<full type name>" }`.

The `$unsupported` marker is the agent's hook to action — it knows it
should write a DTO (via the `acd-mcp-add-dto` skill) or ask the user to.

File watcher is a "nice to have" optimisation; the on-demand rescan is
the load-bearing mechanism. The watcher just lets a freshly-saved user
DTO show up the moment the user hits save instead of on next call.
</implicit-reload>

<starter-set>
Shipped in `dto-system/` at install time:

  Geometry primitives:
      DBPoint, Point2d, Point3d, Vector2d, Vector3d, Extents2d, Extents3d, ObjectId, Handle

  Vertices (Polyline3d uses Vertex3d, not raw points):
      Vertex2d, Vertex3d

  Text:
      DBText, MText

  Entities:
      Circle, Line, Arc, Polyline, Polyline3d, Hatch, BlockReference

  AttributeReference (so BlockReference DTOs can include attribute values).

The agent extends this list on demand — see `<skill-contract>`.
</starter-set>

<entity-data-provider-abstraction>
A user-defined metadata key — "PartNumber", "Zone", "TagID" — may live in
any of three storage mechanisms depending on the domain:

* **Block Attributes**       — `BlockReference.AttributeCollection` →
                                `AttributeReference` per tag.
                                Only on `BlockReference`.

* **PropertySets** (AECC)    — `Autodesk.Aec.PropertyData.*` APIs.
                                Available on **any entity**. Used by
                                Civil 3D / Map / MEP users heavily.

* **XData**                  — extended-entity-data, registered apps
                                with typed values. Available on any
                                entity. Older mechanism, niche use.

A DTO that just reads `block.AttributeCollection` is incorrect for users
who store the same data in PropertySets — and PropertySets are the
mainstream Civil 3D mechanism. The abstraction:

  public interface IEntityDataProvider
  {
      // Try to read a single key.
      Outcome<string> TryRead(Entity ent, Transaction tx, string key);

      // Read everything this provider knows about for an entity.
      IReadOnlyDictionary<string, string> ReadAll(Entity ent, Transaction tx);
  }

Concrete implementations:

  BlockAttributeProvider     — `BlockReference.AttributeCollection`. Returns
                                empty for non-BlockReference entities.
                                Trivial.

  PropertySetProvider        — wraps the AECC PropertyData APIs.
                                Universal (any entity). **Civil-only**:
                                requires the AEC managed assemblies which
                                ship with Civil 3D / Map 3D, not vanilla
                                AutoCAD. Detect-and-disable when the
                                assemblies are absent.

  XDataProvider              — wraps XData. Interface present in v1, full
                                implementation deferred (the XDataProvider
                                class exists and throws `NotSupportedException`
                                with a clear "not yet implemented" message).
                                Including the interface upfront means
                                downstream code doesn't have to be retrofitted
                                later.

A `CompositeDataProvider` chains the registered providers in registration
order and returns the first hit. DTOs use the composite by default:

  Acad.RegisterDto<BlockReference>(br => new
  {
      name          = br.Name,
      layer         = br.Layer,
      position      = br.Position,
      attributes    = Acad.DataProvider.ReadAll(br, tx),
      // attributes now includes block attrs + property sets transparently
  });
</entity-data-provider-abstraction>

<reference-implementations>
The user has battle-tested utilities for both PropertySets and a generic
"flex data store" abstraction. Reference (do NOT copy verbatim — the user
called their own code "rookie"; lift the API insights, not the style):

* PropertySets manager:
  `X:\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\UtilitiesCommonSHARED\PropertySets\PropertySetManager.cs`

* Generic data-store interface (pattern hint):
  `X:\GitHub\shtirlitsDva\Autocad-Civil3d-Tools\Acad-C3D-Tools\AutoCADCommandsSHARED\FlexDataStore.cs`

Re-derive a clean abstraction from these; don't reproduce the existing
shapes.
</reference-implementations>

<civil-only-detection>
The PropertySetProvider only works when the AEC managed assemblies
(`AeccDbMgd.dll`, etc.) are present. These ship with Civil 3D, Map 3D,
and MEP — not vanilla AutoCAD.

Detection strategy: at provider registration, attempt to load the
`Autodesk.Aec.PropertyData` namespace via reflection. If the type loads,
register the provider. If not, log "PropertySets unavailable — vanilla
AutoCAD detected" and proceed without it. The composite continues to
work with the remaining providers.

Never throw at startup just because the AEC stack is missing.
</civil-only-detection>

<skill-contract>
A skill named `acd-mcp-add-dto` ships with the plugin and is the agent's
guide for extending the DTO set. The skill teaches:

1. **Folder discipline.** `dto-user/` is where new DTOs go. Never write to
   `dto-system/` (it's overwritten on plugin update). Never edit a DTO
   in `dto-system/` — if you need to override one, create a same-typed
   file in `dto-user/`.

2. **One type per file.** Filename = lowercase type name, e.g. `circle.csx`,
   `block-reference.csx`. Multi-type files are not supported.

3. **Verify, never guess.** Before referencing a property of an AutoCAD
   type, confirm it exists. Acceptable verification methods:
   - Read the AutoCAD .NET API docs (Context7, official Autodesk docs).
   - Probe the live type via `autocad_execute_csharp` —
     `typeof(Circle).GetProperties().Select(p => p.Name).ToList()`.
   - Examine the metadata of an actual instance:
     `var c = ...; c.GetType().GetProperties()...`.
   **Never write a DTO that references a property without verification.**
   The user is emphatic on this: GUESSING IS FORBIDDEN.

4. **Composite data provider is the default.** When writing a DTO for an
   entity type that carries metadata (PropertySets, attributes), use
   `Acad.DataProvider.ReadAll(ent, tx)` rather than reading one mechanism
   directly. The serializer doesn't know whether the user stores their
   metadata in attributes or property sets.

5. **Test the DTO.** After writing a DTO, the agent should immediately
   run a probe — `select(c => c)` on an instance — and verify the JSON
   shape via `autocad_execute_csharp`. If the output looks wrong, fix
   the DTO before declaring done.

6. **Naming.** DTOs use lowercase property names with snake-case for
   multi-word concepts (`color_index`, not `ColorIndex`). Consistency
   across all DTOs matters for an agent constructing queries.

The skill itself is a single SKILL.md file under
`.claude-plugin/skills/acd-mcp-add-dto/` in the plugin layout.
</skill-contract>

<learning-source-policy>
The agent's primary knowledge source for the DTO system is the
`acd-mcp-add-dto` skill, not the MCP tool descriptions. Tool descriptions
remain concise — they advertise the tool's signature and a one-line
summary. Detailed guidance (the rules above, examples, anti-patterns) lives
in the skill markdown which the agent loads on demand when it needs to
write a DTO.

This pattern generalises: every cross-cutting practice the agent should
follow lives in a dedicated skill, not in inflated tool descriptions.
</learning-source-policy>

<open-research-items>
1. PropertySetManager.cs API: study the actual public surface before
   designing `PropertySetProvider`. The user's implementation may already
   answer the "how do I read a property set programmatically" question
   cleanly.

2. XData implementation deferral: confirm with the user that v1 ships the
   interface only. If they need real XData support for their existing
   workflow, escalate.

3. JsonSerializerOptions sharing: the existing REPL response path may use
   default System.Text.Json options. Adding a shared options instance
   carrying DTOs may require touching `Acd.Mcp.Pipe.FrameIO.JsonOptions`.
   Confirm the integration point before changing.

4. Performance: rescan-on-miss is O(folder-size). Probably fine for v1
   (DTOs are small files), but if the folder grows large (hundreds), a
   smarter cache (filename → registered type, invalidated on file mtime
   change) is warranted. Defer until measured.
</open-research-items>

<not-now>
Implementing this depends on the existing REPL being verified end-to-end
in a real MCP client, and on stable per-type tests. Revisit after the
batch feature ships and the REPL has demonstrated need.
</not-now>
