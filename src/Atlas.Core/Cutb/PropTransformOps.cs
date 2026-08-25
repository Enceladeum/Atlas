using System.Text;
using Lumina;

namespace Atlas.Core.Cutb;

/// <summary>
/// cutscene-prop-transforms.csv builder — ported from CutScan `proptransforms`.
/// CTAL scene-object table: prop placements (entry sizes 232/288/292/348) with
/// world-space transform (+0xC pos, +0x18 rot rad, +0x24 scale) and path bound
/// via u32 @+0xBC relative to entry+0x8C into the NUL-separated path pool.
/// Golden header: CutsceneRow,Cutb,EntryId,EntrySize,Path,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ
/// </summary>
public static class PropTransformOps
{
    public const string Header = "CutsceneRow,Cutb,EntryId,EntrySize,Path,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ";

    static uint U32(byte[] d, int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);

    /// <summary>
    /// Locate the CTAL entry table in a CUTB file: scan u32 cnt (1..500) followed by
    /// cnt strictly increasing offsets with offs[0]==4*cnt; every entry must start
    /// {u32 type in (0xF,3), u32 id 0xFF000000|(index+1)}. Returns entry start
    /// offsets, or null if the cutb has no CTAL section / no valid table.
    /// </summary>
    public static int[]? FindCtalEntryTable(byte[] d)
    {
        if (d.Length < 0x10 || d[0] != 'C' || d[1] != 'U' || d[2] != 'T' || d[3] != 'B') return null;
        var nSec = (int)U32(d, 8);
        var ctal = -1;
        for (int i = 0, off = 0xC; i < nSec && off + 16 <= d.Length; i++, off += 16)
            if (d[off] == 'C' && d[off + 1] == 'T' && d[off + 2] == 'A' && d[off + 3] == 'L') { ctal = (int)U32(d, off + 8); break; }
        if (ctal < 0) return null;
        for (var q = ctal + 0x10; q < Math.Min(ctal + 0x400, d.Length - 4); q += 4)
        {
            var cnt = (int)U32(d, q);
            if (cnt < 1 || cnt > 500 || q + 4 + 4 * cnt > d.Length) continue;
            var offs = new int[cnt];
            var ok = true;
            for (var k = 0; k < cnt; k++)
            {
                offs[k] = (int)U32(d, q + 4 + 4 * k);
                if (k > 0 && offs[k] <= offs[k - 1]) { ok = false; break; }
            }
            if (!ok || offs[0] != 4 * cnt) continue;
            var st = new int[cnt];
            for (var k = 0; k < cnt && ok; k++)
            {
                st[k] = q + 4 + offs[k];
                if (st[k] + 0x30 > d.Length) { ok = false; break; }
                var ty = U32(d, st[k]);
                var id = U32(d, st[k] + 4);
                if (ty != 0xF && ty != 3) { ok = false; break; }
                if ((id >> 24) != 0xFF || (id & 0xFFFFFF) != (uint)(k + 1)) { ok = false; break; }
            }
            if (ok) return st;
        }
        return null;
    }

    /// <summary>Rows for cutscene RowIds in [lo, hi). Returns (scanned, withTable, bound).</summary>
    public static (int Scanned, int WithTable, int Bound) Write(GameData gd,
        IEnumerable<(int Row, string Path)> cutscenes, int lo, int hi,
        TextWriter w, bool header, Action<string>? log = null)
    {
        if (header) w.WriteLine(Header);
        int scanned = 0, withTable = 0, bound = 0;
        var seenCutb = new HashSet<string>();
        foreach (var (rowId, p) in cutscenes)
        {
            if (rowId < lo || rowId >= hi) continue;
            if (!p.Contains('/') || !seenCutb.Add(p)) continue;
            var f = gd.GetFile($"cut/{p}.cutb");
            if (f == null) continue;
            scanned++;
            var d = f.Data;
            if (d.Length < 0x10 || d[0] != 'C' || d[1] != 'U' || d[2] != 'T' || d[3] != 'B') continue;
            float F32(int at) => BitConverter.ToSingle(d, at);
            var starts = FindCtalEntryTable(d);
            if (starts == null) continue;
            withTable++;
            var n = starts.Length;
            // pool: first printable run (>=8) after last entry start + min entry size, then NUL-separated strings
            var poolStarts = new Dictionary<int, string>();
            {
                var scanFrom = starts[n - 1] + 0x8C;
                var cap = Math.Min(scanFrom + 0x8000, d.Length);
                int i = scanFrom, runStart = -1;
                while (i < cap && runStart < 0)
                {
                    if (d[i] >= 0x20 && d[i] < 0x7F)
                    {
                        var j = i;
                        while (j < cap && d[j] >= 0x20 && d[j] < 0x7F) j++;
                        if (j - i >= 8) runStart = i; else i = j + 1;
                    }
                    else i++;
                }
                if (runStart >= 0)
                {
                    var i2 = runStart;
                    while (i2 < cap && d[i2] >= 0x20 && d[i2] < 0x7F)
                    {
                        var j = i2;
                        while (j < cap && d[j] >= 0x20 && d[j] < 0x7F) j++;
                        if (j - i2 >= 4) poolStarts[i2] = Encoding.ASCII.GetString(d, i2, j - i2);
                        i2 = j + 1; // skip single NUL
                    }
                }
            }
            for (var k = 0; k < n; k++)
            {
                var st = starts[k];
                var size = k + 1 < n ? starts[k + 1] - starts[k] : -1;
                if (size >= 0 && size < 0xC4) continue;         // too small to hold +0xBC
                if (st + 0xC0 > d.Length) continue;
                var val = (int)U32(d, st + 0xBC);
                var tgt = st + 0x8C + val;
                if (!poolStarts.TryGetValue(tgt, out var path)) continue;
                float x = F32(st + 0xC), y = F32(st + 0x10), z = F32(st + 0x14);
                float rx = F32(st + 0x18), ry = F32(st + 0x1C), rz = F32(st + 0x20);
                float sx = F32(st + 0x24), sy = F32(st + 0x28), sz = F32(st + 0x2C);
                w.WriteLine(FormattableString.Invariant($"{rowId},{p},{k + 1},{size},{path},{x:0.###},{y:0.###},{z:0.###},{rx:0.####},{ry:0.####},{rz:0.####},{sx:0.###},{sy:0.###},{sz:0.###}"));
                bound++;
            }
        }
        w.Flush();
        log?.Invoke($"scanned={scanned} withTable={withTable} bound={bound}");
        return (scanned, withTable, bound);
    }
}
