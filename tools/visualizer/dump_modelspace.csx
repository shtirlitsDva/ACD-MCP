// REPL snippet — paste into autocad_execute_csharp.
// Produces a self-contained JSON describing every entity in model space
// in a shape the SVG visualizer (visualize.html) understands.
//
// v2: hatch boundaries (loop edge sampling) + block-reference expansion.
//
// IMPORTANT: This file is intended to be RUN via the REPL, not loaded as
// a CSharpScript. The "var outPath = ..." line below is where the user
// adjusts the output path before running.

var outPath = @"H:\GitHub\shtirlitsDva\ACD-MCP\tools\visualizer\example.json";
var maxEntities = 6000;

// ─────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────
List<double[]> SampleEllipseArc(Point3d c, Vector3d major, double rr, double a1, double a2, int n)
{
    var pts = new List<double[]>();
    var mAng = Math.Atan2(major.Y, major.X);
    var ma = major.Length;
    var mi = ma * rr;
    for (int i = 0; i <= n; i++)
    {
        var t = a1 + (a2 - a1) * i / n;
        var cx = ma * Math.Cos(t);
        var cy = mi * Math.Sin(t);
        var x = c.X + cx * Math.Cos(mAng) - cy * Math.Sin(mAng);
        var y = c.Y + cx * Math.Sin(mAng) + cy * Math.Cos(mAng);
        pts.Add(new[] { x, y });
    }
    return pts;
}

List<double[]> SampleArc2d(Point2d c, double r, double a1, double a2, bool ccw, int n)
{
    var pts = new List<double[]>();
    if (!ccw) { (a1, a2) = (a2, a1); }
    var sweep = a2 - a1;
    while (sweep < 0) sweep += 2 * Math.PI;
    for (int i = 0; i <= n; i++)
    {
        var t = a1 + sweep * i / n;
        pts.Add(new[] { c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t) });
    }
    return pts;
}

object EncodeEntity(
    Autodesk.AutoCAD.DatabaseServices.Entity ent,
    Transaction tx,
    Matrix3d xform,
    string layerOverride)
{
    short ci = ent.ColorIndex;
    string layer = string.IsNullOrEmpty(layerOverride) ? ent.Layer : layerOverride;

    double[] Tr(double x, double y)
    {
        var p = new Point3d(x, y, 0).TransformBy(xform);
        return new[] { p.X, p.Y };
    }

    switch (ent)
    {
        case Line ln:
            return new {
                t = "line", l = layer, ci = (int)ci,
                x1 = ln.StartPoint.TransformBy(xform).X, y1 = ln.StartPoint.TransformBy(xform).Y,
                x2 = ln.EndPoint.TransformBy(xform).X,   y2 = ln.EndPoint.TransformBy(xform).Y
            };
        case Circle c:
            var cc = c.Center.TransformBy(xform);
            return new {
                t = "circle", l = layer, ci = (int)ci,
                x = cc.X, y = cc.Y, r = c.Radius * Math.Abs(xform.GetScale())
            };
        case Arc a:
            var ac = a.Center.TransformBy(xform);
            return new {
                t = "arc", l = layer, ci = (int)ci,
                x = ac.X, y = ac.Y, r = a.Radius * Math.Abs(xform.GetScale()),
                a1 = a.StartAngle, a2 = a.EndAngle
            };
        case Ellipse e:
            var ec = e.Center.TransformBy(xform);
            return new {
                t = "polyline", l = layer, ci = (int)ci, closed = false,
                pts = SampleEllipseArc(ec, e.MajorAxis, e.RadiusRatio, e.StartAngle, e.EndAngle, 64).ToArray()
            };
        case Polyline p:
            var pts1 = new List<double[]>();
            for (int i = 0; i < p.NumberOfVertices; i++)
            {
                var v = p.GetPoint2dAt(i);
                pts1.Add(Tr(v.X, v.Y));
            }
            return new { t = "polyline", l = layer, ci = (int)ci, closed = p.Closed, pts = pts1.ToArray() };
        case Polyline2d p2:
            var p2pts = new List<double[]>();
            foreach (ObjectId vid in p2)
            {
                var v = (Vertex2d)tx.GetObject(vid, OpenMode.ForRead);
                var pp = v.Position.TransformBy(xform);
                p2pts.Add(new[] { pp.X, pp.Y });
            }
            return new { t = "polyline", l = layer, ci = (int)ci, closed = p2.Closed, pts = p2pts.ToArray() };
        case Polyline3d p3:
            var p3pts = new List<double[]>();
            foreach (ObjectId vid in p3)
            {
                var v = (PolylineVertex3d)tx.GetObject(vid, OpenMode.ForRead);
                var pp = v.Position.TransformBy(xform);
                p3pts.Add(new[] { pp.X, pp.Y });
            }
            return new { t = "polyline", l = layer, ci = (int)ci, closed = p3.Closed, pts = p3pts.ToArray() };
        case DBText dt:
            var dtp = dt.Position.TransformBy(xform);
            return new {
                t = "text", l = layer, ci = (int)ci,
                x = dtp.X, y = dtp.Y, s = dt.TextString, h = dt.Height, rot = dt.Rotation
            };
        case MText mt:
            var mtp = mt.Location.TransformBy(xform);
            return new {
                t = "text", l = layer, ci = (int)ci,
                x = mtp.X, y = mtp.Y, s = mt.Text, h = mt.TextHeight, rot = mt.Rotation
            };
        case Hatch h:
            var loops = new List<double[][]>();
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                var loop = h.GetLoopAt(i);
                var loopPts = new List<double[]>();
                if (loop.IsPolyline)
                {
                    foreach (BulgeVertex bv in loop.Polyline)
                        loopPts.Add(Tr(bv.Vertex.X, bv.Vertex.Y));
                }
                else
                {
                    foreach (Curve2d cv in loop.Curves)
                    {
                        switch (cv)
                        {
                            case LineSegment2d ls:
                                loopPts.Add(Tr(ls.StartPoint.X, ls.StartPoint.Y));
                                loopPts.Add(Tr(ls.EndPoint.X, ls.EndPoint.Y));
                                break;
                            case CircularArc2d arc:
                                foreach (var pt in SampleArc2d(arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle, !arc.IsClockWise, 16))
                                    loopPts.Add(Tr(pt[0], pt[1]));
                                break;
                            case EllipticalArc2d earc:
                                var ma = new Vector3d(earc.MajorAxis.X * earc.MajorRadius, earc.MajorAxis.Y * earc.MajorRadius, 0);
                                var rr = earc.MinorRadius / earc.MajorRadius;
                                foreach (var pt in SampleEllipseArc(new Point3d(earc.Center.X, earc.Center.Y, 0), ma, rr, earc.StartAngle, earc.EndAngle, 16))
                                    loopPts.Add(Tr(pt[0], pt[1]));
                                break;
                            default:
                                // unsupported edge type — skip silently
                                break;
                        }
                    }
                }
                if (loopPts.Count > 0) loops.Add(loopPts.ToArray());
            }
            return new { t = "hatch", l = layer, ci = (int)ci, pattern = h.PatternName, loops = loops.ToArray() };
        case DBPoint dp:
            var dpp = dp.Position.TransformBy(xform);
            return new { t = "point", l = layer, ci = (int)ci, x = dpp.X, y = dpp.Y };
        case Spline sp:
            var ctrl = new List<double[]>();
            for (int i = 0; i < sp.NumControlPoints; i++)
            {
                var c2 = sp.GetControlPointAt(i);
                var p = c2.TransformBy(xform);
                ctrl.Add(new[] { p.X, p.Y });
            }
            return new { t = "spline", l = layer, ci = (int)ci, pts = ctrl.ToArray() };
        default:
            return new { t = "unknown", k = ent.GetType().Name, l = layer };
    }
}

// ─────────────────────────────────────────────────────────
// Main
// ─────────────────────────────────────────────────────────
using (var tx = Db.TransactionManager.StartTransaction())
{
    var bt = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
    var ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
    var lt = (LayerTable)tx.GetObject(Db.LayerTableId, OpenMode.ForRead);

    var layerColors = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
    foreach (ObjectId id in lt)
    {
        var l = (LayerTableRecord)tx.GetObject(id, OpenMode.ForRead);
        layerColors[l.Name] = l.Color.ColorIndex;
    }

    var ents = new List<object>();
    int n = 0;

    void Visit(Autodesk.AutoCAD.DatabaseServices.Entity ent, Matrix3d xform, string layerOverride, int depth)
    {
        if (n >= maxEntities) return;
        if (depth > 3) return;  // don't recurse forever into nested blocks
        if (ent is BlockReference br)
        {
            // Emit a marker entry so the viewer can show "where the block is"
            var bp = br.Position.TransformBy(xform);
            ents.Add(new {
                t = "bref",
                l = string.IsNullOrEmpty(layerOverride) ? br.Layer : layerOverride,
                ci = (int)br.ColorIndex,
                x = bp.X, y = bp.Y,
                rot = br.Rotation, sx = br.ScaleFactors.X, sy = br.ScaleFactors.Y,
                name = ((BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name
            });
            n++;

            // Expand block contents under the bref's transform.
            var brXform = xform * br.BlockTransform;
            var defRec = (BlockTableRecord)tx.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in defRec)
            {
                if (n >= maxEntities) return;
                var sub = tx.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                if (sub is null) continue;
                if (sub is AttributeDefinition) continue;  // template; skip
                // Inherit layer override only when the sub-entity is on layer "0" (the
                // AutoCAD convention for "use parent's layer when inserted in a block").
                var subLO = (sub.Layer == "0") ? (string.IsNullOrEmpty(layerOverride) ? br.Layer : layerOverride) : "";
                Visit(sub, brXform, subLO, depth + 1);
            }
            return;
        }

        var encoded = EncodeEntity(ent, tx, xform, layerOverride);
        ents.Add(encoded);
        n++;
    }

    foreach (ObjectId id in ms)
    {
        if (n >= maxEntities) break;
        if (tx.GetObject(id, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Entity ent)
            Visit(ent, Matrix3d.Identity, "", 0);
    }
    tx.Commit();

    var payload = new {
        extents = new[] { Db.Extmin.X, Db.Extmin.Y, Db.Extmax.X, Db.Extmax.Y },
        layer_colors = layerColors,
        entity_count = ents.Count,
        truncated = ents.Count >= maxEntities,
        entities = ents.ToArray()
    };
    var opts = new System.Text.Json.JsonSerializerOptions {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    var json = System.Text.Json.JsonSerializer.Serialize(payload, opts);
    File.WriteAllText(outPath, json);
    return new { written = outPath, bytes = json.Length, entity_count = ents.Count, truncated = ents.Count >= maxEntities };
}
