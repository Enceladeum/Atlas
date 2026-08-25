// Deps module CLI dispatch — asset dependency index (Atlas.Core.Deps).
//   atlas mod Deps index [--paths <list[.gz]>] [--out deps.csv] [--append] [--skip N] [--take N]
//       sweep .mdl -> .mtrl and .mtrl -> .tex edges into a CSV; --paths defaults
//       to ATLAS_PATHS (ResLogger2 list). --skip/--take slice AFTER extension
//       filtering so chunked runs are stable; --append omits the header row.
//       Writes <out>.ver game-version sidecar on completion.
//   atlas mod Deps refs <path> --index deps.csv [--out rows.csv]
//       both directions for one asset; chara-relative .mtrl refs matched by
//       "/"+filename automatically. Exit 2 when nothing references the path.

using Atlas.Core;
using Atlas.Core.Deps;
using Atlas.Core.Territory;

namespace Atlas.Cli.Commands;

public static class DepsCommands
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
        bool Flag(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0) return false;
            argv.RemoveAt(i);
            return true;
        }

        const string usage = "usage: atlas mod Deps <index|refs> ...";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "index":
                {
                    var pathsFile = Opt("--paths") ?? Environment.GetEnvironmentVariable("ATLAS_PATHS");
                    var outCsv = Opt("--out") ?? "deps.csv";
                    var append = Flag("--append");
                    var skip = int.TryParse(Opt("--skip"), out var s) ? s : 0;
                    var take = int.TryParse(Opt("--take"), out var t) ? t : int.MaxValue;
                    if (pathsFile == null || !File.Exists(pathsFile))
                    { Console.Error.WriteLine("missing --paths <file> (or ATLAS_PATHS): ResLogger2-style path list"); return 1; }

                    var paths = DepsOps.IndexablePaths(DepsOps.ReadPathsFile(pathsFile)).Skip(skip).Take(take);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var w = new StreamWriter(new FileStream(outCsv,
                        append ? FileMode.Append : FileMode.Create, FileAccess.Write));
                    var stats = DepsOps.BuildIndex(env, paths, w, header: !append,
                        log: m => Console.Error.WriteLine($"  {m}"));
                    w.Flush();
                    var ver = LevelDirs.GameVersion(env.Game);
                    if (ver != null) File.WriteAllText(outCsv + ".ver", ver);
                    Console.WriteLine($"{stats.Files} files -> {stats.Edges} edges " +
                        $"({stats.Missing} missing, {stats.Errors} errors) in {sw.Elapsed.TotalSeconds:0.0} s -> {outCsv}");
                    return 0;
                }
                case "refs":
                {
                    var index = Opt("--index") ?? "deps.csv";
                    var outCsv = Opt("--out");
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Deps refs <path> --index deps.csv [--out rows.csv]"); return 1; }
                    if (!File.Exists(index)) { Console.Error.WriteLine($"no index: {index} (build with `atlas mod Deps index`)"); return 1; }
                    var path = argv[1];
                    var ver = LevelDirs.GameVersion(env.Game);
                    var verFile = index + ".ver";
                    if (ver != null && File.Exists(verFile))
                    {
                        var stamped = File.ReadAllText(verFile).Trim();
                        if (stamped != ver)
                            Console.Error.WriteLine($"warning: index built for {stamped}, game is {ver} — rebuild with `atlas mod Deps index`");
                    }
                    var (uses, usedBy) = DepsOps.Query(index, path);
                    Console.WriteLine($"uses ({uses.Count}):");
                    foreach (var e in uses) Console.WriteLine($"  {e.Target}");
                    Console.WriteLine($"used by ({usedBy.Count}):");
                    foreach (var e in usedBy) Console.WriteLine($"  {e.Source}");
                    if (outCsv != null)
                    {
                        using var w = Csv.OpenWriter(outCsv);
                        w.WriteLine(DepsOps.Header);
                        foreach (var e in uses) w.WriteLine($"{e.Kind},{Csv.Escape(e.Source)},{Csv.Escape(e.Target)}");
                        foreach (var e in usedBy) w.WriteLine($"{e.Kind},{Csv.Escape(e.Source)},{Csv.Escape(e.Target)}");
                        Console.WriteLine($"rows -> {outCsv}");
                    }
                    return uses.Count == 0 && usedBy.Count == 0 ? 2 : 0;
                }
                default:
                    Console.Error.WriteLine(usage);
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
