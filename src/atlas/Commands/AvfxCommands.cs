// Avfx module CLI dispatch — visual-effect inspection (Atlas.Core.Avfx).
//   atlas mod Avfx info <gamepath>               counts, texture paths, embedded-model dims
//   atlas mod Avfx dump <gamepath>               full block tree (heuristic, leaf previews)
//   atlas mod Avfx gltf <gamepath> --out <dir>   embedded particle models -> <stem>.gltf/.bin

using Atlas.Core;
using Atlas.Core.Avfx;
using Atlas.Core.Gltf;

namespace Atlas.Cli.Commands;

public static class AvfxCommands
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

        const string usage = "usage: atlas mod Avfx <info|dump|gltf> <gamepath> [--out <dir>]";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "info":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Avfx info <gamepath>"); return 1; }
                    var a = AvfxOps.Info(env, argv[1]);
                    Console.WriteLine($"avfx: {a.Path}");
                    Console.WriteLine($"version: 0x{a.Version:X8}");
                    Console.WriteLine($"schedulers: {a.Schedulers}  timelines: {a.Timelines}  emitters: {a.Emitters}  particles: {a.Particles}  effectors: {a.Effectors}  binders: {a.Binders}");
                    Console.WriteLine($"textures: {a.Textures.Count}");
                    foreach (var t in a.Textures) Console.WriteLine($"  {t}");
                    Console.WriteLine($"models: {a.Models.Count}");
                    foreach (var m in a.Models) Console.WriteLine($"  [{m.Index}] {m.VertexCount} verts, {m.TriCount} tris");
                    return 0;
                }
                case "dump":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Avfx dump <gamepath>"); return 1; }
                    foreach (var line in AvfxOps.Dump(env, argv[1]))
                        Console.WriteLine(line);
                    return 0;
                }
                case "gltf":
                {
                    var outDir = Opt("--out");
                    if (argv.Count < 2 || outDir == null)
                    { Console.Error.WriteLine("usage: atlas mod Avfx gltf <gamepath> --out <dir>"); return 1; }
                    var gltf = new GltfWriter();
                    var models = AvfxOps.BuildModelsGltf(env, argv[1], gltf);
                    if (models.Count == 0) { Console.Error.WriteLine("no embedded models in this avfx"); return 2; }
                    Directory.CreateDirectory(outDir);
                    var stem = Path.GetFileNameWithoutExtension(argv[1]);
                    var gltfPath = Path.Combine(outDir, stem + ".gltf");
                    gltf.Write(gltfPath, Path.Combine(outDir, stem + ".bin"));
                    Console.WriteLine($"wrote {models.Count} model(s) -> {gltfPath}");
                    foreach (var m in models) Console.WriteLine($"  [{m.Index}] {m.VertexCount} verts, {m.TriCount} tris");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown Avfx verb: {verb} (verbs: info, dump, gltf)");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
