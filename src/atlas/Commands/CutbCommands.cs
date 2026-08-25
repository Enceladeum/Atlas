using System.Text;
using Atlas.Core;
using Atlas.Core.Cutb;
using Atlas.Core.Tmb;

namespace Atlas.Cli.Commands;

/// <summary>
/// Cutb/Tmb module dispatch (agent D). Invoked via: atlas mod Cutb <verb> ...
///   sceneparts --out f [--from N --to N] [--append] [--list cutscene.csv]
///   props      --out f [--from N --to N] [--append] [--list cutscene.csv]
///   keyframes  --out f [--from N --to N] [--append] [--list cutscene.csv]
///   scan <u32,u32,...> [--list cutscene.csv] [--out f] [--from N --to N]
/// Cutscene list defaults to the live Cutscene sheet; --list reads a dumped
/// Cutscene.csv instead (identical content — kept for parity testing).
/// </summary>
public static class CutbCommands
{
    public static int Run(XivEnv env, List<string> argv)
    {
        if (argv.Count == 0)
        {
            Console.Error.WriteLine("usage: atlas mod Cutb <sceneparts|props|keyframes|scan> ...");
            return 1;
        }
        var verb = argv[0];
        argv = argv.GetRange(1, argv.Count - 1);

        string? GetOpt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }
        bool GetFlag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }

        var outPath = GetOpt("--out");
        var listPath = GetOpt("--list");
        var lo = int.TryParse(GetOpt("--from"), out var l) ? l : 0;
        var hi = int.TryParse(GetOpt("--to"), out var h) ? h : int.MaxValue;
        var append = GetFlag("--append");
        Action<string> log = s => Console.Error.WriteLine(s);

        var cutscenes = listPath != null ? CutsceneCatalog.FromCsv(listPath) : CutsceneCatalog.FromSheet(env);

        TextWriter OpenOut(out bool header)
        {
            if (outPath == null) { header = !append; return Console.Out; }
            if (append)
            {
                header = false;
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                return new StreamWriter(outPath, true, new UTF8Encoding(false));
            }
            header = true;
            return Csv.OpenWriter(outPath);
        }

        switch (verb)
        {
            case "sceneparts":
            {
                if (outPath == null) { Console.Error.WriteLine("usage: atlas mod Cutb sceneparts --out f [--from N --to N] [--append] [--list f]"); return 1; }
                using var w = OpenOut(out var header);
                ScenePartsOps.Write(env.Game, cutscenes, lo, hi, w, header, log);
                return 0;
            }
            case "props":
            {
                if (outPath == null) { Console.Error.WriteLine("usage: atlas mod Cutb props --out f [--from N --to N] [--append] [--list f]"); return 1; }
                using var w = OpenOut(out var header);
                PropTransformOps.Write(env.Game, cutscenes, lo, hi, w, header, log);
                return 0;
            }
            case "keyframes":
            {
                if (outPath == null) { Console.Error.WriteLine("usage: atlas mod Cutb keyframes --out f [--from N --to N] [--append] [--list f]"); return 1; }
                using var w = OpenOut(out var header);
                TimelineOps.Write(env.Game, cutscenes, lo, hi, w, header, log);
                return 0;
            }
            case "scan":
            {
                if (argv.Count < 1) { Console.Error.WriteLine("usage: atlas mod Cutb scan <u32,u32,...> [--list f] [--out f] [--from N --to N]"); return 1; }
                var ids = new List<uint>();
                foreach (var s in argv[0].Split(',')) ids.Add(uint.Parse(s));
                TextWriter w = Console.Out;
                StreamWriter? fw = null;
                if (outPath != null) { fw = append ? new StreamWriter(outPath, true, new UTF8Encoding(false)) : Csv.OpenWriter(outPath); w = fw; }
                BinScanOps.Scan(env.Game, cutscenes, ids, w, log, lo, hi);
                fw?.Flush();
                fw?.Dispose();
                return 0;
            }
            default:
                Console.Error.WriteLine($"unknown Cutb verb: {verb}");
                return 1;
        }
    }
}
