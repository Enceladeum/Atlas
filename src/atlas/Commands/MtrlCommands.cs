// Mtrl module CLI dispatch — material dumper (Atlas.Core.Mtrl).
//   atlas mod Mtrl dump <gamepath> [--out <file>]
// Readable text: shpk name, texture paths + usage slots, samplers (id hex + CRC-recovered
// name), constants (id hex + name + float values), shader keys, color-table presence/dims.
// Relative chara paths (leading '/') are resolved with variant 1.

using Atlas.Core;
using Atlas.Core.Mtrl;

namespace Atlas.Cli.Commands;

public static class MtrlCommands
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

        const string usage = "usage: atlas mod Mtrl dump <gamepath> [--out <file>]";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "dump":
                {
                    var outPath = Opt("--out");
                    if (argv.Count < 2) { Console.Error.WriteLine(usage); return 1; }
                    var info = MtrlOps.Dump(env, argv[1]);
                    if (outPath != null)
                    {
                        using var w = Csv.OpenWriter(outPath); // UTF-8 no BOM writer, dirs created
                        MtrlOps.WriteDump(info, w);
                        Console.WriteLine(outPath);
                    }
                    else
                        MtrlOps.WriteDump(info, Console.Out);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown Mtrl verb: {verb} (verbs: dump)");
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
