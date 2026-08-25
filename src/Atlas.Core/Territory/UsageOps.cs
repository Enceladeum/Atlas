// Asset-usage inventory: which placed instances use a given asset (.mdl/.sgb/
// .avfx/.scd), where. Two modes:
//   - Walk: live parse of ONE level dir's 7 lgb files (fast; server caches) —
//     the territory-scoped Find behind /api/usages?tt=.
//   - BuildIndex/QueryIndex: one-time global sweep over leveldirs.txt into a
//     CSV (rows for every asset-bearing instance in every territory), queried
//     for "which territories use this asset". The dumps/library instances.csv
//     deliberately EXCLUDES BgParts, so visual-asset usage needs this index.
// SharedGroups are expanded ONE level (same as Compose): each direct BgPart
// yields a row with Via = the sgb path and a world-composed position; the
// identity quadruple stays the PARENT SharedGroup instance (that IS the
// runtime InstanceKey).
// CSV columns (library convention: Label instead of TerritoryId — rows from
// staging dirs have no TT): Label,LgbFile,LayerId,InstanceId,AssetType,Asset,
// Via,X,Y,Z.
using System.Numerics;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Atlas.Core.Compose;
using Atlas.Core.Gltf;

namespace Atlas.Core.Territory;

public sealed record UsageRow(string Label, string LgbFile, uint LayerId, uint InstanceId,
    string AssetType, string Asset, string Via, float X, float Y, float Z);

public static class UsageOps
{
    public static readonly string[] LgbNames =
        ["bg", "planmap", "planevent", "planlive", "planner", "sound", "vfx"];

    /// <summary>All asset-bearing instances of one level dir (7 lgbs), sgbs
    /// expanded one level with world-composed part positions.</summary>
    public static List<UsageRow> Walk(GameData gd, string levelDir, string label,
        bool expandSgb = true, Action<string>? log = null)
    {
        var rows = new List<UsageRow>();
        var sgbCache = new Dictionary<string, List<ComposeOps.SgbPart>>();
        foreach (var f in LgbNames)
        {
            LgbFile? lgb = null;
            try { lgb = gd.GetFile<LgbFile>($"{levelDir}/{f}.lgb"); } catch { continue; }
            if (lgb == null) continue;
            foreach (var layer in lgb.Layers)
                foreach (var io in layer.InstanceObjects)
                {
                    var t = io.Transform;
                    var lt = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                    string? asset = io.Object switch
                    {
                        LayerCommon.BGInstanceObject b => b.AssetPath,
                        LayerCommon.SharedGroupInstanceObject s => s.AssetPath,
                        LayerCommon.VFXInstanceObject v => v.AssetPath,
                        LayerCommon.SoundInstanceObject sn => sn.AssetPath,
                        _ => null
                    };
                    if (string.IsNullOrEmpty(asset)) continue;
                    var type = io.AssetType.ToString();
                    rows.Add(new UsageRow(label, f, layer.LayerId, io.InstanceId, type, asset, "",
                        lt.X, lt.Y, lt.Z));
                    if (expandSgb && io.Object is LayerCommon.SharedGroupInstanceObject sg
                        && (sg.AssetPath ?? "").EndsWith(".sgb"))
                    {
                        if (!sgbCache.TryGetValue(sg.AssetPath!, out var parts))
                            sgbCache[sg.AssetPath!] = parts = ComposeOps.ReadSgbParts(gd, sg.AssetPath!, log).parts;
                        if (parts.Count == 0) continue;
                        var lr = new Vector3(t.Rotation.X, t.Rotation.Y, t.Rotation.Z);
                        var ls = new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z);
                        var q = GltfWriter.FromEulerXyz(lr);
                        foreach (var p in parts)
                        {
                            var wp = lt + Vector3.Transform(ls * p.T, q);
                            rows.Add(new UsageRow(label, f, layer.LayerId, io.InstanceId,
                                type, p.Asset, sg.AssetPath!, wp.X, wp.Y, wp.Z));
                        }
                    }
                }
        }
        return rows;
    }

    /// <summary>Case-insensitive substring filter on Asset (and Via).</summary>
    public static IEnumerable<UsageRow> Filter(IEnumerable<UsageRow> rows, string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return rows;
        return rows.Where(r =>
            r.Asset.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
            r.Via.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public const string IndexHeader = "Label,LgbFile,LayerId,InstanceId,AssetType,Asset,Via,X,Y,Z";

    public static void WriteRow(TextWriter w, UsageRow r) =>
        w.WriteLine(string.Join(",", Csv.Escape(r.Label), r.LgbFile, r.LayerId, r.InstanceId,
            r.AssetType, Csv.Escape(r.Asset), Csv.Escape(r.Via),
            Csv.Str(r.X), Csv.Str(r.Y), Csv.Str(r.Z)));

    /// <summary>Global sweep: leveldirs lines ("dir\tlabel") -> index CSV.
    /// Returns (dirs walked, rows written). Progress via log every 50 dirs.</summary>
    public static (int Dirs, int Rows) BuildIndex(GameData gd, IEnumerable<string> leveldirLines,
        TextWriter w, Action<string>? log = null)
    {
        w.WriteLine(IndexHeader);
        int nd = 0, nr = 0;
        foreach (var line in leveldirLines)
        {
            var parts = line.Split('\t');
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;
            var dir = parts[0].TrimEnd('/');
            var lbl = parts.Length > 1 ? parts[1] : dir;
            foreach (var r in Walk(gd, dir, lbl, expandSgb: true, log: null)) { WriteRow(w, r); nr++; }
            nd++;
            if (nd % 50 == 0) log?.Invoke($"# {nd} dirs, {nr} rows...");
        }
        return (nd, nr);
    }

    /// <summary>Parse index CSV rows (streaming; header skipped).</summary>
    public static IEnumerable<UsageRow> ReadIndex(TextReader r)
    {
        r.ReadLine();   // header
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var c = SplitCsv(line);
            if (c.Count < 10) continue;
            yield return new UsageRow(c[0], c[1],
                uint.TryParse(c[2], out var la) ? la : 0, uint.TryParse(c[3], out var ii) ? ii : 0,
                c[4], c[5], c[6],
                float.TryParse(c[7], out var x) ? x : 0, float.TryParse(c[8], out var y) ? y : 0,
                float.TryParse(c[9], out var z) ? z : 0);
        }
    }

    /// <summary>Minimal RFC-4180 line splitter for our own index rows (Csv.cs
    /// is writer-only by contract; asset paths never span lines).</summary>
    static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else q = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res;
    }
}
