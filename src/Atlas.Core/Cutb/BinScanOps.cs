using Lumina;

namespace Atlas.Core.Cutb;

/// <summary>
/// Ported from the standalone BinScan pipeline: grep every cutb binary for
/// little-endian u32 id patterns (e.g. casting-record ENPC row ids). Emits
/// one line per cutb with >=1 hit: CutsceneRow,Cutb,id|id|...
/// </summary>
public static class BinScanOps
{
    /// <summary>Scan cutbs for RowIds in [lo, hi). Returns (scanned, hitFiles).</summary>
    public static (int Scanned, int HitFiles) Scan(GameData gd,
        IEnumerable<(int Row, string Path)> cutscenes, IReadOnlyList<uint> idList,
        TextWriter w, Action<string>? log = null, int lo = 0, int hi = int.MaxValue)
    {
        var ids = new List<(uint Id, byte[] Pat)>();
        foreach (var v in idList) ids.Add((v, BitConverter.GetBytes(v))); // little-endian
        int scanned = 0, hitFiles = 0;
        foreach (var (rowId, p) in cutscenes)
        {
            if (rowId < lo || rowId >= hi) continue;
            if (!p.Contains('/')) continue;
            var f = gd.GetFile($"cut/{p}.cutb");
            if (f == null) continue;
            scanned++;
            var data = f.Data;
            var found = new List<uint>();
            foreach (var (id, pat) in ids)
            {
                for (var i = 0; i + 4 <= data.Length; i++)
                {
                    if (data[i] == pat[0] && data[i + 1] == pat[1] && data[i + 2] == pat[2] && data[i + 3] == pat[3])
                    { found.Add(id); break; }
                }
            }
            if (found.Count > 0)
            {
                hitFiles++;
                w.WriteLine($"{rowId},{p},{string.Join('|', found)}");
            }
        }
        w.Flush();
        log?.Invoke($"scanned={scanned} hitFiles={hitFiles}");
        return (scanned, hitFiles);
    }
}
