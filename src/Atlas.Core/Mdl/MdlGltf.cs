// MdlGltf - single-model glTF export (new for Atlas; no xivtool ancestor).
// The asset browser's Textured preview and `mod Mdl gltf` share one builder:
//
//   Export()        {outDir}/{stem}.gltf/.bin (+ tex/*.png when textured), stem =
//                   the mdl file name (+ "-tex") - same file conventions as the
//                   composed map, so exports drop into Blender identically.
//   BuildEmbedded() one self-contained .gltf JSON string (buffer and textures as
//                   data: URIs) - the server's single-response preview payload;
//                   nothing touches disk.
//
// Mesh scope: the LOD's visual ranges (Main/Water/LightShaft/Glass) via
// MdlOps.LodMeshRanges (LOD-correct; see the Lumina quirk note in MdlOps).
// Shadow/TerrainShadow/VerticalFog and the *Change variant ranges are skipped -
// non-visual or variant geometry that would occlude a preview (`mod Mdl obj`
// still exports everything). Geometry matches Compose.GetMesh: POSITION/NORMAL/
// TEXCOORD_0, UV unflipped (glTF top-left origin, proven by the textured map),
// indices trimmed to whole triangles.
//
// Materials: textured mode resolves each .mtrl diffuse via Mtrl.DiffuseResolve
// (the shared sampler-priority rule), wired as baseColorTexture with alphaMode
// MASK for cutouts; anything unresolvable falls back to GltfWriter.Pastel.
// Known pastel-fallback cases: chara materials (variant-relative mtrl paths -
// the .mdl carries no absolute game path) and BC4/BC6H/2D-array textures
// (Lumina cannot decode them).

using System.Numerics;
using Atlas.Core.Gltf;
using Atlas.Core.Mtrl;
using Atlas.Core.Tex;
using Lumina.Data.Files;
using Lumina.Models.Models;

namespace Atlas.Core.Mdl;

public sealed class MdlGltfOptions
{
    /// <summary>Level of detail; clamped to the model's LOD count.</summary>
    public int Lod;
    /// <summary>Resolve + export/embed diffuse textures (default: all-pastel).</summary>
    public bool Textured;
    /// <summary>Textured: use the smallest mip within this dimension; 0 = mip 0.
    /// Default 1024.</summary>
    public int MaxTexDim = 1024;
}

public sealed class MdlGltfSummary
{
    public string MdlPath = "", GltfPath = "";      // GltfPath empty for embedded builds
    public int Lod, SourceMeshes, Vertices, Triangles, Materials;
    public int TexMaterials, TexFiles, TexFailed;   // textured mode only
}

public static class MdlGltf
{
    /// <summary>Write {outDir}/{stem}.gltf + .bin (+ tex/*.png when textured);
    /// stem = model file name (+ "-tex"). Returns counts + the gltf path.</summary>
    public static MdlGltfSummary Export(XivEnv env, string path, string outDir,
        MdlGltfOptions? options = null, Action<string>? log = null)
    {
        var opts = options ?? new MdlGltfOptions();
        var texDir = Path.Combine(outDir, "tex");
        var texFiles = 0;
        string? Sink(DiffuseResolve.Diffuse d)
        {
            // one PNG per texture path, shared by every material that uses it
            var fileName = d.TexPath.Replace('/', '_') + ".png";
            var png = Path.Combine(texDir, fileName);
            if (!File.Exists(png))
            {
                Directory.CreateDirectory(texDir);
                using var fs = File.Create(png);
                TexOps.WritePng(d.Tex, fs, d.Mip);
                texFiles++;
            }
            return "tex/" + fileName;
        }
        var (w, sum) = Build(env, path, opts, Sink, log);
        sum.TexFiles = texFiles;
        var stem = Path.GetFileNameWithoutExtension(path) + (opts.Textured ? "-tex" : "");
        Directory.CreateDirectory(outDir);
        sum.GltfPath = Path.Combine(outDir, $"{stem}.gltf");
        w.Write(sum.GltfPath, Path.Combine(outDir, $"{stem}.bin"), stem);
        return sum;
    }

    /// <summary>Self-contained .gltf JSON (buffer + diffuse PNGs as data: URIs) -
    /// one HTTP response, no cache, no side files.</summary>
    public static (string Json, MdlGltfSummary Summary) BuildEmbedded(XivEnv env, string path,
        MdlGltfOptions? options = null, Action<string>? log = null)
    {
        var opts = options ?? new MdlGltfOptions();
        var uriByTex = new Dictionary<string, string>();   // texPath -> data URI (encode once)
        var texFiles = 0;
        string? Sink(DiffuseResolve.Diffuse d)
        {
            if (uriByTex.TryGetValue(d.TexPath, out var u)) return u;
            using var ms = new MemoryStream();
            TexOps.WritePng(d.Tex, ms, d.Mip);
            texFiles++;
            return uriByTex[d.TexPath] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        var (w, sum) = Build(env, path, opts, Sink, log);
        sum.TexFiles = texFiles;
        return (w.WriteEmbedded(Path.GetFileNameWithoutExtension(path)), sum);
    }

    static (GltfWriter w, MdlGltfSummary sum) Build(XivEnv env, string path, MdlGltfOptions opts,
        Func<DiffuseResolve.Diffuse, string?> texUri, Action<string>? log)
    {
        var mdl = env.Game.GetFile<MdlFile>(path)
                  ?? throw new FileNotFoundException($"not found: {path}");
        var lod = Math.Clamp(opts.Lod, 0, Math.Max(1, (int)mdl.FileHeader.LodCount) - 1);
        var model = new Model(mdl, (Model.ModelLod)lod);
        var w = new GltfWriter();
        var sum = new MdlGltfSummary { MdlPath = path, Lod = lod };

        var matByPath = new Dictionary<string, int>();
        int GetMaterial(string matPath)
        {
            if (matByPath.TryGetValue(matPath, out var idx)) return idx;
            string? uri = null;
            if (opts.Textured && matPath.EndsWith(".mtrl"))
            {
                try
                {
                    if (DiffuseResolve.TryResolve(env.Game, matPath, opts.MaxTexDim) is { } d)
                        uri = texUri(d);
                }
                catch (Exception e) { log?.Invoke($"  tex error {matPath}: {e.Message}"); }
                if (uri == null) sum.TexFailed++; else sum.TexMaterials++;
            }
            matByPath[matPath] = idx = uri != null
                ? w.AddMaterial(Path.GetFileNameWithoutExtension(matPath), Vector4.One, uri, alphaMask: true)
                : w.AddMaterial(Path.GetFileNameWithoutExtension(matPath), GltfWriter.Pastel(matPath));
            return idx;
        }

        var meshIdx = -1;
        foreach (var (mi, types) in MdlOps.LodMeshRanges(mdl, lod))
        {
            if (!types.Any(t => t is Mesh.MeshType.Main or Mesh.MeshType.Water
                    or Mesh.MeshType.LightShaft or Mesh.MeshType.Glass)) continue;
            var mesh = new Mesh(model, mi, types);
            if (mesh.Vertices is not { Length: > 0 } || mesh.Indices is not { Length: >= 3 }) continue;
            var pos = new float[mesh.Vertices.Length * 3];
            var nrm = new float[mesh.Vertices.Length * 3];
            var uv = new float[mesh.Vertices.Length * 2];
            for (var i = 0; i < mesh.Vertices.Length; i++)
            {
                var v = mesh.Vertices[i];
                var p = v.Position ?? Vector4.Zero;
                pos[i * 3] = p.X; pos[i * 3 + 1] = p.Y; pos[i * 3 + 2] = p.Z;
                var n = v.Normal ?? Vector3.UnitY;
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                nrm[i * 3] = n.X; nrm[i * 3 + 1] = n.Y; nrm[i * 3 + 2] = n.Z;
                var u = v.UV ?? Vector4.Zero;
                uv[i * 2] = u.X; uv[i * 2 + 1] = u.Y;
            }
            var idx = new uint[mesh.Indices.Length - mesh.Indices.Length % 3];
            for (var i = 0; i < idx.Length; i++) idx[i] = mesh.Indices[i];
            if (meshIdx < 0) meshIdx = w.AddMesh(Path.GetFileNameWithoutExtension(path));
            w.AddPrimitive(meshIdx, pos, nrm, uv, idx, GetMaterial(mesh.Material?.MaterialPath ?? path));
            sum.SourceMeshes++;
            sum.Vertices += mesh.Vertices.Length;
            sum.Triangles += idx.Length / 3;
            log?.Invoke($"  mesh {mi} [{string.Join('+', types.Select(t => t.ToString()))}]: " +
                        $"{mesh.Vertices.Length} verts, {idx.Length / 3} tris, mtrl {mesh.Material?.MaterialPath ?? "-"}");
        }
        if (meshIdx < 0) throw new Exception($"no visual meshes at lod {lod} (shadow/fog-only model?)");
        sum.Materials = matByPath.Count;

        var node = w.AddNode(Path.GetFileNameWithoutExtension(path), mesh: meshIdx,
            extras: new Dictionary<string, object?> { ["assetPath"] = path, ["lod"] = lod });
        w.AddSceneRoot(node);
        return (w, sum);
    }
}
