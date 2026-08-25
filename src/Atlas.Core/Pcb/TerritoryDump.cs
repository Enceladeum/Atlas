// TerritoryDump - LGB/collision dump engine for one FFXIV territory.
// Ported from LgbDump/DumpCore.cs (golden reference; keep output byte-identical
// with default options). Territory id (or bg level dir) in -> folder of CSV
// sheets (+ optional OBJ) out.
//
// Sheets produced:
//   bg.csv, planmap.csv, planevent.csv, planlive.csv, planner.csv, sound.csv, vfx.csv
//     - one row per LGB instance object, transform + type-specific Extra column
//   collision.csv     - unified collider list (terrain tiles, bg part pcbs incl. derived,
//                       CollisionBox/analytic shapes, SGB-nested colliders) with world
//                       transform and world AABB
//   pcb-meshes.csv    - every referenced .pcb file: version, nodes, verts, tris, local AABB
//   collision-mesh.obj (optional) - all collision geometry, world space, grouped per collider
//
// Rotation convention: local = S * Rx * Ry * Rz * T (System.Numerics row-vector),
// world = local * parentWorld. Matches single-level LGB placement; nested SGB chains
// with non-Y rotation may deviate slightly in axis order.
//
// Additive upgrades (flag-gated; default output stays golden-identical):
//   TriGroups     - split each collider's OBJ faces into per-triangle-class groups
//                   (_invis/_unland/_wall/_floor) using the per-tri material u64
//   ModelBoxBounds- ModelBox colliders get real geometry from the referenced .mdl
//                   bounding box (collision.csv world AABB + 12-tri OBJ box)

using System.Globalization;
using System.Numerics;
using System.Text;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace Atlas.Core.Pcb;

public sealed class TerritoryDumpOptions
{
    /// <summary>Write collision-mesh.obj (world space, one group per collider).</summary>
    public bool ExportObj;
    /// <summary>With ExportObj: split OBJ faces per collider into triangle-class groups
    /// (_invis/_unland/_wall/_floor) from the per-tri material u64.</summary>
    public bool TriGroups;
    /// <summary>ModelBox colliders: read the referenced .mdl bounding box and emit real
    /// AABB extents in collision.csv + a bbox-sized 12-tri box in the OBJ.</summary>
    public bool ModelBoxBounds;
}

public static class TerritoryDump
{
    static string C(string s) => Csv.Escape(s);
    static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    // ---------- collider accumulation ----------
    sealed class Collider
    {
        public string Source = "", LgbFile = "", LayerName = "", Kind = "", PcbPath = "", ParentChain = "";
        public uint LayerId; public ulong InstanceId;
        public Vector3 T, Rot, S = Vector3.One;
        public Matrix4x4 World = Matrix4x4.Identity;
        public string AttrMask = "", Attr = "";
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public bool HasGeom;
    }

    /// <summary>8 corners of an AABB, indexed like PcbParser.BoxVerts (bit0=x, bit1=y, bit2=z).</summary>
    static Vector3[] BoxCorners(Vector3 mn, Vector3 mx)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
            v[i] = new((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z);
        return v;
    }

    /// <summary>Triangle class for TriGroups (world-space verts). Precedence:
    /// invis ((mat&amp;0x1F)==0x11) &gt; unland ((mat&amp;0x200000)!=0) &gt; wall/floor by
    /// face normal (|n.y| &lt; 0.5 after normalization =&gt; wall; degenerate =&gt; floor).</summary>
    static string TriClass(ulong mat, Vector3 a, Vector3 b, Vector3 c)
    {
        if ((mat & 0x1F) == 0x11) return "invis";
        if ((mat & 0x200000) != 0) return "unland";
        var n = Vector3.Cross(b - a, c - a);
        return MathF.Abs(n.Y) < 0.5f * n.Length() ? "wall" : "floor";
    }

    static readonly string[] TriClassOrder = { "invis", "unland", "wall", "floor" };

    public static string Run(GameData gd, string territory, string outRoot, TerritoryDumpOptions? options = null, Action<string>? log = null)
    {
        var opts = options ?? new TerritoryDumpOptions();
        void Log(string s) => log?.Invoke(s);

        // --- resolve territory id or level dir ---
        string levelDir, label = territory.Trim();
        if (uint.TryParse(label, out var terrId))
        {
            var tt = gd.Excel.GetSheet<RawRow>(null, "TerritoryType");
            if (!tt.HasRow(terrId)) throw new Exception($"TerritoryType {terrId} not found");
            var row = tt.GetRow(terrId);
            string? bg = null;
            for (var c = 0; c < row.Columns.Count && bg == null; c++)
                if (row.Columns[c].Type == ExcelColumnDataType.String)
                {
                    var s = row.ReadColumn(c) is ReadOnlySeString rss ? rss.ExtractText() : "";
                    if (s.Contains("/level/")) bg = s;
                }
            if (bg == null) throw new Exception($"no Bg path on TerritoryType {terrId}");
            levelDir = "bg/" + bg[..bg.LastIndexOf('/')];
            label = terrId.ToString();
        }
        else levelDir = label.TrimEnd('/');

        // levelDir like bg/.../<zone>/level -> zone name is second-to-last
        var parts0 = levelDir.Split('/');
        var dirName = parts0[^1] == "level" && parts0.Length >= 2 ? parts0[^2] : parts0[^1];
        var outDir = Path.Combine(outRoot, $"lgb-{label}-{dirName}");
        Directory.CreateDirectory(outDir);
        Log($"level dir: {levelDir}");
        Log($"output:    {outDir}");

        var colliders = new List<Collider>();
        var pcbCache = new Dictionary<string, PcbMesh?>();
        PcbMesh? GetPcb(string path)
        {
            if (pcbCache.TryGetValue(path, out var m)) return m;
            var f = gd.GetFile(path);
            m = f == null ? null : PcbParser.ParsePcb(f.Data);
            pcbCache[path] = m;
            return m;
        }

        // ModelBoxBounds upgrade: .mdl -> local bounding box (ModelBoundingBoxes,
        // falling back to BoundingBoxes when degenerate), cached per path.
        var mdlCache = new Dictionary<string, (Vector3 mn, Vector3 mx)?>();
        (Vector3 mn, Vector3 mx)? GetMdlBounds(string path)
        {
            if (mdlCache.TryGetValue(path, out var b)) return b;
            (Vector3 mn, Vector3 mx)? r = null;
            try
            {
                var mdl = gd.GetFile<MdlFile>(path);
                if (mdl != null)
                {
                    static (Vector3, Vector3)? FromBb(Lumina.Data.Parsing.MdlStructs.BoundingBoxStruct bb)
                    {
                        var mn = new Vector3(bb.Min[0], bb.Min[1], bb.Min[2]);
                        var mx = new Vector3(bb.Max[0], bb.Max[1], bb.Max[2]);
                        return mx.X > mn.X || mx.Y > mn.Y || mx.Z > mn.Z ? (mn, mx) : null;
                    }
                    r = FromBb(mdl.ModelBoundingBoxes) ?? FromBb(mdl.BoundingBoxes);
                }
            }
            catch (Exception e) { Log($"  mdl bbox error {path}: {e.Message}"); }
            mdlCache[path] = r;
            return r;
        }

        void FinishCollider(Collider c)
        {
            if (c.Kind is "Mesh" or "MeshDerived" or "Terrain")
            {
                var m = c.PcbPath.Length > 0 ? GetPcb(c.PcbPath) : null;
                if (m != null)
                {
                    c.HasGeom = true;
                    foreach (var v in m.Verts)
                    {
                        var w = Vector3.Transform(v, c.World);
                        c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                    }
                }
            }
            else if (c.Kind is "Box" or "Board" or "BoardBothSides")
            {
                c.HasGeom = true;
                foreach (var v in PcbParser.BoxVerts)
                {
                    var w = Vector3.Transform(v, c.World);
                    c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                }
            }
            else if (c.Kind is "Sphere" or "Cylinder")
            {
                c.HasGeom = true;
                foreach (var v in PcbParser.CylVerts)
                {
                    var w = Vector3.Transform(v, c.World);
                    c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                }
            }
            else if (c.Kind == "ModelBox" && opts.ModelBoxBounds && c.PcbPath.EndsWith(".mdl"))
            {
                var b = GetMdlBounds(c.PcbPath);
                if (b != null)
                {
                    c.HasGeom = true;
                    foreach (var v in BoxCorners(b.Value.mn, b.Value.mx))
                    {
                        var w = Vector3.Transform(v, c.World);
                        c.Min = Vector3.Min(c.Min, w); c.Max = Vector3.Max(c.Max, w);
                    }
                }
            }
            colliders.Add(c);
        }

        // ---- SGB recursion (custom parser; offsets relative to instance start, see CutScan sgblayouts) ----
        var sgbVisited = new HashSet<string>();
        void ExpandSgb(string sgbPath, Matrix4x4 parentWorld, string chain, string srcLgb, uint layerId, string layerName, ulong rootInstId, int depth)
        {
            if (depth > 6) return;
            var f = gd.GetFile(sgbPath);
            if (f == null) return;
            var d = f.Data;
            uint U32(int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToUInt32(d, at) : 0;
            float F32(int at) => BitConverter.ToSingle(d, at);
            string CStr(int at) { if (at <= 0 || at >= d.Length) return ""; int e = at; while (e < d.Length && d[e] != 0) e++; return Encoding.UTF8.GetString(d, at, e - at); }
            try
            {
                int nSec = (int)U32(8); int off = 0xC, scn = -1;
                for (int i = 0; i < nSec && off + 8 <= d.Length; i++)
                {
                    if (d[off] == 'S' && d[off + 1] == 'C' && d[off + 2] == 'N' && d[off + 3] == '1') { scn = off + 8; break; }
                    off += (int)U32(off + 4);
                }
                if (scn < 0) return;
                int offEmb = (int)U32(scn), numEmb = (int)U32(scn + 4);
                for (int g = 0; g < numEmb; g++)
                {
                    int lg = scn + offEmb + g * 0x10;
                    int offLayers = (int)U32(lg + 8), numLayers = (int)U32(lg + 0xC);
                    for (int k = 0; k < numLayers; k++)
                    {
                        int lyr = lg + offLayers + (int)U32(lg + offLayers + 4 * k);
                        int offInst = (int)U32(lyr + 8), numInst = (int)U32(lyr + 0xC);
                        for (int j = 0; j < numInst; j++)
                        {
                            int io = lyr + offInst + (int)U32(lyr + offInst + 4 * j);
                            uint ty = U32(io);
                            var lt = new Vector3(F32(io + 0xC), F32(io + 0x10), F32(io + 0x14));
                            var lr = new Vector3(F32(io + 0x18), F32(io + 0x1C), F32(io + 0x20));
                            var ls = new Vector3(F32(io + 0x24), F32(io + 0x28), F32(io + 0x2C));
                            var world = PcbParser.LocalMatrix(lt, lr, ls) * parentWorld;
                            if (ty == 1) // BgPart: asset @+0x30, collision pcb @+0x34 (offsets from instance start)
                            {
                                var asset = CStr(io + (int)U32(io + 0x30));
                                var coll = CStr(io + (int)U32(io + 0x34));
                                if (coll.Length == 0 && asset.EndsWith(".mdl"))
                                {
                                    var cand = asset.Replace("/bgparts/", "/collision/")[..^4] + ".pcb";
                                    if (cand != asset && gd.FileExists(cand)) coll = cand;
                                }
                                if (coll.Length > 0 && coll.EndsWith(".pcb"))
                                    FinishCollider(new Collider { Source = "SgbBgPart", LgbFile = srcLgb, LayerId = layerId, LayerName = layerName, InstanceId = rootInstId, ParentChain = chain, Kind = "Mesh", PcbPath = coll, World = world, T = lt, Rot = lr, S = ls });
                            }
                            else if (ty == 6) // nested SharedGroup
                            {
                                var asset = CStr(io + (int)U32(io + 0x30));
                                if (asset.EndsWith(".sgb") && sgbVisited.Add(chain + "|" + asset + "|" + j))
                                    ExpandSgb(asset, world, chain + ">" + Path.GetFileNameWithoutExtension(asset), srcLgb, layerId, layerName, rootInstId, depth + 1);
                            }
                            else if (ty == 57) // CollisionBox: TriggerBox shape @+0x30, collision pcb offset @+0x48
                            {
                                uint shape = U32(io + 0x30);
                                var coll = CStr(io + (int)U32(io + 0x48));
                                var kind = PcbParser.ShapeName(shape);
                                if (coll.EndsWith(".pcb")) { kind = "Mesh"; }
                                FinishCollider(new Collider { Source = "SgbCollisionBox", LgbFile = srcLgb, LayerId = layerId, LayerName = layerName, InstanceId = rootInstId, ParentChain = chain, Kind = kind, PcbPath = coll.EndsWith(".pcb") ? coll : "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{U32(io + 0x3C):X}", Attr = $"0x{U32(io + 0x40):X}" });
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Log($"  sgb parse error {sgbPath}: {e.Message}"); }
        }

        // ---- per-LGB CSV dump + collider harvest ----
        var files = new[] { "bg", "planmap", "planevent", "planlive", "planner", "sound", "vfx" };
        bool any = false;
        foreach (var fname in files)
        {
            LgbFile? lgb = null;
            try { lgb = gd.GetFile<LgbFile>($"{levelDir}/{fname}.lgb"); }
            catch (Exception e) { Log($"{fname}.lgb: parse error: {e.Message}"); continue; }
            if (lgb == null) continue;
            any = true;
            using var w = Csv.OpenWriter(Path.Combine(outDir, fname + ".csv"));
            w.WriteLine("File,LayerId,LayerName,FestivalID,AssetType,InstanceId,Name,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,Extra");
            int count = 0;
            foreach (var layer in lgb.Layers)
            {
                foreach (var io in layer.InstanceObjects)
                {
                    var t = io.Transform;
                    var lt = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                    var lr = new Vector3(t.Rotation.X, t.Rotation.Y, t.Rotation.Z);
                    var ls = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
                    string extra = io.Object switch
                    {
                        LayerCommon.ENPCInstanceObject e2 => $"BaseId={e2.ParentData.ParentData.BaseId}",
                        LayerCommon.PopRangeInstanceObject p2 => $"PopType={p2.PopType};Index={p2.Index}",
                        LayerCommon.ExitRangeInstanceObject x2 => $"ExitType={x2.ExitType};TerritoryType={x2.TerritoryType};Index={x2.Index};Shape={PcbParser.ShapeName((uint)x2.ParentData.TriggerBoxShape)}",
                        LayerCommon.MapRangeInstanceObject m2 => $"Map={m2.Map};PlaceNameBlock={m2.PlaceNameBlock};PlaceNameSpot={m2.PlaceNameSpot};Shape={PcbParser.ShapeName((uint)m2.ParentData.TriggerBoxShape)}",
                        LayerCommon.EventInstanceObject ev => $"BaseId={ev.ParentData.BaseId}",
                        LayerCommon.AetheryteInstanceObject ae => $"BaseId={ae.ParentData.BaseId}",
                        LayerCommon.BGInstanceObject bg2 => $"Asset={bg2.AssetPath};CollisionAsset={bg2.CollisionAssetPath};CollisionType={bg2.CollisionType};AttrMask=0x{bg2.AttributeMask:X};Attr=0x{bg2.Attribute:X};Visible={bg2.IsVisible}",
                        LayerCommon.SharedGroupInstanceObject sg => $"Asset={sg.AssetPath};DoorState={sg.InitialDoorState};RotState={sg.InitialRotationState};TransformState={sg.InitialTransformState}",
                        LayerCommon.CollisionBoxInstanceObject cb => $"Shape={PcbParser.ShapeName((uint)cb.ParentData.TriggerBoxShape)};Enabled={cb.ParentData.Enabled};Priority={cb.ParentData.Priority};AttrMask=0x{cb.AttributeMask:X};Attr=0x{cb.Attribute:X};PushOut={cb.PushPlayerOut};CollisionAsset={cb.CollisionAssetPath}",
                        LayerCommon.EventRangeInstanceObject er => $"Shape={PcbParser.ShapeName((uint)er.ParentData.TriggerBoxShape)};Enabled={er.ParentData.Enabled}",
                        LayerCommon.SoundInstanceObject so => $"Asset={so.AssetPath}",
                        LayerCommon.VFXInstanceObject vf => $"Asset={vf.AssetPath}",
                        LayerCommon.EnvSetInstanceObject es => $"Asset={es.AssetPath}",
                        LayerCommon.LightInstanceObject li => $"LightType={li.LightType};Range={R(li.RangeRate)}",
                        _ => ""
                    };
                    w.WriteLine(string.Join(",",
                        fname, layer.LayerId, C(layer.Name ?? ""), layer.FestivalID,
                        io.AssetType, io.InstanceId, C(io.Name ?? ""),
                        R(lt.X), R(lt.Y), R(lt.Z), R(lr.X), R(lr.Y), R(lr.Z), R(ls.X), R(ls.Y), R(ls.Z),
                        C(extra)));
                    count++;

                    // -------- collider harvest --------
                    var world = PcbParser.LocalMatrix(lt, lr, ls);
                    switch (io.Object)
                    {
                        case LayerCommon.BGInstanceObject bgo:
                        {
                            var coll = bgo.CollisionAssetPath ?? "";
                            var kind = "Mesh";
                            if (coll.Length == 0 && (bgo.AssetPath?.EndsWith(".mdl") ?? false))
                            {
                                var cand = bgo.AssetPath.Replace("/bgparts/", "/collision/")[..^4] + ".pcb";
                                if (cand != bgo.AssetPath && gd.FileExists(cand)) { coll = cand; kind = "MeshDerived"; }
                            }
                            if (coll.Length > 0)
                                FinishCollider(new Collider { Source = "BgPart", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = kind, PcbPath = coll, World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{bgo.AttributeMask:X}", Attr = $"0x{bgo.Attribute:X}" });
                            else if (bgo.CollisionType == ModelCollisionType.Box)
                                FinishCollider(new Collider { Source = "BgPart", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = "ModelBox", PcbPath = bgo.AssetPath ?? "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{bgo.AttributeMask:X}", Attr = $"0x{bgo.Attribute:X}" });
                            break;
                        }
                        case LayerCommon.CollisionBoxInstanceObject cbo:
                        {
                            var coll = cbo.CollisionAssetPath ?? "";
                            var kind = coll.EndsWith(".pcb") ? "Mesh" : PcbParser.ShapeName((uint)cbo.ParentData.TriggerBoxShape);
                            FinishCollider(new Collider { Source = "CollisionBox", LgbFile = fname, LayerId = layer.LayerId, LayerName = layer.Name ?? "", InstanceId = io.InstanceId, Kind = kind, PcbPath = coll.EndsWith(".pcb") ? coll : "", World = world, T = lt, Rot = lr, S = ls, AttrMask = $"0x{cbo.AttributeMask:X}", Attr = $"0x{cbo.Attribute:X}" });
                            break;
                        }
                        case LayerCommon.SharedGroupInstanceObject sgo:
                            if (sgo.AssetPath?.EndsWith(".sgb") ?? false)
                            {
                                sgbVisited.Clear();
                                ExpandSgb(sgo.AssetPath, world, Path.GetFileNameWithoutExtension(sgo.AssetPath), fname, layer.LayerId, layer.Name ?? "", io.InstanceId, 0);
                            }
                            break;
                    }
                }
            }
            Log($"{fname}.lgb: {lgb.Layers.Length} layers, {count} instances -> {fname}.csv");
        }
        if (!any) throw new Exception($"no lgb files under {levelDir}");

        // ---- terrain collision ----
        var levelBase = levelDir.EndsWith("/level") ? levelDir[..^6] : levelDir;
        var listPath = $"{levelBase}/collision/list.pcb";
        var listFile = gd.GetFile(listPath);
        if (listFile != null)
        {
            var d = listFile.Data;
            int n = BitConverter.ToInt32(d, 0);
            for (int i = 0; i < n && 0x20 + 0x20 * i + 0x20 <= d.Length; i++)
            {
                int e = 0x20 + 0x20 * i;
                int meshId = BitConverter.ToInt32(d, e);
                FinishCollider(new Collider { Source = "Terrain", LgbFile = "terrain", Kind = "Terrain", PcbPath = $"{levelBase}/collision/tr{meshId:d4}.pcb", World = Matrix4x4.Identity });
            }
            Log($"terrain: {n} collision tiles ({listPath})");
        }
        else Log($"terrain: no {listPath} (indoor/instanced level or bgplate-less)");

        // ---- collision.csv ----
        using (var w = Csv.OpenWriter(Path.Combine(outDir, "collision.csv")))
        {
            w.WriteLine("Source,LgbFile,LayerId,LayerName,InstanceId,ParentChain,Kind,PcbPath,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,AttrMask,Attr,WorldMinX,WorldMinY,WorldMinZ,WorldMaxX,WorldMaxY,WorldMaxZ");
            foreach (var c in colliders)
            {
                var bb = c.HasGeom
                    ? string.Join(",", R(c.Min.X), R(c.Min.Y), R(c.Min.Z), R(c.Max.X), R(c.Max.Y), R(c.Max.Z))
                    : ",,,,,";
                w.WriteLine(string.Join(",",
                    c.Source, c.LgbFile, c.LayerId, C(c.LayerName), c.InstanceId, C(c.ParentChain), c.Kind, C(c.PcbPath),
                    R(c.T.X), R(c.T.Y), R(c.T.Z), R(c.Rot.X), R(c.Rot.Y), R(c.Rot.Z), R(c.S.X), R(c.S.Y), R(c.S.Z),
                    c.AttrMask, c.Attr, bb));
            }
        }
        Log($"collision.csv: {colliders.Count} colliders " +
            $"(terrain {colliders.Count(c => c.Source == "Terrain")}, bgpart {colliders.Count(c => c.Source == "BgPart")}, " +
            $"box {colliders.Count(c => c.Source == "CollisionBox")}, sgb {colliders.Count(c => c.Source.StartsWith("Sgb"))})");

        // ---- pcb-meshes.csv ----
        using (var w = Csv.OpenWriter(Path.Combine(outDir, "pcb-meshes.csv")))
        {
            w.WriteLine("PcbPath,Version,Nodes,Verts,Tris,LocalMinX,LocalMinY,LocalMinZ,LocalMaxX,LocalMaxY,LocalMaxZ");
            foreach (var (path, m) in pcbCache.OrderBy(kv => kv.Key))
            {
                if (m == null) { w.WriteLine($"{C(path)},MISSING,,,,,,,,,"); continue; }
                w.WriteLine(string.Join(",", C(path), m.Version, m.NodeCount, m.Verts.Count, m.Tris.Count,
                    R(m.Min.X), R(m.Min.Y), R(m.Min.Z), R(m.Max.X), R(m.Max.Y), R(m.Max.Z)));
            }
        }
        Log($"pcb-meshes.csv: {pcbCache.Count} pcb files ({pcbCache.Count(kv => kv.Value == null)} missing/unparsed)");

        // ---- OBJ export ----
        if (opts.ExportObj)
        {
            long triTotal = 0;
            var classTotals = new Dictionary<string, long> { ["invis"] = 0, ["unland"] = 0, ["wall"] = 0, ["floor"] = 0 };
            using var w = Csv.OpenWriter(Path.Combine(outDir, "collision-mesh.obj"));
            w.WriteLine($"# FFXIV collision mesh, world space - {label} ({levelDir})");
            if (opts.TriGroups)
                w.WriteLine("# groups: <Source>_<InstanceId>_<n>_<class>, class in invis/unland/wall/floor; analytic shapes are unit primitives scaled by transform");
            else
                w.WriteLine("# groups: <Source>_<InstanceId>_<n>; analytic shapes are unit primitives scaled by transform");
            int vbase = 1, gi = 0;
            foreach (var c in colliders)
            {
                gi++;
                IReadOnlyList<Vector3> verts;
                IEnumerable<(int, int, int)> tris;
                List<(int a, int b, int c, ulong mat)>? matTris = null;
                if (c.Kind is "Mesh" or "MeshDerived" or "Terrain")
                {
                    var m = c.PcbPath.Length > 0 ? GetPcb(c.PcbPath) : null;
                    if (m == null || m.Tris.Count == 0) continue;
                    verts = m.Verts; tris = m.Tris.Select(t2 => (t2.a, t2.b, t2.c));
                    matTris = m.Tris;
                }
                else if (c.Kind == "ModelBox" && opts.ModelBoxBounds && c.PcbPath.EndsWith(".mdl") && GetMdlBounds(c.PcbPath) is { } mb)
                { verts = BoxCorners(mb.mn, mb.mx); tris = PcbParser.BoxTris; }
                else if (c.Kind is "Box" or "Board" or "BoardBothSides" or "ModelBox") { verts = PcbParser.BoxVerts; tris = PcbParser.BoxTris; }
                else if (c.Kind is "Sphere" or "Cylinder") { verts = PcbParser.CylVerts; tris = PcbParser.CylTris; }
                else continue;

                if (!opts.TriGroups)
                {
                    w.WriteLine($"g {c.Source}_{c.InstanceId}_{gi}");
                    if (c.PcbPath.Length > 0) w.WriteLine($"# {c.PcbPath}");
                    foreach (var v in verts)
                    {
                        var p = Vector3.Transform(v, c.World);
                        w.WriteLine($"v {R(p.X)} {R(p.Y)} {R(p.Z)}");
                    }
                    foreach (var (a, b, c3) in tris)
                    { w.WriteLine($"f {vbase + a} {vbase + b} {vbase + c3}"); triTotal++; }
                    vbase += verts.Count;
                }
                else
                {
                    // TriGroups: verts once per collider, faces bucketed into per-class groups.
                    // Non-pcb geometry has no per-tri material -> mat 0 -> wall/floor by normal.
                    if (c.PcbPath.Length > 0) w.WriteLine($"# {c.PcbPath}");
                    var wv = new Vector3[verts.Count];
                    for (int i = 0; i < verts.Count; i++)
                    {
                        wv[i] = Vector3.Transform(verts[i], c.World);
                        w.WriteLine($"v {R(wv[i].X)} {R(wv[i].Y)} {R(wv[i].Z)}");
                    }
                    var buckets = new Dictionary<string, List<(int, int, int)>>();
                    int ti = 0;
                    foreach (var (a, b, c3) in tris)
                    {
                        ulong mat = matTris != null ? matTris[ti].mat : 0;
                        var cls = TriClass(mat, wv[a], wv[b], wv[c3]);
                        (buckets.TryGetValue(cls, out var l) ? l : buckets[cls] = new()).Add((a, b, c3));
                        ti++;
                    }
                    foreach (var cls in TriClassOrder)
                    {
                        if (!buckets.TryGetValue(cls, out var l)) continue;
                        w.WriteLine($"g {c.Source}_{c.InstanceId}_{gi}_{cls}");
                        foreach (var (a, b, c3) in l)
                        { w.WriteLine($"f {vbase + a} {vbase + b} {vbase + c3}"); triTotal++; }
                        classTotals[cls] += l.Count;
                    }
                    vbase += verts.Count;
                }
            }
            Log($"collision-mesh.obj: {triTotal} triangles, {vbase - 1} vertices");
            if (opts.TriGroups)
                Log($"tri classes: invis {classTotals["invis"]}, unland {classTotals["unland"]}, wall {classTotals["wall"]}, floor {classTotals["floor"]}");
        }
        if (opts.ModelBoxBounds)
        {
            var mb = colliders.Where(c => c.Kind == "ModelBox").ToList();
            Log($"modelbox: {mb.Count(c => c.HasGeom)}/{mb.Count} ModelBox colliders resolved via .mdl bounding box ({mdlCache.Count(kv => kv.Value == null)} mdl files unresolved)");
        }
        return outDir;
    }
}
