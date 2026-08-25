using Lumina.Excel;
using Atlas.Core;
using Atlas.Core.Exd;

namespace Atlas.Cli.Commands;

/// <summary>
/// bnpc index — searchable mob/model index for disguise work (HOutfits/DMS).
/// One row per BNpcBase, joined with ModelChara (Type/Model/Base/Variant) and,
/// where the community mapping provides it, a display name.
///
/// Names: the BNpcBase↔BNpcName pairing is SERVER-side (spawn packets), not in
/// client sheets or LGBs (verified: zero BattleNPC entries across all 642 level
/// dirs). --names takes the Anamnesis/Brio NpcNames.json ("B:&lt;base&gt;" → literal
/// name or "N:&lt;nameId&gt;" → BNpcName row); unmapped bases keep an empty Name and
/// are still searchable by m-number. Runtime capture (copy-target / passive
/// harvest) is the only complete source — plugin-side.
/// CSV: BaseId,NameId,Name,ModelCharaId,McType,McModel,McBase,McVariant,Scale
///   McType: 0=none 1=Human 2=DemiHuman 3=Monster; disguise id = ModelCharaId.
/// </summary>
public static class BnpcCommands
{
    public static int Run(XivEnv env, List<string> args)
    {
        if (args.Count == 0 || args[0] != "index")
        { Console.Error.WriteLine("usage: atlas bnpc index [--out <file>] [--names <NpcNames.json>]"); return 1; }
        string? outFile = null, namesFile = null;
        for (var i = 1; i < args.Count - 1; i++)
        {
            if (args[i] == "--out") outFile = args[i + 1];
            if (args[i] == "--names") namesFile = args[i + 1];
        }
        outFile ??= "mob-model-index.csv";

        int Col(string sheet, RawExcelSheet raw, string name)
        {
            var sn = SchemaNames.TryLoad(env.SchemaDir, sheet, raw, _ => { });
            if (sn == null) return -1;
            var k = Array.IndexOf(sn.Names, name);
            return k >= 0 ? sn.OffsetOrder[k] : -1;
        }

        // BNpcName: RowId -> Singular
        var nameRaw = env.Game.Excel.GetRawSheet("BNpcName");
        var nameCol = Col("BNpcName", nameRaw, "Singular");
        var bnpcNames = new Dictionary<uint, string>();
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "BNpcName"))
            bnpcNames[r.RowId] = Csv.Str(r.ReadColumn(nameCol < 0 ? 0 : nameCol));

        // Community mapping: BaseId -> (Name, NameId)
        var mapped = new Dictionary<uint, (string Name, uint NameId)>();
        if (namesFile != null && File.Exists(namesFile))
        {
            var doc = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(namesFile, System.Text.Encoding.UTF8).TrimStart('﻿'));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!p.Name.StartsWith("B:") || !uint.TryParse(p.Name[2..], out var baseId)) continue;
                var v = p.Value.GetString() ?? "";
                if (v.StartsWith("N:") && uint.TryParse(v[2..], out var nid))
                    mapped[baseId] = (bnpcNames.TryGetValue(nid, out var nm) ? nm : "", nid);
                else
                    mapped[baseId] = (v, 0);
            }
        }


        // NotoriousMonster: client-authoritative BNpcBase<->BNpcName pairs (all hunt
        // marks). Overrides the community mapping where present.
        var nmRaw = env.Game.Excel.GetRawSheet("NotoriousMonster");
        var nmN = Col("NotoriousMonster", nmRaw, "BNpcName");
        var nmB = Col("NotoriousMonster", nmRaw, "BNpcBase");
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "NotoriousMonster"))
        {
            var b = Convert.ToUInt32(r.ReadColumn(nmB));
            var n = Convert.ToUInt32(r.ReadColumn(nmN));
            if (b != 0 && n != 0 && bnpcNames.TryGetValue(n, out var nm2) && nm2.Length > 0)
                mapped[b] = (nm2, n);
        }

        var mcRaw = env.Game.Excel.GetRawSheet("ModelChara");
        var mT = Col("ModelChara", mcRaw, "Type");
        var mM = Col("ModelChara", mcRaw, "Model");
        var mB = Col("ModelChara", mcRaw, "Base");
        var mV = Col("ModelChara", mcRaw, "Variant");
        var mcs = new Dictionary<uint, (int T, int M, int B, int V)>();
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "ModelChara"))
            mcs[r.RowId] = (Convert.ToInt32(r.ReadColumn(mT)), Convert.ToInt32(r.ReadColumn(mM)),
                            Convert.ToInt32(r.ReadColumn(mB)), Convert.ToInt32(r.ReadColumn(mV)));

        var baseRaw = env.Game.Excel.GetRawSheet("BNpcBase");
        var bMc = Col("BNpcBase", baseRaw, "ModelChara");
        var bSc = Col("BNpcBase", baseRaw, "Scale");
        using var w = new StreamWriter(outFile);
        w.WriteLine("BaseId,NameId,Name,ModelCharaId,McType,McModel,McBase,McVariant,Scale");
        int nRows = 0, nNamed = 0;
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "BNpcBase"))
        {
            var mcId = Convert.ToUInt32(r.ReadColumn(bMc));
            if (mcId == 0) continue;                       // no model: nothing to disguise as
            var scale = Convert.ToSingle(r.ReadColumn(bSc));
            mcs.TryGetValue(mcId, out var mc);
            mapped.TryGetValue(r.RowId, out var nm);
            if (!string.IsNullOrEmpty(nm.Name)) nNamed++;
            w.WriteLine($"{r.RowId},{(nm.NameId == 0 ? "" : nm.NameId)},{Csv.Escape(nm.Name ?? "")}," +
                        $"{mcId},{mc.T},{mc.M},{mc.B},{mc.V},{Csv.Str(scale)}");
            nRows++;
        }
        Console.WriteLine($"bnpc index: {nRows} bases with models, {nNamed} named via mapping -> {outFile}");
        return 0;
    }
}
