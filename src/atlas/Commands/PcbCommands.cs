// Pcb module CLI dispatch — territory geometry/collision dumper (port of LgbDump DumpCore).
//   atlas mod Pcb dump <territoryId|bg-level-dir> --out <dir> [--obj] [--tri-groups] [--modelbox]
// Creates <out>/lgb-<id>-<mapcode>/ with the 7 per-LGB CSVs, collision.csv,
// pcb-meshes.csv and (with --obj) collision-mesh.obj.
//   --obj        world-space collision mesh OBJ, one group per collider
//   --tri-groups (needs --obj) split OBJ groups per triangle class (_invis/_unland/_wall/_floor)
//   --modelbox   ModelBox colliders get real AABB/box geometry from the .mdl bounding box
// Default (no upgrade flags) output is byte-identical to the original LgbDump tool.

using Atlas.Core;
using Atlas.Core.Pcb;

namespace Atlas.Cli.Commands;

public static class PcbCommands
{
    public static int Run(XivEnv env, List<string> argv)
    {
        bool Flag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }
        string? Opt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }

        if (argv.Count == 0) { Console.Error.WriteLine("usage: atlas mod Pcb dump <territoryId|bg-level-dir> --out <dir> [--obj] [--tri-groups] [--modelbox]"); return 1; }
        var verb = argv[0];
        switch (verb)
        {
            case "dump":
            {
                var outRoot = Opt("--out");
                var opts = new TerritoryDumpOptions
                {
                    ExportObj = Flag("--obj"),
                    TriGroups = Flag("--tri-groups"),
                    ModelBoxBounds = Flag("--modelbox"),
                };
                if (argv.Count < 2 || outRoot == null)
                { Console.Error.WriteLine("usage: atlas mod Pcb dump <territoryId|bg-level-dir> --out <dir> [--obj] [--tri-groups] [--modelbox]"); return 1; }
                if (opts.TriGroups && !opts.ExportObj)
                { Console.Error.WriteLine("error: --tri-groups requires --obj"); return 1; }
                try
                {
                    var outDir = TerritoryDump.Run(env.Game, argv[1], outRoot, opts, Console.Error.WriteLine);
                    Console.WriteLine(outDir);
                    return 0;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"error: {e.Message}");
                    return 1;
                }
            }
            default:
                Console.Error.WriteLine($"unknown Pcb verb: {verb} (verbs: dump)");
                return 1;
        }
    }
}
