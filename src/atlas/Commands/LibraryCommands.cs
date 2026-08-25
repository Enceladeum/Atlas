using System.Diagnostics;
using Atlas.Core;
using Atlas.Core.Cutb;
using Atlas.Core.Lgb;
using Atlas.Core.Lvb;
using Atlas.Core.Sgb;
using Atlas.Core.Tmb;

namespace Atlas.Cli.Commands;

/// <summary>
/// Wave 3 flagship: regenerate the ENTIRE CSV library for the current game
/// patch with one command, in-process (no chunking — run it under nohup).
///
///   atlas mod Library rebuild --out &lt;dir&gt; [--only a,b,c]
///                               [--dirs leveldirs.txt] [--list Cutscene.csv]
///
/// Targets, in dependency order (name -&gt; file in --out):
///   sets        layer-sets.csv
///   filters     layer-filters.csv
///   layers      layers.csv
///   instances   instances.csv                    (input to sgbpaths)
///   props       cutscene-prop-transforms.csv
///   keyframes   cutscene-timeline-keyframes.csv
///   sceneparts  scene-parts.csv                  (input to sgbpaths; slowest)
///   sgbpaths    sgb-paths.txt   (intermediate: distinct sorted sgb union of
///                                instances.csv + scene-parts.csv, per NOTES-C)
///   sgblayouts  sgb-layouts.csv (from sgb-paths.txt)
///
/// Conventions: DEFAULT flags everywhere (no --vfx-paths), so outputs are
/// convention-identical to the shipped dumps/library goldens. The cutscene
/// list defaults to the LIVE Cutscene sheet — rebuild's purpose is
/// CURRENT-patch truth; byte-reproducing the OLD goldens additionally needs
/// the archived list (--list dumps/raw/Cutscene.csv) and pre-patch game data
/// (see Core/Cutb/NOTES-D.md).
///
/// --only runs a comma-separated subset in canonical order; the CALLER owns
/// dependency completeness (e.g. sgbpaths needs instances.csv +
/// scene-parts.csv already present in --out).
/// --dirs: leveldirs.txt (canonical checked-in copy: src/Atlas.Core/Lgb/);
/// probed in CWD and next to the executable if omitted.
/// Progress: one "# target ..." line per target on stderr with counts +
/// elapsed; module-level log lines are forwarded to stderr too.
/// </summary>
public static class LibraryCommands
{
    static readonly string[] AllTargets =
        ["sets", "filters", "layers", "instances", "props", "keyframes", "sceneparts", "sgbpaths", "sgblayouts"];

    static readonly string[] DirsTargets = ["sets", "filters", "layers", "instances"];
    static readonly string[] CutsceneTargets = ["props", "keyframes", "sceneparts"];

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
        Action<string> log = s => Console.Error.WriteLine(s);

        if (argv.Count == 0) { Usage(); return 1; }
        if (argv[0] == "diff") return RunDiff(argv, GetOpt, log);
        if (argv[0] == "usage-index") return RunUsageIndex(env, GetOpt, log);
        if (argv[0] == "where") return RunWhere(env, argv, GetOpt, log);
        if (argv[0] != "rebuild") { Usage(); return 1; }
        var outDir = GetOpt("--out");
        var only = GetOpt("--only");
        var dirsOpt = GetOpt("--dirs");
        var listOpt = GetOpt("--list");
        if (outDir == null) { Usage(); return 1; }
        Directory.CreateDirectory(outDir);

        // Resolve target set (canonical order regardless of --only order).
        List<string> targets;
        if (only == null)
        {
            targets = new List<string>(AllTargets);
        }
        else
        {
            var req = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in req)
                if (!AllTargets.Contains(t))
                {
                    Console.Error.WriteLine($"error: unknown target '{t}' (valid: {string.Join(",", AllTargets)})");
                    return 1;
                }
            targets = AllTargets.Where(t => req.Contains(t)).ToList();
            if (targets.Count == 0) { Console.Error.WriteLine("error: --only selected no targets"); return 1; }
        }

        // Resolve leveldirs.txt up-front if any Lgb/Lvb target is selected.
        List<string>? dirLines = null;
        if (targets.Any(DirsTargets.Contains))
        {
            var dirsPath = ResolveDirsFile(dirsOpt);
            if (dirsPath == null)
            {
                Console.Error.WriteLine("error: leveldirs.txt not found — pass --dirs <leveldirs.txt> " +
                                        "(canonical copy: src/Atlas.Core/Lgb/leveldirs.txt)");
                return 1;
            }
            dirLines = File.ReadAllLines(dirsPath).ToList();
            log($"# dirs {dirLines.Count} <- {dirsPath}");
        }

        // Cutscene catalog: live sheet by default (current-patch truth), or --list csv.
        List<(int Row, string Path)>? cutscenes = null;
        List<(int Row, string Path)> Cutscenes()
        {
            if (cutscenes == null)
            {
                cutscenes = listOpt != null ? CutsceneCatalog.FromCsv(listOpt) : CutsceneCatalog.FromSheet(env);
                log($"# cutscenes {cutscenes.Count} <- {(listOpt ?? "live Cutscene sheet")}");
            }
            return cutscenes;
        }

        string Out(string file) => Path.Combine(outDir, file);
        var total = Stopwatch.StartNew();
        log($"# rebuild -> {outDir} targets: {string.Join(",", targets)}");

        foreach (var target in targets)
        {
            var sw = Stopwatch.StartNew();
            // PARTIAL-TARGET guard: write to .tmp, rename on completion, so a killed
            // run never leaves a truncated CSV that resume mistakes for a finished one.
            var finName = LibraryDiff.Files.First(f => f.Target == target).FileName;
            var tmpPath = Out(finName) + ".tmp";
            switch (target)
            {
                case "sets":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (files, missing, noScn, filters) = LvbOps.WriteLayerSets(env, dirLines!, w, true, log);
                    Progress(target, "layer-sets.csv", $"rows={filters} files={files} missing={missing} noScn={noScn}", sw);
                    break;
                }
                case "filters":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (files, layers, filtered) = LayerFilterOps.Write(env, dirLines!, w, true, log);
                    Progress(target, "layer-filters.csv", $"rows={layers} files={files} withFilterKeys={filtered}", sw);
                    break;
                }
                case "layers":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (nd, nf, nl) = LayerCensus.Write(env, dirLines!, w, true, log);
                    Progress(target, "layers.csv", $"rows={nl} dirs={nd} files={nf}", sw);
                    break;
                }
                case "instances":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (nd, nf, ni) = InstanceCensus.Write(env, dirLines!, w, true, log);
                    Progress(target, "instances.csv", $"rows={ni} dirs={nd} files={nf}", sw);
                    break;
                }
                case "props":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (scanned, withTable, bound) = PropTransformOps.Write(env.Game, Cutscenes(), 0, int.MaxValue, w, true, log);
                    Progress(target, "cutscene-prop-transforms.csv", $"rows={bound} scanned={scanned} withTable={withTable}", sw);
                    break;
                }
                case "keyframes":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var (scanned, withTmlb, rows) = TimelineOps.Write(env.Game, Cutscenes(), 0, int.MaxValue, w, true, log);
                    Progress(target, "cutscene-timeline-keyframes.csv", $"rows={rows} scanned={scanned} withTmlb={withTmlb}", sw);
                    break;
                }
                case "sceneparts":
                {
                    using var w = Csv.OpenWriter(tmpPath);
                    var rows = ScenePartsOps.Write(env.Game, Cutscenes(), 0, int.MaxValue, w, true, log);
                    Progress(target, "scene-parts.csv", $"rows={rows}", sw);
                    break;
                }
                case "sgbpaths":
                {
                    var instances = Out("instances.csv");
                    var sceneParts = Out("scene-parts.csv");
                    if (!File.Exists(instances) || !File.Exists(sceneParts))
                    {
                        Console.Error.WriteLine($"error: sgbpaths needs {instances} + {sceneParts} (run instances + sceneparts first)");
                        return 1;
                    }
                    var paths = SgbLayouts.BuildPathList(instances, sceneParts);
                    using var w = Csv.OpenWriter(tmpPath);
                    foreach (var p in paths) w.WriteLine(p);
                    Progress(target, "sgb-paths.txt", $"rows={paths.Count}", sw);
                    break;
                }
                case "sgblayouts":
                {
                    List<string> paths;
                    var pathsFile = Out("sgb-paths.txt");
                    if (File.Exists(pathsFile))
                    {
                        paths = File.ReadAllLines(pathsFile).ToList();
                    }
                    else if (File.Exists(Out("instances.csv")) && File.Exists(Out("scene-parts.csv")))
                    {
                        paths = SgbLayouts.BuildPathList(Out("instances.csv"), Out("scene-parts.csv"));
                    }
                    else
                    {
                        Console.Error.WriteLine("error: sgblayouts needs sgb-paths.txt (or instances.csv + scene-parts.csv) in --out (run sgbpaths first)");
                        return 1;
                    }
                    using var w = Csv.OpenWriter(tmpPath);
                    w.WriteLine(SgbLayouts.Header);
                    // vfxPaths stays FALSE: default flags == golden conventions.
                    var st = SgbLayouts.Run(env.Game, paths, w, 0, int.MaxValue, false, log);
                    Progress(target, "sgb-layouts.csv", $"rows={st.Rows} files={st.Files} missing={st.Missing} bad={st.Bad}", sw);
                    break;
                }
            }
            File.Move(tmpPath, Out(finName), overwrite: true);
        }

        log($"# rebuild done: {targets.Count} target(s) in {total.Elapsed.TotalSeconds:F1}s");
        return 0;

        void Progress(string target, string file, string counts, Stopwatch sw) =>
            log($"# {target} -> {file}: {counts} elapsed={sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Patch-drift guard: atlas mod Library diff &lt;dirA&gt; &lt;dirB&gt;
    ///   [--out report.md] [--only a,b,c] [--examples N]
    /// A = old/baseline (e.g. dumps/library), B = new (e.g. fresh rebuild).
    /// Exit codes: 0 = identical, 2 = drift found, 1 = usage/error.
    /// </summary>
    static int RunDiff(List<string> argv, Func<string, string?> getOpt, Action<string> log)
    {
        var outPath = getOpt("--out");
        var only = getOpt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maxExamples = int.TryParse(getOpt("--examples"), out var n) ? n : 5;
        if (argv.Count < 3)
        {
            Console.Error.WriteLine("usage: atlas mod Library diff <dirA> <dirB> [--out report] [--only a,b,c] [--examples N]");
            return 1;
        }
        var dirA = argv[1];
        var dirB = argv[2];
        if (!Directory.Exists(dirA)) { Console.Error.WriteLine($"error: not a directory: {dirA}"); return 1; }
        if (!Directory.Exists(dirB)) { Console.Error.WriteLine($"error: not a directory: {dirB}"); return 1; }
        if (only != null)
            foreach (var t in only)
                if (!LibraryDiff.Files.Any(f => f.Target == t))
                {
                    Console.Error.WriteLine($"error: unknown target '{t}' (valid: {string.Join(",", LibraryDiff.Files.Select(f => f.Target))})");
                    return 1;
                }

        using var report = outPath != null ? Csv.OpenWriter(outPath) : Console.Out;
        var results = LibraryDiff.Run(dirA, dirB, report, only, maxExamples, log);
        var dirty = results.Count(r => !r.Identical);
        if (outPath != null)
            log($"# diff -> {outPath}: {(dirty == 0 ? "CLEAN" : $"DRIFT in {dirty} file(s)")}");
        return dirty == 0 ? 0 : 2;
    }

    /// <summary>Probe for leveldirs.txt: explicit --dirs, CWD, beside the executable.</summary>
    static string? ResolveDirsFile(string? opt)
    {
        if (opt != null) return File.Exists(opt) ? opt : null;
        if (File.Exists("leveldirs.txt")) return Path.GetFullPath("leveldirs.txt");
        var beside = Path.Combine(AppContext.BaseDirectory, "leveldirs.txt");
        if (File.Exists(beside)) return beside;
        return null;
    }

    // ---------- asset-usage inventory (UsageOps) ----------

    /// <summary>Global asset->placement index: every asset-bearing instance
    /// (BgParts/SharedGroup+parts/VFX/Sound) in every level dir. Dirs are
    /// discovered live (TerritoryType + cutscene stages) so the index tracks
    /// patches; --dirs <leveldirs.txt> overrides with a static list. A
    /// <out>.ver sidecar records the game version the index was built from.</summary>
    static int RunUsageIndex(XivEnv env, Func<string, string?> getOpt, Action<string> log)
    {
        var outCsv = getOpt("--out");
        var dirsOpt = getOpt("--dirs");
        if (outCsv == null)
        {
            Console.Error.WriteLine("usage: atlas mod Library usage-index --out <csv> [--dirs <leveldirs.txt>]");
            return 1;
        }
        IEnumerable<string> lines;
        if (dirsOpt != null)
        {
            var dirsPath = ResolveDirsFile(dirsOpt);
            if (dirsPath == null) { Console.Error.WriteLine($"error: --dirs {dirsOpt} not found"); return 1; }
            lines = File.ReadLines(dirsPath);
        }
        else
        {
            var disc = Atlas.Core.Territory.LevelDirs.Discover(env, log);
            var probe = ResolveDirsFile(null);   // CWD or beside the exe
            if (probe != null)
            {
                var n = disc.Count;
                disc = Atlas.Core.Territory.LevelDirs.Merge(disc, File.ReadLines(probe));
                if (disc.Count > n) log($"# +{disc.Count - n} static-only dir(s) merged from {Path.GetFileName(probe)}");
            }
            lines = disc.Select(d => d.Dir + "\t" + d.Label);
        }
        var sw = Stopwatch.StartNew();
        int nd, nr;
        using (var w = Atlas.Core.Csv.OpenWriter(outCsv))
            (nd, nr) = Atlas.Core.Territory.UsageOps.BuildIndex(env.Game, lines, w, log);
        var ver = Atlas.Core.Territory.LevelDirs.GameVersion(env.Game);
        if (ver != null) File.WriteAllText(outCsv + ".ver", ver);
        Console.WriteLine($"{nd} level dirs, {nr} rows -> {outCsv} ({sw.Elapsed.TotalSeconds:0.0}s)"
                          + (ver != null ? $" [game {ver}]" : ""));
        return 0;
    }

    /// <summary>Find placements of an asset (or name fragment). --tt N walks
    /// that territory live; otherwise --index <csv> (from usage-index) is
    /// searched. --out writes full CSV; stdout shows a per-label summary.</summary>
    static int RunWhere(XivEnv env, List<string> argv, Func<string, string?> getOpt, Action<string> log)
    {
        var tt = getOpt("--tt");
        var index = getOpt("--index");
        var outCsv = getOpt("--out");
        if (argv.Count < 2)
        {
            Console.Error.WriteLine("usage: atlas mod Library where <fragment> [--tt N | --index <usage-index.csv>] [--out <csv>]");
            return 1;
        }
        var frag = argv[1];
        List<Atlas.Core.Territory.UsageRow> rows;
        if (tt != null)
        {
            var dir = Atlas.Core.Compose.ComposeOps.ResolveLevelDir(env.Game, tt, out var label);
            rows = Atlas.Core.Territory.UsageOps.Filter(
                Atlas.Core.Territory.UsageOps.Walk(env.Game, dir, $"TT{label}", expandSgb: true, log: log), frag).ToList();
        }
        else if (index != null)
        {
            var iv = File.Exists(index + ".ver") ? File.ReadAllText(index + ".ver").Trim() : null;
            var gv = Atlas.Core.Territory.LevelDirs.GameVersion(env.Game);
            if (iv != null && gv != null && iv != gv)
                Console.Error.WriteLine($"warning: index built for game {iv}, current is {gv} — rebuild with usage-index");
            using var r = new StreamReader(index);
            rows = Atlas.Core.Territory.UsageOps.Filter(
                Atlas.Core.Territory.UsageOps.ReadIndex(r), frag).ToList();
        }
        else
        {
            Console.Error.WriteLine("error: pass --tt N (live walk) or --index <csv> (global; build with usage-index)");
            return 1;
        }
        if (outCsv != null)
        {
            using var w = Atlas.Core.Csv.OpenWriter(outCsv);
            w.WriteLine(Atlas.Core.Territory.UsageOps.IndexHeader);
            foreach (var r in rows) Atlas.Core.Territory.UsageOps.WriteRow(w, r);
        }
        foreach (var g in rows.GroupBy(r => r.Label).OrderBy(g => g.Key))
        {
            var assets = g.Select(r => r.Asset).Distinct().Count();
            Console.WriteLine($"  {g.Key}: {g.Count()} placements, {assets} distinct assets");
        }
        Console.WriteLine($"{rows.Count} placements" + (outCsv != null ? $" -> {outCsv}" : ""));
        return rows.Count > 0 ? 0 : 2;
    }

    static void Usage()
    {
        Console.Error.WriteLine("usage: atlas library rebuild --out <dir> [--only a,b,c] [--dirs <leveldirs.txt>] [--list <Cutscene.csv>]");
        Console.Error.WriteLine("       atlas library diff <dirA> <dirB> [--out report] [--only a,b,c] [--examples N]   (alias: mod Library ...)");
        Console.Error.WriteLine("       atlas library usage-index --out <csv> [--dirs <leveldirs.txt>]");
        Console.Error.WriteLine("       atlas library where <fragment> [--tt N | --index <csv>] [--out <csv>]");
        Console.Error.WriteLine($"  targets (dependency order): {string.Join(",", AllTargets)}");
        Console.Error.WriteLine("  diff exit codes: 0 identical, 2 drift, 1 error; where: 2 = no matches");
    }
}
