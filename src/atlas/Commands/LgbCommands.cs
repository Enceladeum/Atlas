using System.Text;
using Atlas.Core;
using Atlas.Core.Lgb;
using Atlas.Core.Lvb;

namespace Atlas.Cli.Commands;

/// <summary>
/// Agent A module dispatch: atlas mod Lgb &lt;verb&gt; --dirs leveldirs.txt --out f
///   sets       LVB SCN1 layer-set filter keys      (golden: layer-sets.csv)
///   filters    per-LGB-layer filter ops            (golden: layer-filters.csv)
///   layers     game-wide layer census (Lumina)     (golden: layers.csv)
///   instances  game-wide non-BG instance census    (golden: instances.csv)
/// Chunking: --from N --to N (0-based line range over the dirs file, to-exclusive) + --append.
/// Dirs file: one "bg/.../level\tLABEL" per line (canonical: Core/Lgb/leveldirs.txt, 638 dirs).
/// </summary>
public static class LgbCommands
{
    public static int Run(XivEnv env, List<string> argv)
    {
        string? GetOpt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }
        bool GetFlag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }

        if (argv.Count == 0)
        {
            Console.Error.WriteLine("usage: atlas mod Lgb <sets|filters|layers|instances> --dirs <leveldirs.txt> --out <f> [--from N] [--to N] [--append]");
            return 1;
        }
        var verb = argv[0];
        var outPath = GetOpt("--out");
        var dirsPath = GetOpt("--dirs");
        var from = int.TryParse(GetOpt("--from"), out var fv) ? fv : 0;
        var to = int.TryParse(GetOpt("--to"), out var tv) ? tv : int.MaxValue;
        var append = GetFlag("--append");
        Action<string> log = s => Console.Error.WriteLine(s);

        if (outPath == null) { Console.Error.WriteLine("error: --out required"); return 1; }
        if (dirsPath == null && File.Exists("leveldirs.txt")) dirsPath = "leveldirs.txt";
        if (dirsPath == null || !File.Exists(dirsPath))
        {
            Console.Error.WriteLine("error: --dirs <leveldirs.txt> required (one \"bg/.../level<TAB>LABEL\" per line)");
            return 1;
        }

        var all = File.ReadAllLines(dirsPath);
        if (from < 0) from = 0;
        if (to > all.Length) to = all.Length;
        if (from >= to) { Console.Error.WriteLine($"empty range [{from},{to}) of {all.Length} dirs"); return 1; }
        var slice = new List<string>(all[from..to]);
        var writeHeader = !append;

        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        using var w = append
            ? new StreamWriter(outPath, true, new UTF8Encoding(false))
            : Csv.OpenWriter(outPath);

        switch (verb)
        {
            case "sets":
            {
                var (files, missing, noScn, filters) = LvbOps.WriteLayerSets(env, slice, w, writeHeader, log);
                Console.Error.WriteLine($"# sets [{from},{to}): files={files} missing={missing} noScn={noScn} filters={filters}");
                return 0;
            }
            case "filters":
            {
                var (files, layers, filtered) = LayerFilterOps.Write(env, slice, w, writeHeader, log);
                Console.Error.WriteLine($"# filters [{from},{to}): files={files} layers={layers} withFilterKeys={filtered}");
                return 0;
            }
            case "layers":
            {
                var (nd, nf, nl) = LayerCensus.Write(env, slice, w, writeHeader, log);
                Console.Error.WriteLine($"# layers [{from},{to}): dirs={nd} files={nf} layers={nl}");
                return 0;
            }
            case "instances":
            {
                var (nd, nf, ni) = InstanceCensus.Write(env, slice, w, writeHeader, log);
                Console.Error.WriteLine($"# instances [{from},{to}): dirs={nd} files={nf} rows={ni}");
                return 0;
            }
            default:
                Console.Error.WriteLine($"unknown Lgb verb: {verb} (sets|filters|layers|instances)");
                return 1;
        }
    }
}
