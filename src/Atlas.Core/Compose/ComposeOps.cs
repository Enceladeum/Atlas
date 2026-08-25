// ComposeOps - composed-map assembly: one territory's bg layout -> one glTF scene.
// New module (no xivtool ancestor): reference-implementation acceptance
// (CONTRACT.md) - Blender/three.js import + bbox overlap with the Pcb collision
// OBJ for the same territory.
//
// v1 scope: bg.lgb + terrain bgplates (planmap etc. later, flag-gated). BGPart instances whose
// asset ends .mdl become glTF nodes referencing per-asset-path deduped meshes
// (LOD selectable, default 0, Main meshes only). SharedGroup (.sgb) instances are
// resolved ONE level deep: the group's BgPart children get child nodes with their
// LOCAL transforms, composed with the instance transform by glTF node nesting
// (world = local * parentWorld row-vector == parent*child column-vector).
// sgb-in-sgb (type 6) is counted and skipped, never silently dropped.
// Materials v1: flat pastel baseColorFactor (GltfWriter.Pastel - FNV-1a hash of
// the material path, deterministic across runs).
// Textured mode (opts.Textured): each .mtrl's diffuse (Mtrl.DiffuseResolve -
// g_SamplerColorMap0 / g_SamplerDiffuse / *_d.tex priority) is exported once to {outDir}/tex/*.png
// (mip capped at MaxTexDim) and wired as baseColorTexture (alphaMode MASK for
// foliage cutouts); output becomes map-{label}-tex.gltf/.bin. The untextured
// default filenames and bytes stay golden-identical (ADDITIVE, CONTRACT).
//
// Terrain (opts.Terrain, default on): {zoneBase}/bgplate/terrain.tera - 52-byte
// header (version 0x01000003 u32, plateCount u32, plateSize u32 yalms, rest
// reserved) then plateCount i16 (x,y) cell pairs @+52. Plate i pairs with
// {i:04}.mdl in the same folder; world = (plateSize*(x+.5), 0, plateSize*(y+.5)),
// translation only (height is baked into the plate mesh). Plates flow through
// the same mesh/material caches as bg parts (textured mode included); nodes go
// under a "terrain" group with extras lgbFile:"terrain", instanceId=plate index.
//
// Transform convention == the Pcb path (keep in sync with TerritoryDump):
// local = S*Rx*Ry*Rz*T (PcbParser.LocalMatrix, row-vector, euler radians X then
// Y then Z); coordinates pass through unmodified, so map-<tt>.gltf overlays
// lgb-<tt>-*/collision-mesh.obj exactly.
//
// Identity rule: every instance node carries extras
// {territoryId, lgbFile:"bg", layerId, instanceId, assetPath} (+ sgbPath on
// resolved shared-group parts) - a GUI pick round-trips to a CSV row/InstanceKey.

using System.Numerics;
using System.Text;
using Atlas.Core.Gltf;
using Atlas.Core.Mtrl;
using Atlas.Core.Tex;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Text.ReadOnly;
using LModel = Lumina.Models.Models.Model;
using LMesh = Lumina.Models.Models.Mesh;

namespace Atlas.Core.Compose;

public sealed class ComposeOptions
{
    /// <summary>Mesh level of detail: 0 = high (default). Clamped per model.</summary>
    public int Lod;
    /// <summary>Restrict to these LayerIds (null = all layers).</summary>
    public HashSet<uint>? Layers;
    /// <summary>Export diffuse textures under {outDir}/tex/ and wire them as
    /// baseColorTexture; output becomes map-{label}-tex.gltf/.bin.</summary>
    public bool Textured;
    /// <summary>Textured: use the smallest mip that fits within this dimension
    /// (keeps whole-map exports web-viewable). 0 = always mip 0. Default 1024.</summary>
    public int MaxTexDim = 1024;
    /// <summary>Include terrain bgplates ({zoneBase}/bgplate/terrain.tera). Default true.</summary>
    public bool Terrain = true;
}

public sealed class ComposeSummary
{
    public uint TerritoryId;             // 0 when composed from a raw level dir
    public string Label = "", LevelDir = "", GltfPath = "", BinPath = "";
    public int Layers, Instances, SgbGroups, SgbParts, SgbNestedSkipped, OtherSkipped, FailedMdl, UniqueMeshes;
    public int TexMaterials, TexFiles, TexFailed;   // textured mode only
    public int TerrainPlates, TerrainMissing;       // terrain bgplates (missing = plate mdl absent/failed)
}

public static class ComposeOps
{
    /// <summary>Territory id or bg level dir -> level dir + label. Same resolution
    /// as TerritoryDump.Run: TerritoryType string column containing "/level/".</summary>
    public static string ResolveLevelDir(GameData gd, string territory, out string label)
    {
        label = territory.Trim();
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
            label = terrId.ToString();
            return "bg/" + bg[..bg.LastIndexOf('/')];
        }
        return label.TrimEnd('/');
    }

    /// <summary>Compose <c>{outDir}/map-{label}.gltf</c> + <c>.bin</c> from the
    /// territory's bg.lgb. Returns counts + output paths; progress via log.</summary>
    public static ComposeSummary MapGltf(GameData gd, string territory, string outDir,
        ComposeOptions? options = null, Action<string>? log = null)
    {
        var opts = options ?? new ComposeOptions();
        void Log(string s) => log?.Invoke(s);
        var levelDir = ResolveLevelDir(gd, territory, out var label);
        uint.TryParse(label, out var terrId);
        var lgb = gd.GetFile<LgbFile>($"{levelDir}/bg.lgb") ?? throw new Exception($"no bg.lgb under {levelDir}");
        Log($"level dir: {levelDir}");

        var w = new GltfWriter();
        var sum = new ComposeSummary { TerritoryId = terrId, Label = label, LevelDir = levelDir };

        // ---- material cache: path -> material index ----
        // Untextured (default): stable pastel baseColorFactor. Textured: the
        // resolved diffuse PNG under {outDir}/tex/ as baseColorTexture (white
        // base color, alphaMode MASK for cutouts); resolve/decode failures fall
        // back to the pastel (counted in TexFailed; BC4/BC6H are undecodable).
        var matByPath = new Dictionary<string, int>();
        var texDir = Path.Combine(outDir, "tex");
        int GetMaterial(string path)
        {
            if (matByPath.TryGetValue(path, out var idx)) return idx;
            var uri = opts.Textured && path.EndsWith(".mtrl") ? TryExportDiffuse(path) : null;
            matByPath[path] = idx = uri != null
                ? w.AddMaterial(Path.GetFileNameWithoutExtension(path), Vector4.One, uri, alphaMask: true)
                : w.AddMaterial(Path.GetFileNameWithoutExtension(path), GltfWriter.Pastel(path));
            return idx;
        }

        // Textured: mtrl -> diffuse tex -> {outDir}/tex/<path with '/'->'_'>.png,
        // written once per texture (several materials may share one PNG). Returns
        // the glTF-relative uri, or null to fall back to pastel. The diffuse rule
        // itself (sampler priority, mip cap) lives in Mtrl.DiffuseResolve, shared
        // with the single-model export (Mdl.MdlGltf).
        string? TryExportDiffuse(string mtrlPath)
        {
            try
            {
                if (DiffuseResolve.TryResolve(gd, mtrlPath, opts.MaxTexDim) is not { } d) { sum.TexFailed++; return null; }
                var fileName = d.TexPath.Replace('/', '_') + ".png";
                var png = Path.Combine(texDir, fileName);
                if (!File.Exists(png))
                {
                    Directory.CreateDirectory(texDir);
                    using var fs = File.Create(png);
                    TexOps.WritePng(d.Tex, fs, d.Mip);
                    sum.TexFiles++;
                }
                sum.TexMaterials++;
                return "tex/" + fileName;
            }
            catch (Exception e)
            {
                Log($"  tex error {mtrlPath}: {e.Message}");
                sum.TexFailed++;
                return null;
            }
        }

        // ---- mesh cache: asset path -> glTF mesh index (null = load failed) ----
        var meshByAsset = new Dictionary<string, int?>();
        int? GetMesh(string asset)
        {
            if (meshByAsset.TryGetValue(asset, out var cached)) return cached;
            int? result = null;
            try
            {
                var mdl = gd.GetFile<MdlFile>(asset);
                if (mdl != null)
                {
                    var lod = Math.Clamp(opts.Lod, 0, Math.Max(1, (int)mdl.FileHeader.LodCount) - 1);
                    var model = new LModel(mdl, (LModel.ModelLod)lod);
                    var meshIdx = -1;
                    foreach (var m in model.Meshes)
                    {
                        if (m.Types == null || Array.IndexOf(m.Types, LMesh.MeshType.Main) < 0) continue;
                        if (m.Vertices is not { Length: > 0 } || m.Indices is not { Length: >= 3 }) continue;
                        var pos = new float[m.Vertices.Length * 3];
                        var nrm = new float[m.Vertices.Length * 3];
                        var uv = new float[m.Vertices.Length * 2];
                        for (var i = 0; i < m.Vertices.Length; i++)
                        {
                            var v = m.Vertices[i];
                            var p = v.Position ?? Vector4.Zero;
                            pos[i * 3] = p.X; pos[i * 3 + 1] = p.Y; pos[i * 3 + 2] = p.Z;
                            var n = v.Normal ?? Vector3.UnitY;
                            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                            nrm[i * 3] = n.X; nrm[i * 3 + 1] = n.Y; nrm[i * 3 + 2] = n.Z;
                            var u = v.UV ?? Vector4.Zero;
                            uv[i * 2] = u.X; uv[i * 2 + 1] = u.Y;
                        }
                        var idx = new uint[m.Indices.Length - m.Indices.Length % 3];
                        for (var i = 0; i < idx.Length; i++) idx[i] = m.Indices[i];
                        if (meshIdx < 0) meshIdx = w.AddMesh(Path.GetFileNameWithoutExtension(asset));
                        w.AddPrimitive(meshIdx, pos, nrm, uv, idx, GetMaterial(m.Material?.MaterialPath ?? asset));
                    }
                    if (meshIdx >= 0) result = meshIdx;
                }
            }
            catch (Exception e) { Log($"  mdl error {asset}: {e.Message}"); }
            meshByAsset[asset] = result;
            return result;
        }

        // ---- sgb cache: path -> (one-level BgPart list, nested sgb count) ----
        var sgbCache = new Dictionary<string, (List<SgbPart> parts, int nested)>();
        (List<SgbPart> parts, int nested) GetSgbParts(string path)
        {
            if (!sgbCache.TryGetValue(path, out var r))
                sgbCache[path] = r = ReadSgbParts(gd, path, Log);
            return r;
        }

        static Dictionary<string, object?> Extras(uint tt, uint layerId, uint instId, string asset, string? sgb = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["territoryId"] = tt, ["lgbFile"] = "bg", ["layerId"] = layerId,
                ["instanceId"] = instId, ["assetPath"] = asset,
            };
            if (sgb != null) d["sgbPath"] = sgb;
            return d;
        }
        static string NodeName(string asset, uint instId) => $"{Path.GetFileNameWithoutExtension(asset)}_{instId}";

        var root = w.AddNode($"map-{label}", extras: new Dictionary<string, object?>
        { ["territoryId"] = terrId, ["levelDir"] = levelDir });
        w.AddSceneRoot(root);

        foreach (var layer in lgb.Layers)
        {
            if (opts.Layers != null && !opts.Layers.Contains(layer.LayerId)) continue;
            var layerNode = -1;
            int EnsureLayer()
            {
                if (layerNode < 0)
                {
                    // Stage-inspector metadata: the GUI reads these off the group's
                    // userData to badge festival/layer-set/temporary layers.
                    var lex = new Dictionary<string, object?>
                    {
                        ["layer"] = layer.Name ?? "", ["layerId"] = layer.LayerId,
                        ["festivalId"] = (uint)layer.FestivalID,
                        ["festivalPhase"] = (uint)layer.FestivalPhaseID,
                    };
                    if (layer.IsTemporary != 0) lex["temporary"] = true;
                    if (layer.IsHousing != 0) lex["housing"] = true;
                    if (layer.LayerSetReferences is { Length: > 0 })
                        lex["layerSets"] = layer.LayerSetReferences.Select(r => r.LayerSetId).ToArray();
                    layerNode = w.AddNode($"layer-{layer.LayerId}-{layer.Name ?? ""}", extras: lex);
                    w.AddChild(root, layerNode);
                    sum.Layers++;
                }
                return layerNode;
            }
            foreach (var io in layer.InstanceObjects)
            {
                var t = io.Transform;
                var lt = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                var lr = new Vector3(t.Rotation.X, t.Rotation.Y, t.Rotation.Z);
                var ls = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
                switch (io.Object)
                {
                    case LayerCommon.BGInstanceObject bgo when (bgo.AssetPath ?? "").EndsWith(".mdl"):
                    {
                        var mesh = GetMesh(bgo.AssetPath!);
                        if (mesh == null) { sum.FailedMdl++; break; }
                        var n = w.AddNode(NodeName(bgo.AssetPath!, io.InstanceId), lt, GltfWriter.FromEulerXyz(lr), ls,
                            mesh, Extras(terrId, layer.LayerId, io.InstanceId, bgo.AssetPath!));
                        w.AddChild(EnsureLayer(), n);
                        sum.Instances++;
                        break;
                    }
                    case LayerCommon.SharedGroupInstanceObject sgo when (sgo.AssetPath ?? "").EndsWith(".sgb"):
                    {
                        sum.SgbGroups++;
                        var (parts, nested) = GetSgbParts(sgo.AssetPath!);
                        sum.SgbNestedSkipped += nested;
                        if (parts.Count == 0) break;
                        var g = w.AddNode(NodeName(sgo.AssetPath!, io.InstanceId), lt, GltfWriter.FromEulerXyz(lr), ls,
                            null, Extras(terrId, layer.LayerId, io.InstanceId, sgo.AssetPath!));
                        w.AddChild(EnsureLayer(), g);
                        foreach (var p in parts)
                        {
                            var mesh = GetMesh(p.Asset);
                            if (mesh == null) { sum.FailedMdl++; continue; }
                            var cn = w.AddNode(NodeName(p.Asset, io.InstanceId), p.T, GltfWriter.FromEulerXyz(p.R), p.S,
                                mesh, Extras(terrId, layer.LayerId, io.InstanceId, p.Asset, sgo.AssetPath));
                            w.AddChild(g, cn);
                            sum.SgbParts++;
                        }
                        break;
                    }
                    default:
                        sum.OtherSkipped++;
                        break;
                }
            }
        }

        // ---- terrain bgplates: {zoneBase}/bgplate/terrain.tera + {i:04}.mdl ----
        if (opts.Terrain && levelDir.EndsWith("/level"))
        {
            var bgplateDir = levelDir[..^"/level".Length] + "/bgplate";
            var tera = gd.GetFile($"{bgplateDir}/terrain.tera");
            if (tera == null) Log("terrain: no bgplate/terrain.tera (indoor or bgpart-only level)");
            else
            {
                var td = tera.Data;
                var plateCount = td.Length >= 52 ? BitConverter.ToInt32(td, 4) : 0;
                float plateSize = td.Length >= 52 ? BitConverter.ToUInt32(td, 8) : 0f;
                var terrainNode = -1;
                for (var i = 0; i < plateCount && 52 + i * 4 + 4 <= td.Length; i++)
                {
                    var px = BitConverter.ToInt16(td, 52 + i * 4);
                    var py = BitConverter.ToInt16(td, 52 + i * 4 + 2);
                    var asset = $"{bgplateDir}/{i:d4}.mdl";
                    var mesh = GetMesh(asset);
                    if (mesh == null) { sum.TerrainMissing++; continue; }
                    if (terrainNode < 0)
                 {
                     terrainNode = w.AddNode("terrain", extras: new Dictionary<string, object?>
                     { ["layer"] = "terrain", ["layerId"] = 0u, ["terrain"] = true });
                     w.AddChild(root, terrainNode);
                 }
                    var n = w.AddNode($"plate_{i:d4}",
                        new Vector3(plateSize * (px + 0.5f), 0f, plateSize * (py + 0.5f)), null, null,
                        mesh, new Dictionary<string, object?>
                        {
                            ["territoryId"] = terrId, ["lgbFile"] = "terrain", ["layerId"] = 0u,
                            ["instanceId"] = (uint)i, ["assetPath"] = asset,
                        });
                    w.AddChild(terrainNode, n);
                    sum.TerrainPlates++;
                }
                Log($"terrain: {sum.TerrainPlates} plates (cell {plateSize}) "
                    + (sum.TerrainMissing > 0 ? $"+ {sum.TerrainMissing} geometry-less " : "") + $"from {bgplateDir}");
            }
        }

        sum.UniqueMeshes = meshByAsset.Count(kv => kv.Value != null);
        Directory.CreateDirectory(outDir);
        var stem = opts.Textured ? $"map-{label}-tex" : $"map-{label}";
        sum.GltfPath = Path.Combine(outDir, $"{stem}.gltf");
        sum.BinPath = Path.Combine(outDir, $"{stem}.bin");
        w.Write(sum.GltfPath, sum.BinPath, stem);
        Log($"bg.lgb: {sum.Instances} bg instances + {sum.SgbParts} sgb parts placed, " +
            $"{sum.UniqueMeshes} unique meshes, {w.NodeCount} nodes, {sum.Layers} layers");
        if (opts.Textured)
            Log($"textures: {sum.TexMaterials} materials textured, {sum.TexFiles} pngs, {sum.TexFailed} pastel fallback");
        return sum;
    }

    // ---------- SGB one-level BgPart extraction ----------
    public readonly record struct SgbPart(string Asset, Vector3 T, Vector3 R, Vector3 S);

    /// <summary>Walk one .sgb's SCN1 embedded layer groups (same byte layout as
    /// TerritoryDump.ExpandSgb / SgbLayouts: instance TRS @+0xC local space, path
    /// offset @+0x30) and return its direct BgPart .mdl entries. Nested
    /// SharedGroups (type 6) are only counted - v1 resolves ONE level deep.</summary>
    public static (List<SgbPart> parts, int nested) ReadSgbParts(GameData gd, string sgbPath, Action<string>? log)
    {
        var parts = new List<SgbPart>();
        var nested = 0;
        var f = gd.GetFile(sgbPath);
        if (f == null) { log?.Invoke($"  sgb missing: {sgbPath}"); return (parts, nested); }
        var d = f.Data;
        uint U32(int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToUInt32(d, at) : 0;
        float F32(int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToSingle(d, at) : 0f;
        string CStr(int at)
        {
            if (at <= 0 || at >= d.Length) return "";
            var e = at;
            while (e < d.Length && d[e] != 0) e++;
            return Encoding.UTF8.GetString(d, at, e - at);
        }
        try
        {
            var nSec = (int)U32(8);
            int off = 0xC, scn = -1;
            for (var i = 0; i < nSec && off + 8 <= d.Length; i++)
            {
                if (d[off] == 'S' && d[off + 1] == 'C' && d[off + 2] == 'N' && d[off + 3] == '1') { scn = off + 8; break; }
                off += (int)U32(off + 4);
            }
            if (scn < 0) return (parts, nested);
            int offEmb = (int)U32(scn), numEmb = (int)U32(scn + 4);
            for (var g = 0; g < numEmb; g++)
            {
                var lg = scn + offEmb + g * 0x10;
                int offLayers = (int)U32(lg + 8), numLayers = (int)U32(lg + 0xC);
                for (var k = 0; k < numLayers; k++)
                {
                    var lyr = lg + offLayers + (int)U32(lg + offLayers + 4 * k);
                    int offInst = (int)U32(lyr + 8), numInst = (int)U32(lyr + 0xC);
                    for (var j = 0; j < numInst; j++)
                    {
                        var io = lyr + offInst + (int)U32(lyr + offInst + 4 * j);
                        var ty = U32(io);
                        if (ty == 1)         // BgPart: asset path offset @+0x30
                        {
                            var asset = CStr(io + (int)U32(io + 0x30));
                            if (asset.EndsWith(".mdl"))
                                parts.Add(new SgbPart(asset,
                                    new Vector3(F32(io + 0xC), F32(io + 0x10), F32(io + 0x14)),
                                    new Vector3(F32(io + 0x18), F32(io + 0x1C), F32(io + 0x20)),
                                    new Vector3(F32(io + 0x24), F32(io + 0x28), F32(io + 0x2C))));
                        }
                        else if (ty == 6) nested++;   // sgb-in-sgb: v1 counts + skips
                    }
                }
            }
        }
        catch (Exception e) { log?.Invoke($"  sgb parse error {sgbPath}: {e.Message}"); }
        return (parts, nested);
    }

}
