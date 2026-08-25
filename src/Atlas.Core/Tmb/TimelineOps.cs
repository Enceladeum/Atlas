using System.Text;
using Lumina;
using Atlas.Core.Cutb;

namespace Atlas.Core.Tmb;

/// <summary>
/// cutscene-timeline-keyframes.csv builder — ported from CutScan `cttl`.
/// Finds every TMLB block in a cutb, walks TMAC (object handle = CTAL entry id)
/// -> TMTR (track ref = TMTR id + 6) -> C018 (entry ref = C-node id + 2)
/// world-space transform keyframes, and resolves the handle against the CTAL
/// object table (kind/path/actor id/placement pos). Golden header:
/// CutsceneRow,Cutb,TmlbIdx,Handle,Kind,Path,ActorId,TmacTime,C018Id,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,MatchesCtal
/// </summary>
public static class TimelineOps
{
    public const string Header = "CutsceneRow,Cutb,TmlbIdx,Handle,Kind,Path,ActorId,TmacTime,C018Id,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,MatchesCtal";

    /// <summary>Rows for cutscene RowIds in [lo, hi). Returns (scanned, withTmlb, rows).</summary>
    public static (int Scanned, int WithTmlb, int Rows) Write(GameData gd,
        IEnumerable<(int Row, string Path)> cutscenes, int lo, int hi,
        TextWriter w, bool header, Action<string>? log = null)
    {
        if (header) w.WriteLine(Header);
        int scanned = 0, withTmlb = 0, rows = 0, matchown = 0, boundC = 0;
        var seenCutb = new HashSet<string>();
        foreach (var (rowId, p) in cutscenes)
        {
            if (rowId < lo || rowId >= hi) continue;
            if (!p.Contains('/') || !seenCutb.Add(p)) continue;
            var f = gd.GetFile($"cut/{p}.cutb");
            if (f == null) continue;
            var d = f.Data;
            if (d.Length < 0x10 || d[0] != 'C' || d[1] != 'U' || d[2] != 'T' || d[3] != 'B') continue;
            scanned++;
            uint U32(int at) => (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
            ushort U16(int at) => (ushort)(d[at] | d[at + 1] << 8);
            float F32(int at) => BitConverter.ToSingle(d, at);
            // ---- CTAL object table (optional): handle -> (kind,path,actorId,pos) ----
            var ctalKind = new Dictionary<int, string>();
            var ctalPath = new Dictionary<int, string>();
            var ctalActor = new Dictionary<int, uint>();
            var ctalPos = new Dictionary<int, (float, float, float)>();
            {
                var starts = PropTransformOps.FindCtalEntryTable(d);
                if (starts != null)
                {
                    var n = starts.Length;
                    for (var k = 0; k < n; k++)
                    {
                        var st = starts[k];
                        var size = k + 1 < n ? starts[k + 1] - starts[k] : -1;
                        ctalPos[k + 1] = (F32(st + 0xC), F32(st + 0x10), F32(st + 0x14));
                        var kind = size == 196 ? "chara" : size == 140 ? "actor" : size == 144 ? "light" : size == 288 ? "prop" : size == 348 ? "sgb" : "other";
                        ctalKind[k + 1] = kind;
                        if (size == 140 && st + 0x44 <= d.Length) ctalActor[k + 1] = U32(st + 0x40);
                        if ((size == 288 || size == 348) && st + 0xC0 <= d.Length)
                        {
                            var val = (int)U32(st + 0xBC);
                            var tgt = st + 0x8C + val;
                            if (tgt > 0 && tgt < d.Length && d[tgt] >= 0x20 && d[tgt] < 0x7F)
                            {
                                var e2 = tgt;
                                while (e2 < d.Length && d[e2] >= 0x20 && d[e2] < 0x7F) e2++;
                                if (e2 - tgt >= 4) ctalPath[k + 1] = Encoding.ASCII.GetString(d, tgt, e2 - tgt);
                            }
                        }
                    }
                }
            }
            // ---- find TMLBs ----
            var tmlbs = new List<int>();
            for (var i = 0; i + 12 < d.Length; i++)
                if (d[i] == 'T' && d[i + 1] == 'M' && d[i + 2] == 'L' && d[i + 3] == 'B')
                { var sz = U32(i + 4); if (sz >= 16 && i + sz <= d.Length + 16) tmlbs.Add(i); }
            if (tmlbs.Count > 0) withTmlb++;
            var tmlbIdx = 0;
            foreach (var t in tmlbs)
            {
                tmlbIdx++;
                var size = (int)U32(t + 4);
                var end = Math.Min(t + size, d.Length);
                var nodes = new List<(string tag, int pos, int sz)>();
                var np = t + 12;
                while (np + 8 <= end)
                {
                    var okTag = true;
                    for (var c = 0; c < 4; c++) { var b = d[np + c]; if (!((b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9'))) { okTag = false; break; } }
                    if (!okTag) break;
                    var nsz = (int)U32(np + 4);
                    if (nsz < 8 || np + nsz > end + 8) break;
                    nodes.Add((Encoding.ASCII.GetString(d, np, 4), np, nsz));
                    np += nsz;
                }
                var byid = new Dictionary<int, (string tag, int pos, int sz)>();
                foreach (var nd in nodes) { if (nd.tag == "TMAL") continue; int nid = U16(nd.pos + 8); if (!byid.ContainsKey(nid)) byid[nid] = nd; }
                var trefs = new List<int>();
                var minTmtr = int.MaxValue;
                foreach (var nd in nodes)
                {
                    if (nd.tag == "TMTR") { int nid = U16(nd.pos + 8); if (nid < minTmtr) minTmtr = nid; }
                    else if (nd.tag == "TMAC")
                    {
                        var tg = nd.pos + 0x14 + (int)U32(nd.pos + 0x14);
                        var nn = (int)U32(nd.pos + 0x18);
                        for (var i2 = 0; i2 < nn && tg + 2 * i2 + 2 <= d.Length; i2++) trefs.Add(U16(tg + 2 * i2));
                    }
                }
                if (trefs.Count == 0 || minTmtr == int.MaxValue) continue;
                // universal constants (verified across specimens): track refs = TMTR id + 6, entry refs = C-node id + 2
                const int dt = 6;
                const int bestDe = 2;
                // walk TMAC -> TMTR -> C018
                foreach (var nd in nodes)
                {
                    if (nd.tag != "TMAC") continue;
                    var hraw = U32(nd.pos + 0x10);
                    if ((hraw >> 24) != 0xFF) continue;
                    var h = (int)(hraw & 0xFFFFFF);
                    var tmacTime = U32(nd.pos + 0xC);
                    var tg = nd.pos + 0x14 + (int)U32(nd.pos + 0x14);
                    var nn = (int)U32(nd.pos + 0x18);
                    for (var i2 = 0; i2 < nn && tg + 2 * i2 + 2 <= d.Length; i2++)
                    {
                        if (!byid.TryGetValue(U16(tg + 2 * i2) - dt, out var tr) || tr.tag != "TMTR") continue;
                        var eg = tr.pos + 0xC + (int)U32(tr.pos + 0xC);
                        var en = (int)U32(tr.pos + 0x10);
                        for (var j = 0; j < en && eg + 2 * j + 2 <= d.Length; j++)
                        {
                            if (!byid.TryGetValue(U16(eg + 2 * j) - bestDe, out var et) || et.tag != "C018") continue;
                            if (et.pos + 0x38 > d.Length) continue;
                            float x = F32(et.pos + 0x14), y = F32(et.pos + 0x18), z = F32(et.pos + 0x1C);
                            float rx = F32(et.pos + 0x20), ry = F32(et.pos + 0x24), rz = F32(et.pos + 0x28);
                            float sx = F32(et.pos + 0x2C), sy = F32(et.pos + 0x30), sz2 = F32(et.pos + 0x34);
                            var kind = ctalKind.TryGetValue(h, out var kk) ? kk : "";
                            var path = ctalPath.TryGetValue(h, out var pp2) ? pp2 : "";
                            var act = ctalActor.TryGetValue(h, out var aa) ? aa.ToString() : "";
                            var match = 0;
                            if (ctalPos.TryGetValue(h, out var cp))
                                if (Math.Abs(cp.Item1 - x) < 0.01f && Math.Abs(cp.Item2 - y) < 0.01f && Math.Abs(cp.Item3 - z) < 0.01f) { match = 1; matchown++; }
                            if (kind != "") boundC++;
                            w.WriteLine(FormattableString.Invariant($"{rowId},{p},{tmlbIdx},{h},{kind},{path},{act},{tmacTime},{U16(et.pos + 8)},{x:0.###},{y:0.###},{z:0.###},{rx:0.####},{ry:0.####},{rz:0.####},{sx:0.###},{sy:0.###},{sz2:0.###},{match}"));
                            rows++;
                        }
                    }
                }
            }
        }
        w.Flush();
        log?.Invoke($"scanned={scanned} withTmlb={withTmlb} rows={rows} boundToCtal={boundC} posMatchesOwnCtal={matchown}");
        return (scanned, withTmlb, rows);
    }
}
