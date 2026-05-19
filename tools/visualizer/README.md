# ACD-MCP Entity Visualizer

A self-contained HTML+SVG viewer that takes a JSON dump of model-space entities
from the ACD-MCP REPL and renders a pannable, zoomable top-down picture. The
point: language models reading raw `Point3d (550349.6, 5803457.9, 0)` numbers
have no idea what the drawing *looks* like; this fills that gap with one
screenshot per query.

## Files

- `visualize.html` — single-file viewer. No build, no dependencies. Open it in
  a browser (use a local web server because Chrome blocks `fetch('./...')` on
  `file://`).
- `dump_modelspace.csx` — REPL snippet. Paste into `autocad_script_execute`;
  it produces the JSON shape the viewer reads.
- `example.json` — a dump from a real Civil 3D sewage drawing for sanity-check.
- `sample_render.png` — what the example renders to.

## Quick start

1. In the running AutoCAD, paste the body of `dump_modelspace.csx` into the
   ACD-MCP REPL. Either capture the returned JSON via `returnValueJson`, or
   add a `File.WriteAllText(...)` line at the end to save it next to
   `visualize.html` as `example.json`.

2. From this folder, serve the page:
   ```
   python -m http.server 8765
   ```

3. Open `http://localhost:8765/visualize.html`.

4. Click **Example** (or drop a JSON file in the picker, or **Paste JSON** from
   the clipboard).

5. Pan = drag, zoom = wheel, **F** = fit, click a layer row to hide/show it.

## JSON contract

The viewer expects (each field optional except `entities`):

```jsonc
{
  "extents":      [xmin, ymin, xmax, ymax],
  "layer_colors": { "<layer-name>": <aci 1..255> },
  "entity_count": 2582,
  "truncated":    false,
  "entities":     [ /* one entry per entity, shapes below */ ]
}
```

Entity shapes (`t` = type discriminator):

```jsonc
{ "t": "line",     "l": "<layer>", "ci": <aci>, "x1":, "y1":, "x2":, "y2": }
{ "t": "circle",   "l":, "ci":, "x":, "y":, "r": }
{ "t": "arc",      "l":, "ci":, "x":, "y":, "r":, "a1":, "a2": }       // radians
{ "t": "ellipse",  "l":, "ci":, "x":, "y":, "mx":, "my":, "rr":, "a1":, "a2": }
{ "t": "polyline", "l":, "ci":, "closed": false, "pts": [[x,y], ...] }
{ "t": "spline",   "l":, "ci":, "pts": [[x,y], ...] }                  // control pts
{ "t": "text",     "l":, "ci":, "x":, "y":, "s":, "h":, "rot": }       // rot in radians
{ "t": "bref",     "l":, "ci":, "x":, "y":, "rot":, "sx":, "sy":, "name": }
{ "t": "hatch",    "l":, "ci":, "pattern": "ANSI31" }                  // glyph only
{ "t": "point",    "l":, "ci":, "x":, "y": }
{ "t": "unknown",  "l":, "k": "<type-name>" }                          // fallback
```

`ci = 0` (ByBlock) or `>= 256` (ByLayer) falls back to the layer's color.

## Known limits

- Hatch boundaries are not exported — hatches show as a single dot marker.
  Adding boundary data is a per-loop walk through `Hatch.GetLoopAt(i)`.
- BlockReferences render as an X marker plus the block name, not the actual
  block geometry. Expanding requires recursing into the block's
  `BlockTableRecord`. Reasonable next step.
- 3D entities are flattened to XY only.
