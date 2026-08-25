// Tex module CLI dispatch — texture inspection and PNG export (Atlas.Core.Tex).
//   atlas mod Tex info <gamepath>                          format, dims, mips, array/cube flags
//   atlas mod Tex png  <gamepath> --out <file> [--mip 0]   decode (incl. BC1/2/3/5/7) -> RGBA PNG

using Atlas.Core;
using Atlas.Core.Tex;

namespace Atlas.Cli.Commands;

public static class TexCommands
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

        const string usage = "usage: atlas mod Tex <info|png> <gamepath> [--out <file>] [--mip N]";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "info":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Tex info <gamepath>"); return 1; }
                    var t = TexOps.Info(env, argv[1]);
                    Console.WriteLine($"tex: {t.Path}");
                    Console.WriteLine($"format: {t.Format} (0x{t.FormatValue:X4})");
                    Console.WriteLine($"dims: {t.Width}x{t.Height}x{t.Depth}  mips: {t.MipCount}  arraySize: {t.ArraySize}");
                    var kinds = new List<string>();
                    if (t.Is1D) kinds.Add("1D");
                    if (t.Is2D) kinds.Add("2D");
                    if (t.Is3D) kinds.Add("3D");
                    if (t.IsCube) kinds.Add("cube");
                    if (t.IsArray) kinds.Add("array");
                    Console.WriteLine($"type: {string.Join('+', kinds)}  attributes: 0x{t.AttributeFlags:X8}");
                    return 0;
                }
                case "png":
                {
                    var outPath = Opt("--out");
                    var mip = int.TryParse(Opt("--mip"), out var m) ? m : 0;
                    if (argv.Count < 2 || outPath == null)
                    { Console.Error.WriteLine("usage: atlas mod Tex png <gamepath> --out <file> [--mip 0]"); return 1; }
                    var (w, h) = TexOps.WritePng(env, argv[1], outPath, mip);
                    Console.WriteLine($"wrote {w}x{h} png -> {outPath}");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown Tex verb: {verb} (verbs: info, png)");
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
