using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;

namespace Atlas.Core.Lgb;

/// <summary>
/// Game-wide LGB layer census — rewrite of the lost `lgbindex` builder (layers.csv half),
/// recovered verbatim from the pre-consolidation xivtool Program.cs (sandbox copy).
/// Golden: dumps/library/layers.csv. KNOWN GOLDEN BUG (kept for parity): the LayerSetIds
/// column is Lumina's LayerSetReferences.LayerSetId values, which are wrong (often duplicate
/// InstanceCount) — use layer-filters.csv for real filter data.
/// </summary>
public static class LayerCensus
{
    public const string Header = "Label,Dir,File,LayerId,LayerName,FestivalID,FestivalPhase,IsTemporary,IsHousing,InstanceCount,BgPartCount,LayerSetIds";

    /// <summary>
    /// Write layer rows for dir-list lines ("bg/.../level\tLABEL"; missing label => "").
    /// Row order: input line order, then LayerFilterOps.LgbNames order, then layer order.
    /// Returns (dirsWithLgbs, lgbFiles, layers).
    /// </summary>
    public static (int Dirs, int Files, int Layers) Write(
        XivEnv env, IEnumerable<string> dirLines, TextWriter w, bool writeHeader = true, Action<string>? log = null)
    {
        if (writeHeader) w.WriteLine(Header);
        int nd = 0, nf = 0, nl = 0;
        foreach (var line in dirLines)
        {
            var parts = line.Split('\t');
            var dir = parts[0].TrimEnd('/');
            var lbl = parts.Length > 1 ? parts[1] : "";
            var found = false;
            foreach (var f in LayerFilterOps.LgbNames)
            {
                LgbFile? lgb = null;
                try { lgb = env.Game.GetFile<LgbFile>($"{dir}/{f}.lgb"); } catch { continue; }
                if (lgb == null) continue;
                found = true; nf++;
                foreach (var layer in lgb.Layers)
                {
                    var bgCount = 0;
                    foreach (var io in layer.InstanceObjects)
                        if (io.Object is LayerCommon.BGInstanceObject) bgCount++;
                    var lsIds = string.Join("|", layer.LayerSetReferences.Select(x => x.LayerSetId));
                    w.WriteLine(string.Join(",",
                        Csv.Escape(lbl), dir, f, layer.LayerId, Csv.Escape(layer.Name ?? ""),
                        layer.FestivalID, layer.FestivalPhaseID, layer.IsTemporary, layer.IsHousing,
                        layer.InstanceObjectCount, bgCount, Csv.Escape(lsIds)));
                    nl++;
                }
            }
            if (found) nd++;
            if (nd % 50 == 0 && found) log?.Invoke($"# {nd} dirs done...");
        }
        return (nd, nf, nl);
    }
}
