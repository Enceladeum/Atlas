using System.Text;

namespace Atlas.Core.Lvb;

/// <summary>
/// LVB SCN1 layer-set (FileSceneFilter) extraction — faithful port of CutScan `lvbsets`.
/// Golden: dumps/library/layer-sets.csv. One row per SCN1 filter entry per level dir:
/// the Key is what runtime LayoutManager.LayerFilterKey and LGB layer filters match against.
/// </summary>
public static class LvbOps
{
    public const string LayerSetsHeader = "Label,Dir,LvbPath,FilterIndex,Key,TerritoryTypeId,CfcId,ServerNavMesh";

    /// <summary>
    /// Write layer-set rows for the given dir-list lines (each line: "bg/.../level\tLABEL";
    /// lines without a tab are skipped, matching the original). Row order == input line order.
    /// Returns (files, missing, noScn, filters).
    /// </summary>
    public static (int Files, int Missing, int NoScn, int Filters) WriteLayerSets(
        XivEnv env, IEnumerable<string> dirLines, TextWriter w, bool writeHeader = true, Action<string>? log = null)
    {
        if (writeHeader) w.WriteLine(LayerSetsHeader);
        int files = 0, missing = 0, noScn = 0, filters = 0;
        foreach (var line in dirLines)
        {
            var tab = line.Split('\t');
            if (tab.Length < 2) continue;
            var dir = tab[0].TrimEnd('/'); var label = tab[1];
            var comps = dir.Split('/');
            var leaf = comps[^2]; // bg/.../<leaf>/level -> <leaf>.lvb inside level dir
            var lvbPath = $"{dir}/{leaf}.lvb";
            var f = env.Game.GetFile(lvbPath);
            if (f == null) { missing++; log?.Invoke($"MISSING {lvbPath}"); continue; }
            files++;
            var d = f.Data;
            uint U32(int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
            // FileHeader: magic, totalSize, numSections; sections follow at 0xC
            int nSec = (int)U32(8);
            int off = 0xC; int scn = -1;
            for (int i = 0; i < nSec && off + 8 <= d.Length; i++)
            {
                var magic = Encoding.ASCII.GetString(d, off, 4);
                if (magic == "SCN1") { scn = off + 8; break; }
                off += (int)U32(off + 4);
            }
            if (scn < 0) { noScn++; continue; }
            int offFilters = (int)U32(scn + 0x0C);
            if (offFilters <= 0) continue;
            int fl = scn + offFilters;
            int offEntries = (int)U32(fl);
            int numEntries = (int)U32(fl + 4);
            for (int k = 0; k < numEntries; k++)
            {
                int e = fl + offEntries + k * 0x1C;
                if (e + 0x1C > d.Length) break;
                uint u0 = U32(e), key = U32(e + 4);
                ushort tt = (ushort)(d[e + 0x10] | d[e + 0x11] << 8), cfc = (ushort)(d[e + 0x12] | d[e + 0x13] << 8);
                string nvm = "";
                int sp = e + (int)u0;
                if (u0 > 0 && sp < d.Length)
                {
                    int se = sp;
                    while (se < d.Length && d[se] >= 0x20 && d[se] < 0x7F) se++;
                    if (se > sp) nvm = Encoding.ASCII.GetString(d, sp, se - sp);
                }
                // NOTE: no CSV escaping here, matching the original builder (golden has no quoted fields).
                w.WriteLine($"{label},{dir},{lvbPath},{k},{key},{tt},{cfc},{nvm}");
                filters++;
            }
        }
        return (files, missing, noScn, filters);
    }
}
