namespace Atlas.Core.Territory;

// Stage/composition data for the layers pane, read from the dumps/library CSVs
// (layer-sets.csv = the LVB LayerSet table: one row per keyed composition;
//  layer-filters.csv = each LGB layer's LayerSetReferencedList: FilterOp + keys).
// Visibility semantics under an active key K:
//   Op None    -> visible (ungated; includes the server-toggled Gate-3a layers)
//   Op Match   -> visible iff K is in Keys
//   Op NoMatch -> visible iff K is NOT in Keys
// Zones whose progression is server-toggled (759 Doman Enclave: every bg layer is
// op=None) get nothing from the key axis; those are handled by curated presets on
// the web side (web/js/stages.js).
public sealed record StageKey(uint Key, int Index, uint TerritoryTypeId);
public sealed record LayerFilter(uint LayerId, string LayerName, string Op, uint[] Keys, string Lgb);
public sealed record StageData(StageKey[] Keys, LayerFilter[] Filters);

public static class LayerStages
{
    public static StageData? Load(string libraryDir, uint tt)
    {
        var setsFile = Path.Combine(libraryDir, "layer-sets.csv");
        var filtersFile = Path.Combine(libraryDir, "layer-filters.csv");
        if (!File.Exists(setsFile) || !File.Exists(filtersFile)) return null;

        var label = $"TT{tt}";
        var keys = new List<StageKey>();
        string? homeLabel = null;

        // Pass 1: rows labelled with this tt. Pass 2 (fallback for secondary tts that
        // share a level dir, e.g. 925/926 riding on TT919's n4eb): any row whose
        // TerritoryTypeId column is this tt; adopt that row's label for the filters.
        var setRows = File.ReadLines(setsFile).Skip(1).Select(l => l.Split(',')).Where(c => c.Length >= 6).ToList();
        foreach (var c in setRows.Where(c => c[0] == label))
        {
            homeLabel = c[0];
            if (int.TryParse(c[3], out var idx) && uint.TryParse(c[4], out var key) && uint.TryParse(c[5], out var kt))
                keys.Add(new StageKey(key, idx, kt));
        }
        if (homeLabel == null)
        {
            foreach (var c in setRows.Where(c => c[5] == tt.ToString()))
            {
                homeLabel = c[0];
                break;
            }
            if (homeLabel != null)
                foreach (var c in setRows.Where(c => c[0] == homeLabel))
                    if (int.TryParse(c[3], out var idx) && uint.TryParse(c[4], out var key) && uint.TryParse(c[5], out var kt))
                        keys.Add(new StageKey(key, idx, kt));
        }
        if (homeLabel == null) return null;

        var filters = new List<LayerFilter>();
        foreach (var line in File.ReadLines(filtersFile).Skip(1))
        {
            var c = line.Split(',');
            if (c.Length < 7 || c[0] != homeLabel) continue;
            if (!uint.TryParse(c[3], out var lid)) continue;
            var fk = c[6].Length == 0 ? Array.Empty<uint>()
                : c[6].Split('|').Select(s => uint.TryParse(s, out var v) ? v : 0u).Where(v => v != 0).ToArray();
            filters.Add(new LayerFilter(lid, c[4], c[5], fk, c[2]));
        }
        return new StageData(keys.OrderBy(k => k.Index).ToArray(), filters.ToArray());
    }
}
