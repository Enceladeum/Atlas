// Map module CLI dispatch - composed-map exports (Atlas.Core.Compose + .Gltf).
//   atlas mod Map gltf <territoryId|bg-level-dir> --out <dir> [--layers id1,id2] [--lod 0]
// Writes <out>/map-<tt>.gltf + map-<tt>.bin: the territory's bg.lgb visual
// layout as one glTF 2.0 scene (root -> layer-<id>-<name> group nodes ->
// instance nodes; node extras = identity quadruple + assetPath). Coordinates
// match the Pcb collision OBJ (same S*Rx*Ry*Rz*T convention, no axis flips).
//   --layers    restrict to these LayerIds (comma-separated)
//   --lod       mesh level of detail, 0 = high (default), clamped per model
//   --textured  export diffuse PNGs under <out>/tex/ + wire baseColorTexture;
//               output becomes map-<tt>-tex.gltf (default outputs stay golden)
//   --texsize   textured: cap texture dimension via mip pick (default 1024, 0 = mip 0)

using Atlas.Core;
using Atlas.Core.Compose;

namespace Atlas.Cli.Commands;

public static class MapCommands
{
    const string Usage = "usage: atlas mod Map gltf <territoryId|bg-level-dir> --out <dir> [--layers id1,id2] [--lod 0] [--textured] [--texsize 1024]";

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

        bool Flag(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0) return false;
            argv.RemoveAt(i);
            return true;
        }

        if (argv.Count == 0) { Console.Error.WriteLine(Usage); return 1; }
        var verb = argv[0];
        switch (verb)
        {
            case "gltf":
            {
                var outDir = Opt("--out");
                var layersArg = Opt("--layers");
                var lodArg = Opt("--lod");
                var texSizeArg = Opt("--texsize");
                var textured = Flag("--textured");
                if (argv.Count < 2 || outDir == null) { Console.Error.WriteLine(Usage); return 1; }
                var opts = new ComposeOptions();
                if (lodArg != null)
                {
                    if (!int.TryParse(lodArg, out var lod) || lod is < 0 or > 2)
                    { Console.Error.WriteLine("error: --lod must be 0..2"); return 1; }
                    opts.Lod = lod;
                }
                if (layersArg != null)
                {
                    opts.Layers = new HashSet<uint>();
                    foreach (var p in layersArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!uint.TryParse(p, out var id))
                        { Console.Error.WriteLine($"error: bad layer id '{p}'"); return 1; }
                        opts.Layers.Add(id);
                    }
                }
                opts.Textured = textured;
                if (texSizeArg != null)
                {
                    if (!textured) { Console.Error.WriteLine("error: --texsize requires --textured"); return 1; }
                    if (!int.TryParse(texSizeArg, out var ts) || ts is < 0 or > 8192)
                    { Console.Error.WriteLine("error: --texsize must be 0..8192"); return 1; }
                    opts.MaxTexDim = ts;
                }
                try
                {
                    var s = ComposeOps.MapGltf(env.Game, argv[1], outDir, opts, Console.Error.WriteLine);
                    var texInfo = opts.Textured
                        ? $", {s.TexMaterials} textured materials ({s.TexFiles} pngs, {s.TexFailed} fallback)" : "";
                    Console.WriteLine(
                        $"map-{s.Label}: {s.Instances} bg instances, {s.UniqueMeshes} unique meshes, " +
                        $"{s.SgbGroups} sgb groups ({s.SgbParts} parts resolved one level, {s.SgbNestedSkipped} nested sgb skipped), " +
                        $"{s.OtherSkipped} other instances skipped, {s.FailedMdl} mdl failed, {s.Layers} layers{texInfo} -> {s.GltfPath}");
                    return 0;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"error: {e.Message}");
                    return 1;
                }
            }
            default:
                Console.Error.WriteLine($"unknown Map verb: {verb} (verbs: gltf)");
                return 1;
        }
    }
}
