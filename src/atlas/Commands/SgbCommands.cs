using System.Text;
using Atlas.Core;
using Atlas.Core.Sgb;

namespace Atlas.Cli.Commands;

/// <summary>
/// CLI dispatch for the Sgb module (reflection-invoked from Program.cs `mod` case).
///   atlas mod Sgb paths   --out <f> (--library <dir> | --instances <csv> --sceneparts <csv>)
///   atlas mod Sgb layouts --out <f> (--paths <f> | --library <dir> | --instances <csv> --sceneparts <csv>)
///                           [--from N] [--to N] [--append] [--vfx-paths]
/// Chunking: first chunk without --append writes the header; later chunks use
/// --append (header suppressed), reproducing CutScan's lo==0 header rule.
/// --vfx-paths (additive, non-golden): also emit the avfx AssetPath for Type=4 Vfx
/// rows (later CutScan behavior; the golden file has it empty).
/// </summary>
public static class SgbCommands
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
        Action<string> log = s => Console.Error.WriteLine(s);

        if (argv.Count == 0) { Usage(); return 1; }
        var verb = argv[0];

        List<string>? ResolvePaths()
        {
            var pathsFile = GetOpt("--paths");
            if (pathsFile != null) return File.ReadAllLines(pathsFile).ToList();
            var libDir = GetOpt("--library");
            var instances = GetOpt("--instances") ?? (libDir != null ? Path.Combine(libDir, "instances.csv") : null);
            var sceneParts = GetOpt("--sceneparts") ?? (libDir != null ? Path.Combine(libDir, "scene-parts.csv") : null);
            if (instances == null || sceneParts == null)
            {
                Console.Error.WriteLine("error: need --paths <file>, or --library <dir>, or --instances <csv> --sceneparts <csv>");
                return null;
            }
            return SgbLayouts.BuildPathList(instances, sceneParts);
        }

        switch (verb)
        {
            case "paths":
            {
                // Build + persist the SGB enumeration so chunked layout runs share a stable order.
                var outPath = GetOpt("--out");
                if (outPath == null) { Usage(); return 1; }
                var paths = ResolvePaths();
                if (paths == null) return 1;
                using var w = Csv.OpenWriter(outPath);
                foreach (var p in paths) w.WriteLine(p);
                log($"paths {paths.Count} -> {outPath}");
                return 0;
            }
            case "layouts":
            {
                var outPath = GetOpt("--out");
                var from = int.TryParse(GetOpt("--from"), out var lo) ? lo : 0;
                var toOpt = GetOpt("--to");
                var append = GetFlag("--append");
                var vfxPaths = GetFlag("--vfx-paths");
                if (outPath == null) { Usage(); return 1; }
                var paths = ResolvePaths();
                if (paths == null) return 1;
                var to = toOpt != null && int.TryParse(toOpt, out var hi) ? hi : paths.Count;

                using var w = append ? OpenAppend(outPath) : Csv.OpenWriter(outPath);
                if (!append) w.WriteLine(SgbLayouts.Header);
                var st = SgbLayouts.Run(env.Game, paths, w, from, to, vfxPaths, log);
                log($"chunk [{from},{Math.Min(to, paths.Count)}) of {paths.Count} paths -> {outPath}" + (append ? " (append)" : ""));
                return 0;
            }
            default:
                Console.Error.WriteLine($"unknown Sgb verb: {verb}");
                Usage();
                return 1;
        }
    }

    static StreamWriter OpenAppend(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new StreamWriter(path, true, new UTF8Encoding(false));
    }

    static void Usage()
    {
        Console.Error.WriteLine("usage: atlas mod Sgb <verb> ...");
        Console.Error.WriteLine("  paths   --out <f> (--library <dir> | --instances <csv> --sceneparts <csv>)");
        Console.Error.WriteLine("  layouts --out <f> (--paths <f> | --library <dir> | --instances <csv> --sceneparts <csv>)");
        Console.Error.WriteLine("          [--from N] [--to N] [--append] [--vfx-paths]");
    }
}
