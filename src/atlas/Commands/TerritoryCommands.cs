// Territory module CLI dispatch — per-territory research workspace.
//   atlas territory [dump] <tt> --out <dir> [--library <libdir>] [--collision]
//   (alias: atlas mod Territory dump ...)
// <tt> = TerritoryType row id. Produces <out>/{summary.md, territorytype.csv,
// layer-sets.csv, layer-filters.csv, layers.csv, instances.csv, layer-axes.csv,
// npcs.csv, quest-npcs.csv, quest-policy.csv, exits.csv, cfc.csv, vfx.csv}
// (+ collision/ with --collision). --library defaults to dumps/library in CWD.

using Atlas.Core;
using Atlas.Core.Territory;

namespace Atlas.Cli.Commands;

public static class TerritoryCommands
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

        if (argv.Count == 0 || argv[0] != "dump") { Usage(); return 1; }
        var outDir = Opt("--out");
        var libDir = Opt("--library");
        var collision = Flag("--collision");
        if (argv.Count < 2 || outDir == null || !uint.TryParse(argv[1], out var tt)) { Usage(); return 1; }

        libDir ??= Path.Combine("dumps", "library");
        // The manifest slice, exit in-edges and library-label resolution need these:
        foreach (var need in new[] { "quest-npc-manifest.csv", "instances.csv" })
            if (!File.Exists(Path.Combine(libDir, need)))
            {
                Console.Error.WriteLine($"error: library file missing: {Path.Combine(libDir, need)}");
                Console.Error.WriteLine("       pass --library <dir> pointing at a dumps/library checkout " +
                                        "(quest-npcs.csv, quest-policy.csv and exits.csv in-edges are sliced from it)");
                return 1;
            }

        try
        {
            var res = TerritoryWorkspace.Run(env, tt, outDir, libDir, collision, Console.Error.WriteLine);
            Console.Error.WriteLine($"# workspace TT{res.TerritoryId} ({res.Place}) -> {outDir}: " +
                string.Join(" ", res.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine(outDir);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    static void Usage() =>
        Console.Error.WriteLine("usage: atlas territory [dump] <territoryId> --out <dir> [--library <libdir>] [--collision]  (alias: mod Territory dump)");
}
