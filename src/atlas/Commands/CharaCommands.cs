// Chara module CLI dispatch — equipment resolver (Atlas.Core.Chara).
//   atlas mod Chara imc <path.imc>
//       parse an .imc: header + per-part default/variant entries as CSV on stdout.
//   atlas mod Chara resolve <path.mdl> [--variant N] [--json]
//       classify the chara mdl, load its .imc, and print per-variant resolved
//       material paths with existence flags. --variant 0 = default entry.
//       Exit 2 when the path is not a resolvable chara asset or not in game data.

using System.Text.Json;
using Atlas.Core;
using Atlas.Core.Chara;

namespace Atlas.Cli.Commands;

public static class CharaCommands
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

        const string usage = "usage: atlas mod Chara <imc|resolve> <path> ...";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "imc":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Chara imc <path.imc>"); return 1; }
                    var imc = CharaOps.LoadImc(env, argv[1]);
                    Console.WriteLine($"# count={imc.Count} partMask=0x{imc.PartMask:X} parts={imc.Parts.Length}");
                    Console.WriteLine("Part,Variant,MaterialId,DecalId,AttributeMask,SoundId,VfxId,MaterialAnimationId");
                    for (var p = 0; p < imc.Parts.Length; p++)
                    {
                        void Row(int v, ImcEntry e) => Console.WriteLine(
                            $"{p},{v},{e.MaterialId},{e.DecalId},0x{e.AttributeMask:X},{e.SoundId},{e.VfxId},{e.MaterialAnimationId}");
                        Row(0, imc.Parts[p].Default);
                        for (var v = 0; v < imc.Parts[p].Variants.Length; v++) Row(v + 1, imc.Parts[p].Variants[v]);
                    }
                    return 0;
                }
                case "resolve":
                {
                    var variant = int.TryParse(Opt("--variant"), out var vn) ? (int?)vn : null;
                    var json = Flag("--json");
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Chara resolve <path.mdl> [--variant N] [--json]"); return 1; }
                    var r = CharaOps.Resolve(env, argv[1], variant);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(r, new JsonSerializerOptions
                        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                        return 0;
                    }
                    Console.WriteLine($"# {r.Ref.Category} pid={r.Ref.PrimaryId} sid={r.Ref.SecondaryId} slot={r.Ref.Slot} imc={r.ImcPath} variants=0..{r.ImcCount} part={r.PartIndex}");
                    Console.WriteLine($"# mdl materials: {string.Join(" | ", r.MdlMaterials)}");
                    Console.WriteLine("Variant,MaterialId,VfxId,Mtrl,Exists");
                    foreach (var v in r.Variants)
                    {
                        foreach (var m in v.Mtrls)
                            Console.WriteLine($"{v.Variant},{v.Entry.MaterialId},{v.Entry.VfxId},{m.Path},{(m.Exists ? 1 : 0)}");
                        if (v.VfxPath != null)
                            Console.WriteLine($"{v.Variant},{v.Entry.MaterialId},{v.Entry.VfxId},{v.VfxPath},{(v.VfxExists == true ? 1 : 0)}");
                    }
                    return 0;
                }
                default:
                    Console.Error.WriteLine(usage);
                    return 1;
            }
        }
        catch (FileNotFoundException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
    }
}
