// Mdl module — .mdl mesh reader wrapping Lumina (MdlFile + Lumina.Models.Models).
// New for Atlas (no xivtool ancestor); acceptance = reference-implementation vs Lumina.Models.
//
// Lumina quirk (discovered 2026-08-24): Lumina.Models.Models.Model.ReadMeshes counts meshes by
// walking contiguous covered indices STARTING AT ABSOLUTE INDEX 0, but LodStruct mesh ranges for
// LOD>0 start past 0, so Model(mdl, Med/Low).Meshes comes back empty (or truncated when ranges
// have gaps). We therefore enumerate the LOD's mesh ranges ourselves from MdlFile.Lods[lod]
// (+ ExtraLods) and construct Lumina Mesh objects directly — Mesh reads vertex/index buffers via
// Parent.Lod, so buffers stay correct for every LOD.

using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Models.Models;

namespace Atlas.Core.Mdl;

public sealed record MdlMeshInfo(
    int MeshIndex,
    string Types,
    int VertexCount,
    int IndexCount,
    int SubmeshCount,
    int MaterialIndex,
    string MaterialPath,
    bool HasNormal,
    bool HasUv,
    bool HasColor,
    bool HasTangent,
    bool HasBlend);

public sealed record MdlLodInfo(int Lod, IReadOnlyList<MdlMeshInfo> Meshes)
{
    public int VertexTotal => Meshes.Sum(m => m.VertexCount);
    public int IndexTotal => Meshes.Sum(m => m.IndexCount);
}

public sealed record ModelInfo(
    string Path,
    int LodCount,
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> AttributeNames,
    int BoneCount,
    int ShapeCount,
    Vector3? BboxMin,
    Vector3? BboxMax,
    IReadOnlyList<MdlLodInfo> Lods);

public static class MdlOps
{
    /// <summary>
    /// Load a .mdl via Lumina, shimming v6 chara models to v5 first (MdlV6.ToV5).
    /// v5 loads take the direct sqpack path; shimmed bytes go through a temp file
    /// (Lumina's byte-level loader) with the game path preserved as origin.
    /// </summary>
    public static MdlFile LoadMdl(XivEnv env, string path)
    {
        var raw = env.Game.GetFile(path) ?? throw new FileNotFoundException($"not found: {path}");
        var data = MdlV6.ToV5(raw.Data);
        if (ReferenceEquals(data, raw.Data))
            return env.Game.GetFile<MdlFile>(path)!;
        var tmp = Path.Combine(Path.GetTempPath(), $"atlas-v6-{Guid.NewGuid():N}.mdl");
        try
        {
            File.WriteAllBytes(tmp, data);
            return env.Game.GetFileFromDisk<MdlFile>(tmp, path);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>Load a .mdl and summarize every LOD (mesh/vertex/index counts, materials, bbox).</summary>
    public static ModelInfo Info(XivEnv env, string path)
    {
        var mdl = LoadMdl(env, path);

        var lodCount = (int)mdl.FileHeader.LodCount;
        var lods = new List<MdlLodInfo>(lodCount);
        IReadOnlyList<string> materials = [];
        IReadOnlyList<string> attributes = [];

        for (var lod = 0; lod < lodCount; lod++)
        {
            var model = new Model(mdl, (Model.ModelLod)lod);
            if (lod == 0)
            {
                materials = model.Materials.Select(m => m.MaterialPath).ToArray();
                attributes = mdl.AttributeNameOffsets
                    .Select(o => model.StringOffsetToStringMap.TryGetValue((int)o, out var s) ? s : $"@{o}")
                    .ToArray();
            }

            var meshes = new List<MdlMeshInfo>();
            foreach (var (meshIndex, types) in LodMeshRanges(mdl, lod))
            {
                var mesh = new Mesh(model, meshIndex, types);
                var usages = mdl.VertexDeclarations[meshIndex].VertexElements
                    .Select(e => (Vertex.VertexUsage)e.Usage).ToHashSet();
                meshes.Add(new MdlMeshInfo(
                    meshIndex,
                    string.Join('+', types.Select(t => t.ToString())),
                    mesh.Vertices.Length,
                    mesh.Indices.Length,
                    mesh.Submeshes.Length,
                    mdl.Meshes[meshIndex].MaterialIndex,
                    mesh.Material?.MaterialPath ?? "",
                    usages.Contains(Vertex.VertexUsage.Normal),
                    usages.Contains(Vertex.VertexUsage.UV),
                    usages.Contains(Vertex.VertexUsage.Color),
                    usages.Contains(Vertex.VertexUsage.Tangent1) || usages.Contains(Vertex.VertexUsage.Tangent2),
                    usages.Contains(Vertex.VertexUsage.BlendWeights)));
            }
            lods.Add(new MdlLodInfo(lod, meshes));
        }

        var (mn, mx) = Bounds(mdl);
        return new ModelInfo(path, lodCount, materials, attributes,
            mdl.ModelHeader.BoneCount, mdl.ModelHeader.ShapeCount, mn, mx, lods);
    }

    /// <summary>
    /// Export one LOD as Wavefront OBJ: shared 1-based v/vt/vn indexing, one `o` group per mesh
    /// named &lt;meshIndex&gt;_&lt;materialFileName&gt;, `usemtl` = the material game path.
    /// UV V is flipped (1-v) for the OBJ bottom-left convention.
    /// </summary>
    public static void ExportObj(XivEnv env, string path, TextWriter w, int lod = 0, Action<string>? log = null)
    {
        var mdl = LoadMdl(env, path);
        if (lod < 0 || lod >= mdl.FileHeader.LodCount)
            throw new ArgumentOutOfRangeException(nameof(lod), lod, $"model has {mdl.FileHeader.LodCount} LODs");

        var model = new Model(mdl, (Model.ModelLod)lod);
        static string F(float v) => v.ToString("R");

        w.WriteLine($"# atlas mod Mdl obj — {path} (lod {lod})");
        w.WriteLine("# shared 1-based indexing; vt V flipped (1-v) to OBJ convention");

        var vBase = 1; var tBase = 1; var nBase = 1;
        foreach (var (meshIndex, types) in LodMeshRanges(mdl, lod))
        {
            var mesh = new Mesh(model, meshIndex, types);
            var matPath = mesh.Material?.MaterialPath ?? "unknown";
            var matName = Path.GetFileNameWithoutExtension(matPath);
            var usages = mdl.VertexDeclarations[meshIndex].VertexElements
                .Select(e => (Vertex.VertexUsage)e.Usage).ToHashSet();
            var hasUv = usages.Contains(Vertex.VertexUsage.UV);
            var hasN = usages.Contains(Vertex.VertexUsage.Normal);

            w.WriteLine($"o {meshIndex}_{matName}");
            w.WriteLine($"usemtl {matPath}");

            foreach (var v in mesh.Vertices)
            {
                var p = v.Position ?? Vector4.Zero;
                w.WriteLine($"v {F(p.X)} {F(p.Y)} {F(p.Z)}");
            }
            if (hasUv)
                foreach (var v in mesh.Vertices)
                {
                    var uv = v.UV ?? Vector4.Zero;
                    w.WriteLine($"vt {F(uv.X)} {F(1f - uv.Y)}");
                }
            if (hasN)
                foreach (var v in mesh.Vertices)
                {
                    var n = v.Normal ?? Vector3.UnitY;
                    w.WriteLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");
                }

            for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                w.WriteLine((hasUv, hasN) switch
                {
                    (true, true) => $"f {a + vBase}/{a + tBase}/{a + nBase} {b + vBase}/{b + tBase}/{b + nBase} {c + vBase}/{c + tBase}/{c + nBase}",
                    (true, false) => $"f {a + vBase}/{a + tBase} {b + vBase}/{b + tBase} {c + vBase}/{c + tBase}",
                    (false, true) => $"f {a + vBase}//{a + nBase} {b + vBase}//{b + nBase} {c + vBase}//{c + nBase}",
                    _ => $"f {a + vBase} {b + vBase} {c + vBase}",
                });
            }

            log?.Invoke($"  mesh {meshIndex} ({string.Join('+', types)}): {mesh.Vertices.Length} verts, {mesh.Indices.Length / 3} tris, mtrl {matPath}");
            vBase += mesh.Vertices.Length;
            if (hasUv) tBase += mesh.Vertices.Length;
            if (hasN) nBase += mesh.Vertices.Length;
        }
    }

    /// <summary>Absolute mesh indices for one LOD, with their mesh-type tags, in index order.</summary>
    internal static IEnumerable<(int Index, Mesh.MeshType[] Types)> LodMeshRanges(MdlFile mdl, int lod)
    {
        var l = mdl.Lods[lod];
        var ranges = new List<(int Start, int Count, Mesh.MeshType Type)>
        {
            (l.MeshIndex, l.MeshCount, Mesh.MeshType.Main),
            (l.WaterMeshIndex, l.WaterMeshCount, Mesh.MeshType.Water),
            (l.ShadowMeshIndex, l.ShadowMeshCount, Mesh.MeshType.Shadow),
            (l.TerrainShadowMeshIndex, l.TerrainShadowMeshCount, Mesh.MeshType.TerrainShadow),
            (l.VerticalFogMeshIndex, l.VerticalFogMeshCount, Mesh.MeshType.VerticalFog),
        };
        if (mdl.ModelHeader.ExtraLodEnabled)
        {
            var e = mdl.ExtraLods[lod];
            ranges.Add((e.LightShaftMeshIndex, e.LightShaftMeshCount, Mesh.MeshType.LightShaft));
            ranges.Add((e.GlassMeshIndex, e.GlassMeshCount, Mesh.MeshType.Glass));
            ranges.Add((e.MaterialChangeMeshIndex, e.MaterialChangeMeshCount, Mesh.MeshType.MaterialChange));
            ranges.Add((e.CrestChangeMeshIndex, e.CrestChangeMeshCount, Mesh.MeshType.CrestChange));
        }

        var byIndex = new SortedDictionary<int, List<Mesh.MeshType>>();
        foreach (var (start, count, type) in ranges)
            for (var i = start; i < start + count; i++)
            {
                if (i < 0 || i >= mdl.Meshes.Length) continue;
                if (!byIndex.TryGetValue(i, out var list)) byIndex[i] = list = [];
                list.Add(type);
            }
        foreach (var (idx, list) in byIndex)
            yield return (idx, list.ToArray());
    }

    /// <summary>Model-local AABB: ModelBoundingBoxes, falling back to BoundingBoxes when degenerate
    /// (same rule as the Pcb --modelbox upgrade, so downstream ModelBox colliders agree).</summary>
    public static (Vector3? Min, Vector3? Max) Bounds(MdlFile mdl)
    {
        static (Vector3, Vector3)? FromBb(MdlStructs.BoundingBoxStruct bb)
        {
            var mn = new Vector3(bb.Min[0], bb.Min[1], bb.Min[2]);
            var mx = new Vector3(bb.Max[0], bb.Max[1], bb.Max[2]);
            return mx.X > mn.X || mx.Y > mn.Y || mx.Z > mn.Z ? (mn, mx) : null;
        }
        var r = FromBb(mdl.ModelBoundingBoxes) ?? FromBb(mdl.BoundingBoxes);
        return r == null ? (null, null) : (r.Value.Item1, r.Value.Item2);
    }
}
