// Mdl module CLI dispatch — 3D model inspection/export (Atlas.Core.Mdl).
//   atlas mod Mdl info <gamepath>                         per-LOD mesh/vertex/index counts,
//                                                         materials, attributes, bounding box
//   atlas mod Mdl obj  <gamepath> --out <file> [--lod 0]  Wavefront OBJ export (v/vt/vn + f,
//                                                         one o-group per mesh, usemtl = mtrl path)
//   atlas mod Mdl gltf <gamepath> --out <dir> [--lod 0] [--textured] [--texsize 1024]
//                                                         glTF export (visual meshes; --textured
//                                                         adds tex/*.png diffuse maps, mip-capped)

using System.Text;
using Atlas.Core;
using Atlas.Core.Mdl;

namespace Atlas.Cli.Commands;

public static class MdlCommands
{
    public static int Run(XivEnv env, List<string> argv)
    {
        string? Opt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }

        const string usage = "usage: atlas mod Mdl <info|obj|gltf> <gamepath> [--out <file|dir>] [--lod N] [--textured] [--texsize N]";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "info":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Mdl info <gamepath>"); return 1; }
                    var info = MdlOps.Info(env, argv[1]);
                    Console.WriteLine($"mdl: {info.Path}");
                    Console.WriteLine($"lods: {info.LodCount}  bones: {info.BoneCount}  shapes: {info.ShapeCount}");
                    Console.WriteLine(info.BboxMin is { } mn && info.BboxMax is { } mx
                        ? $"bbox: min ({mn.X:R}, {mn.Y:R}, {mn.Z:R})  max ({mx.X:R}, {mx.Y:R}, {mx.Z:R})"
                        : "bbox: degenerate/absent");
                    Console.WriteLine($"materials ({info.MaterialPaths.Count}):");
                    foreach (var m in info.MaterialPaths) Console.WriteLine($"  {m}");
                    if (info.AttributeNames.Count > 0)
                        Console.WriteLine($"attributes: {string.Join(", ", info.AttributeNames)}");
                    foreach (var lod in info.Lods)
                    {
                        Console.WriteLine($"lod {lod.Lod}: {lod.Meshes.Count} meshes, {lod.VertexTotal} verts, {lod.IndexTotal} indices ({lod.IndexTotal / 3} tris)");
                        foreach (var m in lod.Meshes)
                        {
                            var attrs = new List<string>();
                            if (m.HasNormal) attrs.Add("normal");
                            if (m.HasUv) attrs.Add("uv");
                            if (m.HasColor) attrs.Add("color");
                            if (m.HasTangent) attrs.Add("tangent");
                            if (m.HasBlend) attrs.Add("blend");
                            Console.WriteLine($"  mesh {m.MeshIndex} [{m.Types}]: {m.VertexCount} verts, {m.IndexCount} indices ({m.IndexCount / 3} tris), " +
                                              $"{m.SubmeshCount} submeshes, mtrl[{m.MaterialIndex}] {m.MaterialPath}, attrs: {string.Join('/', attrs)}");
                        }
                    }
                    return 0;
                }
                case "obj":
                {
                    var outPath = Opt("--out");
                    var lod = int.TryParse(Opt("--lod"), out var l) ? l : 0;
                    if (argv.Count < 2 || outPath == null)
                    { Console.Error.WriteLine("usage: atlas mod Mdl obj <gamepath> --out <file> [--lod 0]"); return 1; }
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    using (var w = new StreamWriter(outPath, false, new UTF8Encoding(false)))
                        MdlOps.ExportObj(env, argv[1], w, lod, Console.Error.WriteLine);
                    Console.WriteLine(outPath);
                    return 0;
                }
                case "gltf":
                {
                    var outDir = Opt("--out");
                    var lod = int.TryParse(Opt("--lod"), out var lg) ? lg : 0;
                    var texSize = int.TryParse(Opt("--texsize"), out var tsz) ? tsz : 1024;
                    var textured = argv.Remove("--textured");
                    if (argv.Count < 2 || outDir == null)
                    { Console.Error.WriteLine("usage: atlas mod Mdl gltf <gamepath> --out <dir> [--lod 0] [--textured] [--texsize 1024]"); return 1; }
                    var sum = Atlas.Core.Mdl.MdlGltf.Export(env, argv[1], outDir,
                        new MdlGltfOptions { Lod = lod, Textured = textured, MaxTexDim = texSize },
                        Console.Error.WriteLine);
                    Console.WriteLine($"lod {sum.Lod}: {sum.SourceMeshes} meshes, {sum.Vertices} verts, {sum.Triangles} tris, {sum.Materials} materials");
                    if (textured)
                        Console.WriteLine($"textures: {sum.TexMaterials} materials textured, {sum.TexFiles} pngs, {sum.TexFailed} pastel fallback");
                    Console.WriteLine(sum.GltfPath);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown Mdl verb: {verb} (verbs: info, obj, gltf)");
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }
}
